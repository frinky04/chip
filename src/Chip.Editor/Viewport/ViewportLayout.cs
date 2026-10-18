using System.Numerics;
using System.Runtime.CompilerServices;
using Chip.Editor.Framework;

namespace Chip.Editor.Viewport;

public enum LayoutKind { Single, Two, Four }

/// <summary>
/// The viewport's panes and which one is active. One per editor context (looked up with <see cref="Of"/>), so features
/// and the host share it without new context members. Activating a pane makes its camera <c>ctx.Camera</c>.
/// Defaults follow Hammer: perspective, then top, front and side.
/// </summary>
public sealed class ViewportLayout
{
    static readonly ConditionalWeakTable<EditorContext, ViewportLayout> Table = new();

    public static ViewportLayout Of(EditorContext ctx) => Table.GetValue(ctx, c => new ViewportLayout(c));

    readonly EditorContext _ctx;
    readonly ViewportPane[] _panes;
    readonly bool[] _needsFrame = [false, true, true, true];

    ViewportLayout(EditorContext ctx)
    {
        _ctx = ctx;
        _panes = [new ViewportPane(0, ctx.Camera), Ortho(1, Projection.Top), Ortho(2, Projection.Front), Ortho(3, Projection.Side)];
        Active = _panes[0];
    }

    static ViewportPane Ortho(int index, Projection p) => new(index, new Camera { Projection = p, Position = Vector3.Zero });

    public IReadOnlyList<ViewportPane> Panes => _panes;
    public LayoutKind Kind { get; private set; } = LayoutKind.Single;
    public ViewportPane Active { get; private set; }
    public CameraBookmarks Bookmarks { get; } = new();

    public static int PaneCount(LayoutKind k) => k switch { LayoutKind.Single => 1, LayoutKind.Two => 2, _ => 4 };

    public IEnumerable<ViewportPane> Visible => _panes.Take(PaneCount(Kind));

    public void SetKind(LayoutKind kind)
    {
        Kind = kind;
        if (Active.Index >= PaneCount(kind)) Activate(_panes[0]);
    }

    public void Activate(ViewportPane pane)
    {
        Active = pane;
        _ctx.Camera = pane.Camera;
    }

    /// <summary>Changes a pane's projection; a projection the pane hasn't shown before starts framed on the level.</summary>
    public void SetProjection(ViewportPane pane, Projection p)
    {
        if (pane.SetProjection(p) && p != Projection.Perspective) _needsFrame[pane.Index] = true;
    }

    /// <summary>Splits the area into the visible panes (separated by <paramref name="gap"/> pixels).</summary>
    public void Arrange(Vector2 origin, Vector2 size, float gap)
    {
        var rects = Rects(Kind, origin, size, gap);
        for (int i = 0; i < rects.Length; i++)
        {
            var pane = _panes[i];
            pane.Origin = rects[i].Origin;
            pane.Size = Vector2.Max(rects[i].Size, Vector2.One);
            if (_needsFrame[i])
            {
                _ = pane.View; // records the aspect so framing fits both width and height
                FrameScene(pane);
                _needsFrame[i] = false;
            }
        }
    }

    public static (Vector2 Origin, Vector2 Size)[] Rects(LayoutKind kind, Vector2 origin, Vector2 size, float gap)
    {
        float halfW = MathF.Floor((size.X - gap) * 0.5f), halfH = MathF.Floor((size.Y - gap) * 0.5f);
        float rightX = origin.X + halfW + gap, bottomY = origin.Y + halfH + gap;
        var right = new Vector2(size.X - halfW - gap, size.Y);
        return kind switch
        {
            LayoutKind.Single => [(origin, size)],
            LayoutKind.Two => [(origin, new Vector2(halfW, size.Y)), (new Vector2(rightX, origin.Y), right)],
            _ =>
            [
                (origin, new Vector2(halfW, halfH)),
                (new Vector2(rightX, origin.Y), new Vector2(right.X, halfH)),
                (new Vector2(origin.X, bottomY), new Vector2(halfW, size.Y - halfH - gap)),
                (new Vector2(rightX, bottomY), new Vector2(right.X, size.Y - halfH - gap)),
            ],
        };
    }

    public ViewportPane? PaneAt(Vector2 screen) => Visible.FirstOrDefault(p => p.Contains(screen));

    /// <summary>Frames all visible meshes in one pane.</summary>
    public void FrameScene(ViewportPane pane)
    {
        var visible = _ctx.Doc.Meshes.Where(m => !m.Hidden && m.Positions.Count > 0).ToList();
        if (visible.Count == 0) { pane.Camera.Frame(new Vector3(-8, -8, -8), new Vector3(8, 8, 8)); return; }
        pane.Camera.Frame(visible.Select(m => m.Bounds().Min).Aggregate(Vector3.Min), visible.Select(m => m.Bounds().Max).Aggregate(Vector3.Max));
    }

    /// <summary>Stores the active pane's camera in bookmark slot 1–9.</summary>
    public void StoreBookmark(int slot) => Bookmarks.Set(slot, Active.Camera.Capture());

    /// <summary>Restores bookmark slot 1–9 into the active pane (including its projection). False when the slot is empty.</summary>
    public bool RecallBookmark(int slot)
    {
        if (Bookmarks.Get(slot) is not { } s) return false;
        if (s.Projection != Active.Projection) Active.SetProjection(s.Projection);
        Active.Camera.Apply(s);
        return true;
    }
}
