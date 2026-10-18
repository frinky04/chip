using Chip.Core.Geometry;

namespace Chip.Core.Scene;

/// <summary>
/// The only way the editor changes a document. Every committed gesture is one snapshot undo step:
/// <see cref="Begin"/> snapshots, live edits call <see cref="Touch"/>, then <see cref="End"/> commits or <see cref="Cancel"/> restores.
/// <see cref="Version"/> changes whenever geometry may have changed, so views can rebuild lazily.
/// </summary>
public sealed class Session
{
    const int MaxUndo = 256;

    sealed record Snapshot(string Label, Document Doc, Selection Sel);

    readonly List<Snapshot> _undo = new();
    readonly List<Snapshot> _redo = new();
    Snapshot? _pending;

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

    public void Reset(Document doc, string? path)
    {
        Doc = doc;
        Sel = new Selection();
        Path = path;
        Dirty = false;
        _undo.Clear();
        _redo.Clear();
        _pending = null;
        Version++;
    }

    /// <summary>Raised by <see cref="MarkSaved"/>, i.e. after every successful save, with the saved path (watch mode hooks this).</summary>
    public event Action<string>? Saved;

    public void MarkSaved(string path) { Path = path; Dirty = false; Saved?.Invoke(path); }

    public void Begin(string label)
    {
        if (_pending != null) End();
        _pending = new Snapshot(label, Doc.Clone(), Sel.Clone());
    }

    public void Touch() => Version++;

    public void End()
    {
        if (_pending == null) return;
        _undo.Add(_pending);
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
        _pending = null;
        Sel.Prune(Doc);
        Dirty = true;
        Version++;
    }

    public void Cancel()
    {
        if (_pending == null) return;
        Doc = _pending.Doc;
        Sel = _pending.Sel;
        _pending = null;
        Version++;
    }

    /// <summary>Runs one atomic edit. Exceptions roll the document back.</summary>
    public void Do(string label, Action edit)
    {
        Begin(label);
        try { edit(); }
        catch { Cancel(); throw; }
        End();
    }

    public bool Undo() => Step(_undo, _redo);
    public bool Redo() => Step(_redo, _undo);

    bool Step(List<Snapshot> from, List<Snapshot> to)
    {
        if (_pending != null || from.Count == 0) return false;
        var s = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(new Snapshot(s.Label, Doc, Sel));
        Doc = s.Doc;
        Sel = s.Sel;
        Dirty = true;
        Version++;
        return true;
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
