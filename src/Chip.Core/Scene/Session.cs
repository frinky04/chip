using Chip.Core.Geometry;

namespace Chip.Core.Scene;

/// <summary>
/// The only way the editor changes a document. Every committed gesture is one undo step:
/// <see cref="Begin"/> snapshots, live edits call <see cref="Touch"/>, then <see cref="End"/> commits or <see cref="Cancel"/> restores.
/// <see cref="Version"/> changes whenever geometry may have changed, so views can rebuild lazily.
/// </summary>
public sealed class Session
{
    const int MaxUndo = 256;

    sealed record Snapshot(string Label, Document? Doc, Selection Sel, UndoDelta? Delta = null, IReadOnlyList<FileChange>? Files = null);
    sealed record ScopeCheck(PolyMesh Mesh, ulong Hash);

    readonly List<Snapshot> _undo = new();
    readonly List<Snapshot> _redo = new();
    Snapshot? _pending;
    List<FileChange>? _pendingFiles;
    List<ScopeCheck>? _scopeChecks;

    /// <summary>Where <see cref="WriteFile"/> reads and writes. Files written in a step (dressing rules) undo and redo with it.</summary>
    public ISessionFiles Files { get; init; } = DiskSessionFiles.Instance;

    /// <summary>Raised after a step, undo, redo or cancel rewrote files (their paths), so watchers can reload them at once.</summary>
    public event Action<IReadOnlyList<string>>? FilesChanged;

    /// <summary>Raised when undo, redo or cancel couldn't rewrite a file (the path and the reason). The document still steps.</summary>
    public event Action<string, string>? FileError;

    /// <summary>Reference implementation for undo equivalence tests and baseline measurements.</summary>
    public bool FullSnapshotUndo { get; init; }

    /// <summary>Check that scoped callers did not mutate other meshes, including removed ones. On by default in DEBUG.</summary>
    public bool ValidateUndoScopes { get; init; } =
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>
    /// Receives scope violations instead of an exception. Tests leave it unset so a mis-scoped caller fails loudly;
    /// the editor reports it, because throwing mid-frame would take unsaved work down with it.
    /// </summary>
    public Action<string>? ScopeViolation { get; set; }

    public Document Doc { get; private set; } = new();
    public Selection Sel { get; private set; } = new();
    public string? Path { get; private set; }
    public bool Dirty { get; private set; }
    public int Version { get; private set; }
    public bool InGesture => _pending != null;
    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;
    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;
    /// <summary>
    /// Texture lock for object moves: projected textures follow rigidly moved objects (default). Off, they stay
    /// world-aligned and slide. An editor setting, not document state; explicit UVs always stay on the geometry.
    /// </summary>
    public bool TextureLock { get; set; } = true;

    /// <summary>Prefabs the last <see cref="Reset"/> couldn't read or find (one message each), for the editor to report.</summary>
    public IReadOnlyList<string> PrefabProblems { get; private set; } = [];

    /// <summary>Counts <see cref="Reset"/>s, so watchers can tell a level being opened from an undo swapping documents.</summary>
    public int Resets { get; private set; }

    /// <summary>
    /// Starts over with <paramref name="doc"/> (a level just opened, or a new one). Its prefab copies are first brought
    /// up to date with their files (<see cref="Prefabs.Refresh"/>), so a prefab edited elsewhere shows as it is now.
    /// </summary>
    public void Reset(Document doc, string? path)
    {
        Resets++;
        PrefabProblems = [];
        if (doc.Definitions.Any(d => d.Source != null) && Prefabs.RootFor(path) is { } root)
            PrefabProblems = Prefabs.Refresh(doc, root).Where(r => r.Message != null).Select(r => r.Message!).ToList();
        Doc = doc;
        Sel = new Selection();
        Path = path;
        Dirty = false;
        _undo.Clear();
        _redo.Clear();
        _pending = null;
        _pendingFiles = null;
        _scopeChecks = null;
        Version++;
        RefreshInstances();
    }

    /// <summary>Raised by <see cref="MarkSaved"/>, i.e. after every successful save, with the saved path (watch mode hooks this).</summary>
    public event Action<string>? Saved;

    public void MarkSaved(string path) { Path = path; Dirty = false; Saved?.Invoke(path); }

    public void Begin(string label) => BeginCore(label, null);

    /// <summary>
    /// Snapshots only the given existing meshes. All document structure, entities, groups and settings are covered.
    /// Include every existing mesh whose contents may change; additions, removals and reordering are supported.
    /// Use the unscoped overload when the affected meshes are unknown.
    /// </summary>
    public void Begin(string label, IEnumerable<int> meshIds) => BeginCore(label, meshIds.ToHashSet());

    void BeginCore(string label, HashSet<int>? meshIds)
    {
        if (_pending != null) End();
        if (FullSnapshotUndo) meshIds = null;
        _scopeChecks = meshIds != null && ValidateUndoScopes
            ? Doc.Meshes.Where(m => !meshIds.Contains(m.Id)).Select(m => new ScopeCheck(m, ScopeHash(m))).ToList()
            : null;
        _pending = new Snapshot(label, Doc.Clone(meshIds), Sel.Clone());
        _pendingFiles = null;
    }

    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/> (null deletes it) as part of the current step, so undo puts
    /// the old text back. Only inside a step (<see cref="Do(string, Action)"/> or <see cref="Begin(string)"/>). Throws
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the file can't be written, which rolls
    /// a <see cref="Do(string, Action)"/> back.
    /// </summary>
    public void WriteFile(string path, string? text)
    {
        if (_pending == null) throw new InvalidOperationException("WriteFile is only allowed inside an undo step");
        _pendingFiles ??= new();
        int i = _pendingFiles.FindIndex(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        string? before = i >= 0 ? _pendingFiles[i].Before : Files.Read(path);
        Files.Write(path, text);
        if (i >= 0) _pendingFiles[i] = _pendingFiles[i] with { After = text };
        else _pendingFiles.Add(new FileChange(path, before, text));
    }

    /// <summary>Puts each file's <see cref="FileChange.Before"/> text back, newest first, reporting files it couldn't write.</summary>
    void Restore(IReadOnlyList<FileChange> files)
    {
        for (int i = files.Count - 1; i >= 0; i--)
        {
            try { Files.Write(files[i].Path, files[i].Before); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { FileError?.Invoke(files[i].Path, e.Message); }
        }
        FilesChanged?.Invoke(files.Select(f => f.Path).ToList());
    }

    /// <summary>Marks a live change (a drag frame). Linked copies follow the one being edited as it moves.</summary>
    public void Touch()
    {
        Version++;
        SyncInstances(); // bumps Version again when there are copies, which only means "maybe changed"
    }

    readonly InstanceSync _instances = new();

    /// <summary>
    /// Carries an edit of one linked copy to the others (<see cref="InstanceSync"/>). The meshes it rebuilds are new
    /// objects, so the pending snapshot keeps the old ones; they are exempt from the scope check.
    /// </summary>
    void SyncInstances()
    {
        if (Doc.Definitions.Count == 0) return;
        Version++; // the edit may not have touched yet, and mesh revisions are recomputed per version
        var touched = _instances.Run(Doc, MeshRevision);
        if (touched.Count == 0) return;
        var set = touched.ToHashSet();
        _scopeChecks?.RemoveAll(c => set.Contains(c.Mesh.Id));
        Version++;
        _instances.Refresh(Doc, MeshRevision);
    }

    /// <summary>The folder prefab references are relative to (<see cref="Prefabs.RootFor"/>); null for an unsaved level.</summary>
    public string? PrefabRoot => Prefabs.RootFor(Path);

    /// <summary>
    /// A step that changed a prefab's parts (an edit of one of its copies) writes the prefab file, as part of the step,
    /// so undo puts the file back too. A file that already holds those parts (it was just read) is left alone.
    /// </summary>
    void WritePrefabs()
    {
        if (_pending?.Doc is not { } before || ReferenceEquals(before.Definitions, Doc.Definitions) || PrefabRoot is not { } root) return;
        foreach (var def in Doc.Definitions)
        {
            if (def.Source is not { } src || before.FindDefinition(def.Id) is not { Source: not null } old || ReferenceEquals(old.Parts, def.Parts)) continue;
            var path = Prefabs.FullPath(root, src);
            try
            {
                var text = Files.Read(path);
                if (text != null && IsSameParts(text, def)) continue;
                WriteFile(path, Prefabs.Write(def, text));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { FileError?.Invoke(path, e.Message); }
        }

        static bool IsSameParts(string text, InstanceDefinition def)
        {
            try { return InstanceSync.Same(Prefabs.Parts(IO.DocumentSerializer.Deserialize(text)), def.Parts); }
            catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException) { return false; }
        }
    }

    void RefreshInstances()
    {
        if (Doc.Definitions.Count > 0) _instances.Refresh(Doc, MeshRevision);
    }

    public void End()
    {
        if (_pending == null) return;
        SyncInstances();
        InstanceOps.PruneDefinitions(Doc);
        WritePrefabs();
        CheckScope();
        PruneGroupMembers(); // before the delta, so the cleanup undoes with the step that caused it
        var files = _pendingFiles?.Where(f => f.Before != f.After).ToList();
        var step = _pending with { Files = files is { Count: > 0 } ? files : null };
        _undo.Add(FullSnapshotUndo ? step : step with { Doc = null, Delta = UndoDelta.Between(_pending.Doc!, Doc) });
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
        _pending = null;
        _pendingFiles = null;
        _scopeChecks = null;
        Sel.Prune(Doc);
        Dirty = true;
        Version++;
        if (step.Files != null) FilesChanged?.Invoke(step.Files.Select(f => f.Path).ToList());
    }

    /// <summary>Drops group members whose object no longer exists (merged meshes, meshes emptied by deleting every face).</summary>
    void PruneGroupMembers()
    {
        if (Doc.Groups.Count == 0) return;
        var live = new HashSet<int>(Doc.Meshes.Count + Doc.Entities.Count);
        foreach (var m in Doc.Meshes) live.Add(m.Id);
        foreach (var e in Doc.Entities) live.Add(e.Id);
        foreach (var g in Doc.Groups) g.Members.RemoveAll(id => !live.Contains(id));
    }

    public void Cancel()
    {
        if (_pending == null) return;
        CheckScope();
        Doc = _pending.Doc!;
        Sel = _pending.Sel;
        _pending = null;
        var files = _pendingFiles;
        _pendingFiles = null;
        _scopeChecks = null;
        Version++;
        RefreshInstances();
        if (files != null) Restore(files);
    }

    /// <summary>Runs one atomic edit. Exceptions roll the document back.</summary>
    public void Do(string label, Action edit)
    {
        Begin(label);
        try { edit(); End(); }
        catch { Cancel(); throw; }
    }

    /// <summary>Runs an atomic edit with a known mesh scope. See <see cref="Begin(string, IEnumerable{int})"/>.</summary>
    public void Do(string label, IEnumerable<int> meshIds, Action edit)
    {
        Begin(label, meshIds);
        try { edit(); End(); }
        catch { Cancel(); throw; }
    }

    public bool Undo() => Step(_undo, _redo);
    public bool Redo() => Step(_redo, _undo);

    bool Step(List<Snapshot> from, List<Snapshot> to)
    {
        if (_pending != null || from.Count == 0) return false;
        var s = from[^1];
        from.RemoveAt(from.Count - 1);
        var files = s.Files?.Select(f => new FileChange(f.Path, f.After, f.Before)).ToList(); // the step back the other way
        if (s.Delta is { } delta)
        {
            var (doc, reverse) = delta.Swap(Doc);
            to.Add(new Snapshot(s.Label, null, Sel, reverse, files));
            Doc = doc;
        }
        else
        {
            to.Add(new Snapshot(s.Label, Doc, Sel, Files: files));
            Doc = s.Doc!;
        }
        if (s.Files != null) Restore(s.Files);
        Sel = s.Sel;
        Dirty = true;
        Version++;
        RefreshInstances();
        return true;
    }

    void CheckScope()
    {
        if (_scopeChecks == null) return;
        var live = Doc.Meshes.ToDictionary(m => m.Id);
        foreach (var check in _scopeChecks)
            if (ScopeHash(check.Mesh) != check.Hash ||
                (live.TryGetValue(check.Mesh.Id, out var mesh) && !ReferenceEquals(mesh, check.Mesh) && ScopeHash(mesh) != check.Hash))
            {
                string message = $"Undo scope violation in '{_pending?.Label}': mesh {check.Mesh.Id} changed outside the declared scope.";
                if (ScopeViolation == null) throw new InvalidOperationException(message);
                ScopeViolation(message);
                return;
            }
    }

    static ulong ScopeHash(PolyMesh mesh)
    {
        ulong hash = MeshHash.Of(mesh);
        hash = (hash ^ (uint)HashCode.Combine(mesh.Id, mesh.Name, mesh.Hidden, mesh.Locked, mesh.Collision)) * 1099511628211UL;
        foreach (var face in mesh.Faces) hash = (hash ^ (uint)StringComparer.Ordinal.GetHashCode(face.Material)) * 1099511628211UL;
        return hash;
    }

    // ---- Per-mesh change tracking ----

    readonly Dictionary<int, (ulong Hash, int Revision, PolyMesh Mesh)> _revisions = new();
    int _revisionsVersion = -1, _nextRevision;
    Document? _revisionsDoc;

    /// <summary>
    /// A number that changes whenever <paramref name="mesh"/>'s content does (anything <see cref="MeshHash"/> covers),
    /// including across undo, redo and cancel, which swap in other mesh objects with the same ids. Per-mesh caches
    /// (GPU buffers, picking structures) key on it. Returns -1 for meshes not in <see cref="Doc"/>.
    /// The first call after <see cref="Version"/> changes rehashes every mesh, in parallel.
    /// </summary>
    public int MeshRevision(PolyMesh mesh)
    {
        SyncRevisions();
        return _revisions.TryGetValue(mesh.Id, out var r) && ReferenceEquals(r.Mesh, mesh) ? r.Revision : -1;
    }

    void SyncRevisions()
    {
        if (_revisionsVersion == Version && ReferenceEquals(_revisionsDoc, Doc)) return;
        var meshes = Doc.Meshes;
        var hashes = new ulong[meshes.Count];
        Parallel.For(0, meshes.Count, i => hashes[i] = MeshHash.Of(meshes[i]));
        var live = new HashSet<int>();
        for (int i = 0; i < meshes.Count; i++)
        {
            int id = meshes[i].Id;
            live.Add(id);
            _revisions[id] = _revisions.TryGetValue(id, out var r) && r.Hash == hashes[i]
                ? r with { Mesh = meshes[i] }
                : (hashes[i], ++_nextRevision, meshes[i]);
        }
        foreach (var id in _revisions.Keys.Where(id => !live.Contains(id)).ToList()) _revisions.Remove(id);
        _revisionsVersion = Version;
        _revisionsDoc = Doc;
    }
}
