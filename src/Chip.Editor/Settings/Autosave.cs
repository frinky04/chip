using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chip.Core.IO;
using Chip.Core.Scene;

namespace Chip.Editor.Settings;

/// <summary>What an autosave on disk says about itself (written next to it as <c>key.autosave.json</c>).</summary>
public sealed record AutosaveInfo(string Key, string? OriginalPath, DateTime SavedUtc, int Pid, DateTime ProcessStartUtc)
{
    public string DisplayName => OriginalPath != null ? System.IO.Path.GetFileName(OriginalPath) : "an untitled level";
}

public enum RecoveryDecision
{
    /// <summary>The autosave holds work the file doesn't: ask the user.</summary>
    Offer,
    /// <summary>The file is as new as the autosave (it was saved later): delete the autosave quietly.</summary>
    Stale,
}

/// <summary>
/// Autosave and crash recovery. While the document has unsaved changes, it is written to
/// <c>%APPDATA%/Chip/autosave/</c> at most every <see cref="IntervalSeconds"/> (only when it changed). Saving, closing
/// normally or discarding removes the autosave, so anything left at startup came from a crash or a forced kill.
/// Writes are atomic (<see cref="AtomicFile"/>): the .chip lands first, then its info file.
/// </summary>
public sealed class AutosaveManager
{
    const string InfoSuffix = ".autosave.json";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    readonly Func<int> _interval;
    string? _untitledKey;
    string? _writtenKey;
    int _writtenVersion = -1;
    double? _pendingSince;
    bool _reportedFailure;

    /// <param name="folder">Autosave folder; null disables autosave (tests, headless captures).</param>
    /// <param name="intervalSeconds">Read every tick so Preferences apply at once; 0 or less = off.</param>
    public AutosaveManager(string? folder, Func<int> intervalSeconds)
    {
        Folder = folder;
        _interval = intervalSeconds;
    }

    public string? Folder { get; }
    public int IntervalSeconds => _interval();
    /// <summary>Called with a message when an autosave can't be written (once until a write succeeds again).</summary>
    public Action<string>? OnError { get; set; }
    /// <summary>Key of the autosave currently on disk for this session, if any.</summary>
    public string? CurrentKey => _writtenKey;

    /// <summary>Saved files with unsaved work: newer than the file, or the file is gone, or it was never saved.</summary>
    public static RecoveryDecision Decide(DateTime autosaveUtc, string? originalPath, DateTime? originalWriteUtc) =>
        originalPath == null || originalWriteUtc == null || autosaveUtc > originalWriteUtc ? RecoveryDecision.Offer : RecoveryDecision.Stale;

    /// <summary>Stable per file path ("level-1a2b3c4d"); untitled documents get one key per session.</summary>
    public string KeyFor(string? path)
    {
        if (path == null) return _untitledKey ??= $"untitled-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}";
        var full = System.IO.Path.GetFullPath(path).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..8].ToLowerInvariant();
        return $"{Sanitize(System.IO.Path.GetFileNameWithoutExtension(path))}-{hash}";
    }

    static string Sanitize(string name)
    {
        var bad = System.IO.Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => bad.Contains(c) || c == ' ' ? '_' : c).ToArray());
        return s.Length > 40 ? s[..40] : s.Length == 0 ? "level" : s;
    }

    public string ChipPath(string key) => System.IO.Path.Combine(Folder ?? "", key + ".chip");
    string InfoPath(string key) => System.IO.Path.Combine(Folder ?? "", key + InfoSuffix);

    /// <summary>Call once per frame with the time in seconds. Writes when changes have waited a full interval.</summary>
    public void Tick(Session session, double now)
    {
        if (Folder == null) return;
        string key = KeyFor(session.Path);
        if (_writtenKey != null && _writtenKey != key) DeleteCurrent(); // saved as / opened another file: that work is resolved
        if (!session.Dirty)
        {
            DeleteCurrent();
            _pendingSince = null;
            return;
        }
        if (session.InGesture || (_writtenKey == key && _writtenVersion == session.Version)) return;
        _pendingSince ??= now;
        int interval = IntervalSeconds;
        if (interval <= 0 || now - _pendingSince < interval) return;
        _pendingSince = null;
        if (Write(key, session.Path, session.Doc)) _writtenVersion = session.Version;
        else _pendingSince = now; // retry after another interval
    }

    bool Write(string key, string? originalPath, Document doc)
    {
        try
        {
            Directory.CreateDirectory(Folder!);
            AtomicFile.Write(ChipPath(key), Encoding.UTF8.GetBytes(DocumentSerializer.Serialize(doc)));
            using var self = Process.GetCurrentProcess();
            var info = new AutosaveInfo(key, originalPath == null ? null : System.IO.Path.GetFullPath(originalPath), DateTime.UtcNow,
                Environment.ProcessId, self.StartTime.ToUniversalTime());
            AtomicFile.Write(InfoPath(key), JsonSerializer.SerializeToUtf8Bytes(info, Json));
            _writtenKey = key;
            _reportedFailure = false;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!_reportedFailure) OnError?.Invoke($"Autosave failed: {e.Message} Check that {Folder} is writable, or save the level yourself.");
            _reportedFailure = true;
            return false;
        }
    }

    /// <summary>Removes this session's autosave (after a save, a discard, or a normal exit).</summary>
    public void DeleteCurrent()
    {
        if (_writtenKey == null) return;
        Delete(_writtenKey);
        _writtenKey = null;
        _writtenVersion = -1;
    }

    public void Delete(string key)
    {
        TryDelete(InfoPath(key));
        TryDelete(ChipPath(key));
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Autosaves worth offering at startup, newest first. Stale or broken ones are deleted; ones that belong to another
    /// running Chip are left alone. <paramref name="fileTime"/> defaults to the file's last write time (null if missing).
    /// </summary>
    public List<AutosaveInfo> FindRecoverable(Func<string, DateTime?>? fileTime = null)
    {
        var found = new List<AutosaveInfo>();
        if (Folder == null || !Directory.Exists(Folder)) return found;
        fileTime ??= p => File.Exists(p) ? File.GetLastWriteTimeUtc(p) : null;
        foreach (var infoPath in Directory.EnumerateFiles(Folder, "*" + InfoSuffix))
        {
            AutosaveInfo? info = null;
            try { info = JsonSerializer.Deserialize<AutosaveInfo>(File.ReadAllText(infoPath)); }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
            string key = System.IO.Path.GetFileName(infoPath)[..^InfoSuffix.Length];
            if (info == null || info.Key != key || !File.Exists(ChipPath(key))) { Delete(key); continue; }
            if (OwnedByRunningProcess(info)) continue;
            if (Decide(info.SavedUtc, info.OriginalPath, info.OriginalPath == null ? null : fileTime(info.OriginalPath)) == RecoveryDecision.Stale)
            {
                Delete(key);
                continue;
            }
            found.Add(info);
        }
        return found.OrderByDescending(i => i.SavedUtc).ToList();
    }

    static bool OwnedByRunningProcess(AutosaveInfo info)
    {
        if (info.Pid == Environment.ProcessId) return false;
        try
        {
            using var p = Process.GetProcessById(info.Pid);
            return Math.Abs((p.StartTime.ToUniversalTime() - info.ProcessStartUtc).TotalSeconds) < 2;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens the original file (if it still exists) and applies the autosave as one undo step, so the level reads as
    /// unsaved and Undo shows what is on disk. This session then owns the autosave until it is saved or discarded.
    /// </summary>
    public void Restore(AutosaveInfo info, Session session)
    {
        var recovered = DocumentSerializer.Load(ChipPath(info.Key));
        Document baseline;
        try { baseline = info.OriginalPath != null && File.Exists(info.OriginalPath) ? DocumentSerializer.Load(info.OriginalPath) : new Document(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { baseline = new Document(); }
        session.Reset(baseline, info.OriginalPath);
        session.Do("Restore Autosave", () => CopyInto(recovered, session.Doc));
        if (info.OriginalPath == null) _untitledKey = info.Key;
        _writtenKey = info.Key;
        _writtenVersion = session.Version;
    }

    /// <summary>Copies every public field and settable property, so schema additions are carried over without edits here.</summary>
    static void CopyInto(Document from, Document to)
    {
        foreach (var f in typeof(Document).GetFields(BindingFlags.Public | BindingFlags.Instance)) f.SetValue(to, f.GetValue(from));
        foreach (var p in typeof(Document).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p is { CanRead: true, CanWrite: true } && p.GetIndexParameters().Length == 0))
            p.SetValue(to, p.GetValue(from));
    }
}
