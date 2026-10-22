using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.Viewport;

namespace Chip.Editor.Features.Dimensions;

/// <summary>
/// The words of the gizmo drag readout, as pieces with the gizmo axis each belongs to (-1 for none) so they can be
/// coloured like the gizmo. Move: the distance along each gizmo axis that moved, plus the straight-line total when
/// more than one did. Rotate: the angle. Scale: the factor and the selection's new size along the axis.
/// </summary>
public static class DragReadout
{
    static readonly string[] AxisNames = ["X", "Y", "Z"];

    public static List<(string Text, int Axis)> Describe(GizmoMode mode, Handle handle, IReadOnlyList<Vector3> basis,
        Gizmo.DragAmount amount, GestureKind kind, Vector3 startSize)
    {
        var runs = new List<(string, int)>();
        int handleAxis = handle switch { Handle.X => 0, Handle.Y => 1, Handle.Z => 2, _ => -1 };
        switch (mode)
        {
            case GizmoMode.Translate:
            {
                runs.Add((kind switch { GestureKind.Extrude => "Extrude", GestureKind.PushPull => "Push/pull", _ => "Move" }, -1));
                int moved = 0;
                for (int i = 0; i < 3; i++)
                {
                    float c = Vector3.Dot(amount.Delta, basis[i]);
                    if (MathF.Abs(c) < 5e-5f && i != handleAxis) continue;
                    runs.Add(($"{AxisNames[i]} {Labels.SignedMeasure(c)}", i));
                    moved++;
                }
                if (moved == 0) runs.Add((Labels.Measure(0), -1));
                else if (moved > 1) runs.Add(($"{Labels.Length(amount.Delta.Length())} in all", -1)); // a diagonal: the centimetre is enough
                break;
            }
            case GizmoMode.Rotate:
                runs.Add(("Rotate", -1));
                runs.Add(($"{(Math.Round(amount.Degrees, 2) < 0 ? "−" : "+")}{Math.Abs(Math.Round(amount.Degrees, 2)):0.##}°", handleAxis));
                break;
            default:
            {
                runs.Add(("Scale", -1));
                if (handle == Handle.Center)
                {
                    runs.Add(($"×{Math.Round(amount.Scale.X, 3):0.###}", -1));
                    break;
                }
                float s = amount.Scale[handleAxis];
                runs.Add(($"×{Math.Round(s, 3):0.###}", handleAxis));
                // The selection's size along the axis (exact for world axes; a fair estimate on turned ones).
                var a = basis[handleAxis];
                float size = MathF.Abs(a.X) * startSize.X + MathF.Abs(a.Y) * startSize.Y + MathF.Abs(a.Z) * startSize.Z;
                if (size > 1e-5f) runs.Add(($"{Labels.Measure(size)} → {Labels.Measure(size * MathF.Abs(s))}", handleAxis));
                break;
            }
        }
        return runs;
    }

    /// <summary>What a typed value means for this gizmo mode.</summary>
    public static EntryUnit UnitFor(GizmoMode mode) => mode switch
    {
        GizmoMode.Rotate => EntryUnit.Angle,
        GizmoMode.Scale => EntryUnit.Factor,
        _ => EntryUnit.Length,
    };
}
