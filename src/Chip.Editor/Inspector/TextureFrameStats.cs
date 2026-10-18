using Chip.Core.Texturing;

namespace Chip.Editor.Inspector;

/// <summary>A single pass over the selected faces instead of allocating and scanning mapping lists on every draw.</summary>
public sealed record TextureFrameStats(List<FaceRef> Faces, int ExplicitCount, TextureMapping First,
    bool MixedShiftU, bool MixedShiftV, bool MixedScaleU, bool MixedScaleV, bool MixedRotation)
{
    public static TextureFrameStats Build(List<FaceRef> faces)
    {
        if (faces.Count == 0) return new(faces, 0, default, false, false, false, false, false);
        var first = faces[0].Face.Map;
        int explicitCount = 0;
        bool su = false, sv = false, cu = false, cv = false, r = false;
        foreach (var face in faces)
        {
            if (face.Face.UVs != null) explicitCount++;
            var map = face.Face.Map;
            su |= map.ShiftU != first.ShiftU; sv |= map.ShiftV != first.ShiftV;
            cu |= map.ScaleU != first.ScaleU; cv |= map.ScaleV != first.ScaleV;
            r |= map.Rotation != first.Rotation;
        }
        return new(faces, explicitCount, first, su, sv, cu, cv, r);
    }
}
