using System.Numerics;
using Chip.Core.Picking;

namespace Chip.Core.Scene;

/// <summary>
/// Where a reference model goes when it's placed on a wall. The model turns so its front (+Z, the glTF convention) faces
/// out of the wall and stands on the wall's bottom edge. If it is no deeper than the wall (a door and its frame) it is
/// centred in the wall's thickness, so the hole to cut is exactly its outline; anything deeper (a person, a cabinet) stands
/// against the wall instead.
/// </summary>
public static class ReferencePlacement
{
    /// <summary>Faces steeper than this (|normal.y| below it) count as walls.</summary>
    public const float WallSlope = 0.3f;
    /// <summary>A model this much deeper than the wall still counts as fitting in it (frames often stand proud a little).</summary>
    public const float FitTolerance = 0.02f;
    /// <summary>Walls thicker than this are treated as solid (no far side to centre in).</summary>
    public const float MaxThickness = 2f;

    /// <param name="Thickness">The wall's thickness through the clicked point, or null when no far side was found.</param>
    /// <param name="Inset">True when the model was centred in the wall; false when it stands against it.</param>
    public readonly record struct Result(Vector3 Position, float Yaw, float? Thickness, bool Inset);

    public static bool IsWall(Vector3 normal) => MathF.Abs(normal.Y) < WallSlope;

    /// <param name="point">The clicked point on the wall (grid-snapped along it).</param>
    /// <param name="normal">The wall's normal, pointing out toward the viewer.</param>
    /// <param name="bottom">Height of the wall face's bottom edge; the model's origin goes there.</param>
    public static Result OnWall(Document doc, Vector3 point, Vector3 normal, float bottom, ReferenceModel model)
    {
        var n = Vector3.Normalize(new Vector3(normal.X, 0, normal.Z));
        float yaw = MathF.Round(MathF.Atan2(n.X, n.Z) * 180 / MathF.PI, 2);
        if (yaw == 0) yaw = 0; // no negative zero in the inspector
        float? thickness = Thickness(doc, point, n);
        float zMin = model.Min.Z, zMax = model.Max.Z;
        bool inset = thickness is { } t && zMax - zMin <= t + FitTolerance;
        // Model z runs along n: inset puts the model's depth centre on the wall's mid-plane, otherwise its back on the face.
        var at = inset ? point - n * (thickness!.Value * 0.5f) - n * ((zMin + zMax) * 0.5f) : point - n * zMin;
        return new Result(new Vector3(at.X, bottom, at.Z), yaw, thickness, inset);
    }

    /// <summary>Distance from the wall face at <paramref name="point"/> to the next surface facing the other way behind it.</summary>
    public static float? Thickness(Document doc, Vector3 point, Vector3 outward)
    {
        const float Start = 0.001f;
        var ray = new Ray(point - outward * Start, -outward);
        for (int i = 0; i < 8; i++)
        {
            if (Picker.RaycastFace(doc, ray, backfaces: true) is not { } hit || hit.Distance > MaxThickness) return null;
            float travelled = Vector3.Distance(point, hit.Point);
            if (Vector3.Dot(hit.Normal, outward) < -0.5f) return travelled;
            ray = new Ray(hit.Point - outward * Start, -outward); // an inner face (a ledge, a coplanar piece): keep going
        }
        return null;
    }
}
