using System.Numerics;
using Chip.Core.Picking;

namespace Chip.Editor.Viewport;

/// <summary>How a pane looks at the scene. Orthographic views look along a world axis (Hammer's 2D views).</summary>
public enum Projection { Perspective, Top, Front, Side }

/// <summary>Everything needed to put a camera back where it was (bookmarks, per-pane projection memory).</summary>
public readonly record struct CameraState(Projection Projection, Vector3 Position, float Yaw, float Pitch, float OrthoHeight);

/// <summary>
/// Fly/orbit camera in metres, perspective or orthographic. Yaw 0 looks down -Z; Y is up.
/// Orthographic: <see cref="Position"/> is the point at the centre of the view and <see cref="OrthoHeight"/> the visible height;
/// the eye sits <see cref="OrthoDepth"/> behind it so the whole level fits between the clip planes.
/// </summary>
public sealed class Camera
{
    public Vector3 Position = new(-6, 4, 6);
    public float Yaw, Pitch;
    public float FovY = 75f * MathF.PI / 180f;
    public float FlySpeed = 8f;
    public Projection Projection = Projection.Perspective;
    /// <summary>World metres visible from the top to the bottom of an orthographic view.</summary>
    public float OrthoHeight = 20f;
    /// <summary>Last point the camera was focused on; orbit falls back to it when nothing is under the cursor.</summary>
    public Vector3? FocusTarget;
    public const float Near = 0.02f, Far = 2000f, OrthoDepth = 1000f;
    public const float MinOrthoHeight = 0.25f, MaxOrthoHeight = 1500f;

    float _aspect = 16 / 9f;

    public Camera() => LookAt(Vector3.Zero);

    public bool IsOrtho => Projection != Projection.Perspective;

    /// <summary>View basis (forward, up) for each orthographic projection. Right = forward × up.</summary>
    public static (Vector3 Forward, Vector3 Up) OrthoAxes(Projection p) => p switch
    {
        Projection.Top => (-Vector3.UnitY, -Vector3.UnitZ), // looking down, -Z (forward) at the top of the screen
        Projection.Front => (-Vector3.UnitZ, Vector3.UnitY), // looking along -Z, X to the right
        _ => (-Vector3.UnitX, Vector3.UnitY),                // Side: looking along -X, -Z to the right
    };

    public Vector3 Forward => IsOrtho
        ? OrthoAxes(Projection).Forward
        : new(MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch), -MathF.Cos(Yaw) * MathF.Cos(Pitch));

    public Vector3 Up => IsOrtho ? OrthoAxes(Projection).Up : Vector3.Cross(Right, Forward);

    public Vector3 Right => IsOrtho
        ? Vector3.Cross(OrthoAxes(Projection).Forward, OrthoAxes(Projection).Up)
        : Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));

    /// <summary>Where rays and distances are measured from.</summary>
    public Vector3 Eye => IsOrtho ? Position - Forward * OrthoDepth : Position;

    public void LookAt(Vector3 target)
    {
        if (IsOrtho) { Position = target; return; }
        var d = target - Position;
        if (d.LengthSquared() < 1e-10f) return;
        d = Vector3.Normalize(d);
        Pitch = MathF.Asin(Math.Clamp(d.Y, -1, 1));
        Yaw = MathF.Atan2(d.X, -d.Z);
    }

    public void Rotate(float dYaw, float dPitch)
    {
        if (IsOrtho) return;
        Yaw += dYaw;
        Pitch = Math.Clamp(Pitch + dPitch, -1.55f, 1.55f);
    }

    public void Orbit(Vector3 pivot, float dYaw, float dPitch)
    {
        if (IsOrtho) return;
        float r = Vector3.Distance(Position, pivot);
        Rotate(dYaw, dPitch);
        Position = pivot - Forward * r;
    }

    /// <summary>Frames a bounding box so it fills the view.</summary>
    public void Frame(Vector3 min, Vector3 max)
    {
        var center = (min + max) * 0.5f;
        FocusTarget = center;
        if (IsOrtho)
        {
            var ext = max - min;
            float w = MathF.Abs(Vector3.Dot(ext, Right)), h = MathF.Abs(Vector3.Dot(ext, Up));
            OrthoHeight = Math.Clamp(MathF.Max(h, w / _aspect) * 1.2f, 2f, MaxOrthoHeight);
            Position = InPlane(center);
            return;
        }
        float radius = MathF.Max(Vector3.Distance(min, max) * 0.5f, 0.5f);
        Position = center - Forward * (radius / MathF.Tan(FovY * 0.5f) * 1.1f);
    }

    /// <summary>
    /// Centres the view on a point. Perspective keeps the current distance (clamped so the point stays workable);
    /// orthographic keeps the zoom.
    /// </summary>
    public void FocusOn(Vector3 point)
    {
        FocusTarget = point;
        if (IsOrtho) { Position = InPlane(point); return; }
        float dist = Math.Clamp(Vector3.Distance(Position, point), 2f, 12f);
        Position = point - Forward * dist;
    }

    /// <summary>Orthographic zoom keeping <paramref name="anchor"/> (a point on screen) fixed. factor &lt; 1 zooms in.</summary>
    public void Zoom(float factor, Vector3 anchor)
    {
        if (!IsOrtho) return;
        float h = Math.Clamp(OrthoHeight * factor, MinOrthoHeight, MaxOrthoHeight);
        float k = h / OrthoHeight;
        var a = InPlane(anchor);
        Position = a + (Position - a) * k;
        OrthoHeight = h;
    }

    /// <summary>Moves the view by a screen-space mouse delta (pixels) so the content follows the cursor.</summary>
    public void Pan(Vector2 deltaPx, float unitsPerPixel) => Position += (-Right * deltaPx.X + Up * deltaPx.Y) * unitsPerPixel;

    /// <summary>Projects a point onto the plane through the origin facing the camera (ortho views keep depth at 0).</summary>
    Vector3 InPlane(Vector3 p) => p - Forward * Vector3.Dot(p, Forward);

    public ViewInfo View(Vector2 size)
    {
        float w = MathF.Max(size.X, 1), h = MathF.Max(size.Y, 1);
        _aspect = w / h;
        var eye = Eye;
        var view = Matrix4x4.CreateLookAt(eye, eye + Forward, Up);
        var proj = IsOrtho ? GlOrthographic(OrthoHeight * _aspect, OrthoHeight) : GlPerspective(_aspect);
        return new ViewInfo(view * proj, size, eye);
    }

    /// <summary>OpenGL clip convention (z in [-w, w]) so depth uses the full buffer range.</summary>
    Matrix4x4 GlPerspective(float aspect)
    {
        float f = 1 / MathF.Tan(FovY * 0.5f);
        return new Matrix4x4(
            f / aspect, 0, 0, 0,
            0, f, 0, 0,
            0, 0, (Far + Near) / (Near - Far), -1,
            0, 0, 2 * Far * Near / (Near - Far), 0);
    }

    static Matrix4x4 GlOrthographic(float width, float height) => new(
        2 / width, 0, 0, 0,
        0, 2 / height, 0, 0,
        0, 0, -2 / (Far - Near), 0,
        0, 0, -(Far + Near) / (Far - Near), 1);

    /// <summary>World units covered by one pixel at <paramref name="distance"/> (any distance in orthographic views).</summary>
    public float UnitsPerPixel(float distance, float viewportHeight) => IsOrtho
        ? OrthoHeight / MathF.Max(viewportHeight, 1)
        : 2 * distance * MathF.Tan(FovY * 0.5f) / MathF.Max(viewportHeight, 1);

    public CameraState Capture() => new(Projection, Position, Yaw, Pitch, OrthoHeight);

    public void Apply(CameraState s)
    {
        Projection = s.Projection;
        Position = s.Position;
        Yaw = s.Yaw;
        Pitch = Math.Clamp(s.Pitch, -1.55f, 1.55f);
        OrthoHeight = Math.Clamp(s.OrthoHeight, MinOrthoHeight, MaxOrthoHeight);
    }

    public static string Label(Projection p) => p switch
    {
        Projection.Top => "Top", Projection.Front => "Front", Projection.Side => "Side", _ => "Perspective",
    };
}
