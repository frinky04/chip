using Chip.Core.Scene;

namespace Chip.Editor.Rendering;

/// <summary>Exact invalidation for read-only frame data. Selection is mutable even without a session edit.</summary>
public sealed class FrameCache<T>(bool selection = true)
{
    Session? _session;
    Document? _doc;
    int _version = -1;
    SelectMode _mode;
    ElementRef? _active;
    readonly HashSet<ElementRef> _items = new();
    public T Value { get; private set; } = default!;

    public bool IsCurrent(Session session) => ReferenceEquals(session, _session) && ReferenceEquals(session.Doc, _doc)
        && session.Version == _version && (!selection || (session.Sel.Mode == _mode && session.Sel.Active == _active && _items.SetEquals(session.Sel.Items)));

    public void Store(Session session, T value)
    {
        _session = session; _doc = session.Doc; _version = session.Version;
        if (selection)
        {
            _mode = session.Sel.Mode; _active = session.Sel.Active;
            _items.Clear(); _items.UnionWith(session.Sel.Items);
        }
        Value = value;
    }
}
