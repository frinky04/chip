using System.Numerics;
using Chip.Core.Picking;

namespace Chip.Editor.Viewport;

/// <summary>Perspective fly/orbit camera in metres. Yaw 0 looks down -Z; Y is up.</summary>
public sealed class Camera
{
    public Vector3 Position = new(-6, 4, 6);
    public float Yaw, Pitch;
    public float FovY = 75f * MathF.PI / 180f;
    public float FlySpeed = 8f;
    public const float Near = 0.02f, Far = 2000f;

    public Camera() => LookAt(Vector3.Zero);

    public Vector3 Forward => new(MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch), -MathF.Cos(Yaw) * MathF.Cos(Pitch));
    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));
    public Vector3 Up => Vector3.Cross(Right, Forward);

    public void LookAt(Vector3 target)
    {
        var d = Vector3.Normalize(target - Position);
        Pitch = MathF.Asin(Math.Clamp(d.Y, -1, 1));
        Yaw = MathF.Atan2(d.X, -d.Z);
    }

    public void Rotate(float dYaw, float dPitch)
    {
        Yaw += dYaw;
        Pitch = Math.Clamp(Pitch + dPitch, -1.55f, 1.55f);
    }

    public void Orbit(Vector3 pivot, float dYaw, float dPitch)
    {
        float r = Vector3.Distance(Position, pivot);
        Rotate(dYaw, dPitch);
        Position = pivot - Forward * r;
    }

    /// <summary>Frames a bounding box so it fills the view.</summary>
    public void Frame(Vector3 min, Vector3 max)
    {
        var center = (min + max) * 0.5f;
        float radius = MathF.Max(Vector3.Distance(min, max) * 0.5f, 0.5f);
        Position = center - Forward * (radius / MathF.Tan(FovY * 0.5f) * 1.1f);
    }

    public ViewInfo View(Vector2 size)
    {
        var view = Matrix4x4.CreateLookAt(Position, Position + Forward, Vector3.UnitY);
        return new ViewInfo(view * GlPerspective(MathF.Max(size.X, 1) / MathF.Max(size.Y, 1)), size, Position);
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

    /// <summary>World units covered by one pixel at <paramref name="distance"/>.</summary>
    public float UnitsPerPixel(float distance, float viewportHeight) =>
        2 * distance * MathF.Tan(FovY * 0.5f) / MathF.Max(viewportHeight, 1);
}
