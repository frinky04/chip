using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Settings;

namespace Chip.Editor.Tests;

public sealed class AutosaveTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "chip-autosave-" + Guid.NewGuid().ToString("N"));
    string AutosaveDir => Path.Combine(_dir, "autosave");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    static readonly DateTime T0 = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Decision_offers_newer_missing_and_untitled()
    {
        Assert.Equal(RecoveryDecision.Offer, AutosaveManager.Decide(T0, @"C:\l.chip", T0.AddMinutes(-1)));  // autosave newer
        Assert.Equal(RecoveryDecision.Stale, AutosaveManager.Decide(T0, @"C:\l.chip", T0.AddMinutes(1)));   // file saved later
        Assert.Equal(RecoveryDecision.Stale, AutosaveManager.Decide(T0, @"C:\l.chip", T0));                 // same moment
        Assert.Equal(RecoveryDecision.Offer, AutosaveManager.Decide(T0, @"C:\l.chip", null));               // file gone
        Assert.Equal(RecoveryDecision.Offer, AutosaveManager.Decide(T0, null, null));                      // never saved
    }

    static void AddBox(Session s) =>
        s.Do("Add", () => s.Doc.Add(MeshOps.CreateBox(Vector3.Zero, Vector3.One, "dev_a", TextureMapping.Default)));

    AutosaveManager Manager(int interval = 30) => new(AutosaveDir, () => interval);

    [Fact]
    public void Writes_only_after_the_interval_and_only_when_changed()
    {
        var m = Manager();
        var s = new Session();
        s.Reset(new Document(), null);
        m.Tick(s, 0);
        Assert.False(Directory.Exists(AutosaveDir)); // nothing unsaved

        AddBox(s);
        m.Tick(s, 1);
        m.Tick(s, 20);
        Assert.Null(m.CurrentKey);                   // waiting for the interval
        m.Tick(s, 31.5);
        Assert.NotNull(m.CurrentKey);
        var chip = m.ChipPath(m.CurrentKey!);
        Assert.True(File.Exists(chip));
        Assert.Single(DocumentSerializer.Load(chip).Meshes);

        var written = File.GetLastWriteTimeUtc(chip);
        m.Tick(s, 100);                              // unchanged: no rewrite
        Assert.Equal(written, File.GetLastWriteTimeUtc(chip));
        Assert.Empty(Directory.GetFiles(AutosaveDir, "*.tmp"));
    }

    [Fact]
    public void Saving_or_resetting_removes_the_autosave()
    {
        var m = Manager(interval: 10);
        var s = new Session();
        var path = Path.Combine(_dir, "level.chip");
        Directory.CreateDirectory(_dir);
        DocumentSerializer.Save(new Document(), path);
        s.Reset(DocumentSerializer.Load(path), path);
        AddBox(s);
        m.Tick(s, 0);
        m.Tick(s, 11);
        var key = m.CurrentKey;
        Assert.NotNull(key);
        Assert.StartsWith("level-", key);

        DocumentSerializer.Save(s.Doc, path);
        s.MarkSaved(path);
        m.Tick(s, 12);
        Assert.Null(m.CurrentKey);
        Assert.False(File.Exists(m.ChipPath(key!)));
        Assert.Empty(Directory.GetFiles(AutosaveDir));
    }

    [Fact]
    public void Off_means_never_written()
    {
        var m = Manager(interval: 0);
        var s = new Session();
        s.Reset(new Document(), null);
        AddBox(s);
        m.Tick(s, 0);
        m.Tick(s, 10_000);
        Assert.Null(m.CurrentKey);
    }

    [Fact]
    public void Crash_leaves_a_recoverable_autosave_that_restores_as_unsaved()
    {
        var path = Path.Combine(_dir, "level.chip");
        Directory.CreateDirectory(_dir);
        DocumentSerializer.Save(new Document(), path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10));

        // Session 1 edits, autosaves, then "crashes" (no DeleteCurrent).
        var crashed = Manager(interval: 5);
        var s1 = new Session();
        s1.Reset(DocumentSerializer.Load(path), path);
        AddBox(s1);
        AddBox(s1);
        crashed.Tick(s1, 0);
        crashed.Tick(s1, 6);

        // Session 2 starts: the autosave is newer than the file, so it is offered.
        var m = Manager();
        var found = Assert.Single(m.FindRecoverable());
        Assert.Equal(Path.GetFullPath(path), found.OriginalPath);

        var s2 = new Session();
        s2.Reset(new Document(), null);
        m.Restore(found, s2);
        Assert.Equal(2, s2.Doc.Meshes.Count);
        Assert.Equal(Path.GetFullPath(path), s2.Path);
        Assert.True(s2.Dirty);
        Assert.True(s2.Undo());                       // undo shows the file as saved
        Assert.Empty(s2.Doc.Meshes);
        Assert.Equal(found.Key, m.CurrentKey);       // this session now owns it
    }

    [Fact]
    public void Stale_and_broken_autosaves_are_cleaned_up()
    {
        var path = Path.Combine(_dir, "level.chip");
        Directory.CreateDirectory(_dir);
        DocumentSerializer.Save(new Document(), path);
        var crashed = Manager(interval: 5);
        var s = new Session();
        s.Reset(DocumentSerializer.Load(path), path);
        AddBox(s);
        crashed.Tick(s, 0);
        crashed.Tick(s, 6);
        File.WriteAllText(Path.Combine(AutosaveDir, "junk.autosave.json"), "{ broken");

        var m = Manager();
        Assert.Empty(m.FindRecoverable(_ => DateTime.UtcNow.AddMinutes(5))); // file saved after the autosave
        Assert.Empty(Directory.GetFiles(AutosaveDir));
    }

    [Fact]
    public void Untitled_autosave_is_offered_when_nothing_else_exists()
    {
        var crashed = Manager(interval: 5);
        var s = new Session();
        s.Reset(new Document(), null);
        AddBox(s);
        crashed.Tick(s, 0);
        crashed.Tick(s, 6);

        var found = Assert.Single(Manager().FindRecoverable());
        Assert.Null(found.OriginalPath);
        Assert.StartsWith("untitled-", found.Key);
    }
}
