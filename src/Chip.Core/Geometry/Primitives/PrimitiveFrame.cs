using System.Numerics;
using Chip.Core.Texturing;

namespace Chip.Core.Geometry.Primitives;

/// <summary>
/// The box a primitive fills, with its own axes: X is the run (stairs climb along it, arches span it), Y is up
/// (cylinder axis, stair rise) and Z is across. Axes are unit world axes, possibly negative; <see cref="Size"/> is
/// the extent along each. Local point (x, y, z) in [0, Size] maps to <c>Origin + X*x + Y*y + Z*z</c>.
/// </summary>
public readonly record struct PrimitiveFrame(Vector3 Origin, Vector3 X, Vector3 Y, Vector3 Z, Vector3 Size)
{
    public Vector3 ToWorld(Vector3 local) => Origin + X * local.X + Y * local.Y + Z * local.Z;

    public Vector3 Min => Vector3.Min(Origin, ToWorld(Size));
    public Vector3 Max => Vector3.Max(Origin, ToWorld(Size));

    /// <summary>Frame for an axis-aligned box, given signed unit world axes for up and run.</summary>
    public static PrimitiveFrame FromBox(Vector3 min, Vector3 max, Vector3 up, Vector3 run)
    {
        var across = Vector3.Abs(Vector3.Cross(up, run));
        var s = up + run + across; // each component is +1 or -1: which end of the box the frame starts at
        var origin = new Vector3(s.X < 0 ? max.X : min.X, s.Y < 0 ? max.Y : min.Y, s.Z < 0 ? max.Z : min.Z);
        var d = max - min;
        var size = new Vector3(MathF.Abs(Vector3.Dot(d, run)), MathF.Abs(Vector3.Dot(d, up)), MathF.Abs(Vector3.Dot(d, across)));
        return new PrimitiveFrame(origin, run, up, across, size);
    }

    /// <summary>
    /// Frame from the block tool's gesture: a base dragged from <paramref name="a"/> to <paramref name="b"/> on an axis plane
    /// with <paramref name="normal"/>, then raised by <paramref name="height"/> (negative goes below the plane).
    /// The run follows the longer side of the base in the drag direction; <paramref name="turn"/> uses the other side.
    /// </summary>
    public static PrimitiveFrame FromBlock(Vector3 a, Vector3 b, Vector3 normal, float height, bool turn = false)
    {
        var n = TextureMapping.DominantAxis(normal);
        var up = height < 0 ? -n : n;
        var top = b + n * height;
        var min = Vector3.Min(Vector3.Min(a, b), top);
        var max = Vector3.Max(Vector3.Max(a, b), top);

        var (u0, u1) = Primitive.BaseAxes(n);
        var d = b - a;
        var run = MathF.Abs(Vector3.Dot(d, u0)) >= MathF.Abs(Vector3.Dot(d, u1)) ? u0 : u1;
        if (turn) run = run == u0 ? u1 : u0;
        if (Vector3.Dot(d, run) < 0) run = -run;
        return FromBox(min, max, up, run);
    }
}
