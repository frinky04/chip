using System.Numerics;

namespace Chip.Core.Geometry;

/// <summary>
/// An object's own origin: a point and an orientation that travel with it (a mesh's, or a group's). Vertices stay in
/// world space; the origin is the frame laid over them, as a mesh entity's position and rotation are. It is the gizmo's
/// pivot and axes, and export writes the object's vertices relative to it with the origin as the node's transform. An
/// object without one (<c>null</c>) behaves as before: pivot at its bounds centre, exported at the world origin.
/// </summary>
public readonly record struct ObjectOrigin(Vector3 Position, Quaternion Rotation)
{
    public static ObjectOrigin At(Vector3 position) => new(position, Quaternion.Identity);

    /// <summary>Origin space to world space.</summary>
    public Matrix4x4 World => Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);

    /// <summary>World space to origin space.</summary>
    public Matrix4x4 Local => Matrix4x4.CreateTranslation(-Position) * Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(Rotation));

    public (Vector3 X, Vector3 Y, Vector3 Z) Axes =>
        (Vector3.Transform(Vector3.UnitX, Rotation), Vector3.Transform(Vector3.UnitY, Rotation), Vector3.Transform(Vector3.UnitZ, Rotation));

    /// <summary>
    /// The origin after its object is transformed by <paramref name="m"/>: the point goes along, and the orientation turns
    /// by the rotation part of <paramref name="m"/> (an origin has no size, so scale only moves the point).
    /// </summary>
    public ObjectOrigin Transformed(Matrix4x4 m)
    {
        var turn = Matrix4x4.Decompose(m, out _, out var q, out _) ? Quaternion.Normalize(q) : Quaternion.Identity;
        bool turned = MathF.Abs(turn.W) < 0.999999f;
        return new(Vector3.Transform(Position, m), turned ? Quaternion.Normalize(Quaternion.Concatenate(Rotation, turn)) : Rotation);
    }
}
