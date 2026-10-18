using System.Numerics;
using Chip.Core.Picking;

namespace Chip.Editor.Viewport;

/// <summary>How a pane shades the scene.</summary>
public enum ViewMode { Lit, Unlit, Wireframe, UvChecker }

/// <summary>
/// One view into the level: its own camera, projection, shading mode and screen rectangle. Tools never see a pane;
/// the host copies the active pane's <see cref="View"/>, mouse and rectangle into the editor context.
/// </summary>
public sealed class ViewportPane
{
    readonly Dictionary<Projection, CameraState> _remembered = new();

    public ViewportPane(int index, Camera camera)
    {
        Index = index;
        Camera = camera;
    }

    public int Index { get; }
    public Camera Camera { get; }
    public ViewMode Mode = ViewMode.Lit;

    /// <summary>Top-left of the pane's image in screen pixels, and its size.</summary>
    public Vector2 Origin, Size = Vector2.One;

    public Projection Projection => Camera.Projection;
    public string Name => Camera.Label(Camera.Projection);
    public ViewInfo View => Camera.View(Size);

    public bool Contains(Vector2 screen) =>
        screen.X >= Origin.X && screen.Y >= Origin.Y && screen.X < Origin.X + Size.X && screen.Y < Origin.Y + Size.Y;

    /// <summary>
    /// Switches projection, remembering where the camera was in each one so switching back restores it.
    /// Returns true when the projection is new to this pane (the caller frames the scene).
    /// </summary>
    public bool SetProjection(Projection p)
    {
        if (p == Camera.Projection) return false;
        _remembered[Camera.Projection] = Camera.Capture();
        if (_remembered.TryGetValue(p, out var s)) { Camera.Apply(s); return false; }
        var current = Camera.Capture();
        Camera.Projection = p;
        if (p != Projection.Perspective)
        {
            // Keep looking at roughly the same place: the point the perspective camera was looking at.
            var target = Camera.FocusTarget ?? current.Position;
            Camera.Position = target - Camera.Forward * Vector3.Dot(target, Camera.Forward);
        }
        return true;
    }

    public static string ModeLabel(ViewMode m) => m switch
    {
        ViewMode.Unlit => "Unlit", ViewMode.Wireframe => "Wireframe", ViewMode.UvChecker => "UV checker", _ => "Lit",
    };
}
