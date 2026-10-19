using System.Numerics;

namespace Chip.Core.Geometry;

/// <summary>
/// Cheap 64-bit content hash of a mesh's content: everything its GPU buffers depend on (positions, face loops, materials,
/// mappings, explicit UVs, soft edges, dominant faces) plus face and mesh tags. Per-mesh caches (GPU buffers, picking,
/// dressing) key on it, so a gizmo drag rebuilds only the meshes it moves.
/// </summary>
public static class MeshHash
{
    const ulong Offset = 14695981039346656037UL, Prime = 1099511628211UL;

    public static ulong Of(PolyMesh m)
    {
        ulong h = Offset;
        void Add(uint x)
        {
            h ^= x;
            h *= Prime;
        }
        void AddF(float f) => Add(BitConverter.SingleToUInt32Bits(f));
        void AddV(Vector3 v) { AddF(v.X); AddF(v.Y); AddF(v.Z); }
        void AddTags(TagSet tags)
        {
            Add((uint)tags.Count);
            foreach (var t in tags) Add((uint)StringComparer.Ordinal.GetHashCode(t));
        }

        Add((uint)m.Positions.Count);
        foreach (var p in m.Positions) AddV(p);
        Add((uint)m.Faces.Count);
        foreach (var f in m.Faces)
        {
            Add((uint)f.Verts.Length);
            foreach (var v in f.Verts) Add((uint)v);
            Add((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(f.Material));
            var map = f.Map;
            AddV(map.UAxis); AddV(map.VAxis);
            AddF(map.ShiftU); AddF(map.ShiftV); AddF(map.ScaleU); AddF(map.ScaleV); AddF(map.Rotation);
            if (f.UVs is { } uvs)
            {
                Add((uint)uvs.Length);
                foreach (var uv in uvs) { AddF(uv.X); AddF(uv.Y); }
            }
            else Add(0xFFFFFFFF);
            if (f.Dominant) Add(0xD0D0D0D0); // dominant normals change corner normals
            AddTags(f.Tags); // dressing rules select by tags, so a tag edit is a content change
        }
        AddTags(m.Tags);
        // Order-independent: a cloned HashSet may enumerate differently.
        ulong soft = 0;
        foreach (var e in m.SoftEdges) soft += (ulong)(uint)e.A * 2654435761UL ^ (ulong)(uint)e.B * 40503UL + 0x9E3779B97F4A7C15UL;
        Add((uint)soft);
        Add((uint)(soft >> 32));
        return h;
    }
}
