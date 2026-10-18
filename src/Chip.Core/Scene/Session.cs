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

    public void MarkSaved(string path) { Path = path; Dirty = false; }

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
}
