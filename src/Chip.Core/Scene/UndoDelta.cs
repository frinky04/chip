using System.Runtime.InteropServices;
using Chip.Core.Geometry;

namespace Chip.Core.Scene;

/// <summary>
/// Detached previous values only. Applying a delta transfers its values into the live document and captures
/// the outgoing values for the reverse step. No historical mesh is shared with a live, editable mesh.
/// </summary>
sealed class UndoDelta
{
    readonly Document _settings;
    readonly Values<PolyMesh> _meshes;
    readonly Values<Entity> _entities;
    readonly Values<Group> _groups;

    UndoDelta(Document before, Document after)
    {
        _meshes = Values<PolyMesh>.Between(before.Meshes, after.Meshes, m => m.Id, MeshEquals);
        _entities = Values<Entity>.Between(before.Entities, after.Entities, e => e.Id, EntityEquals);
        _groups = Values<Group>.Between(before.Groups, after.Groups, g => g.Id, GroupEquals);
        // Keep settings without retaining the snapshot's unchanged clones/references. The export object is detached:
        // before is either a pending clone or the outgoing document during undo/redo.
        _settings = new Document
        {
            World = before.World, NextId = before.NextId, TexelDensity = before.TexelDensity, ExportScale = before.ExportScale, ExportPath = before.ExportPath,
            ExportCollision = before.ExportCollision, Export = before.Export, DressingPath = before.DressingPath,
        };
    }

    public static UndoDelta Between(Document before, Document after) => new(before, after);

    public (Document Doc, UndoDelta Reverse) Swap(Document current)
    {
        _settings.Meshes = _meshes.Restore(current.Meshes, m => m.Id);
        _settings.Entities = _entities.Restore(current.Entities, e => e.Id);
        _settings.Groups = _groups.Restore(current.Groups, g => g.Id);
        // The outgoing document is detached now, so its changed values can move directly into history.
        return (_settings, Between(current, _settings));
    }

    sealed class Values<T> where T : class
    {
        readonly Dictionary<int, T> _previous = new();
        HashSet<int>? _added;
        int[]? _order;

        public static Values<T> Between(List<T> before, List<T> after, Func<T, int> id, Func<T, T, bool> same)
        {
            var delta = new Values<T>();
            bool ordered = before.Count == after.Count;
            if (ordered)
                for (int i = 0; i < before.Count; i++)
                    if (id(before[i]) != id(after[i])) { ordered = false; break; }
            if (ordered)
            {
                for (int i = 0; i < before.Count; i++)
                    if (!ReferenceEquals(before[i], after[i]) && !same(before[i], after[i]))
                        delta._previous.Add(id(before[i]), before[i]);
            }
            else
            {
                var live = after.ToDictionary(id);
                var oldIds = before.Select(id).ToHashSet();
                delta._order = before.Select(id).ToArray();
                delta._added = after.Select(id).Where(k => !oldIds.Contains(k)).ToHashSet();
                foreach (var value in before)
                    if (!live.TryGetValue(id(value), out var other) ||
                        (!ReferenceEquals(value, other) && !same(value, other)))
                        delta._previous.Add(id(value), value);
            }
            return delta;
        }

        public List<T> Restore(List<T> current, Func<T, int> id)
        {
            if (_order == null)
            {
                var result = new List<T>(current.Count);
                foreach (var value in current) result.Add(_previous.GetValueOrDefault(id(value), value));
                return result;
            }
            var live = current.Where(v => !_added!.Contains(id(v))).ToDictionary(id);
            foreach (var (key, value) in _previous) live[key] = value;
            return _order.Select(k => live[k]).ToList();
        }
    }

    // Compare all saved fields exactly, including material case and float bits. Hashes alone must never decide undo.
    static bool Bits<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b) where T : unmanaged =>
        MemoryMarshal.AsBytes(a).SequenceEqual(MemoryMarshal.AsBytes(b));

    static bool BitsValue<T>(in T a, in T b) where T : unmanaged =>
        Bits(MemoryMarshal.CreateReadOnlySpan(in a, 1), MemoryMarshal.CreateReadOnlySpan(in b, 1));

    internal static bool MeshEquals(PolyMesh a, PolyMesh b)
    {
        if (a.Id != b.Id || a.Name != b.Name || a.Hidden != b.Hidden || a.Locked != b.Locked || a.Collision != b.Collision || a.Tags != b.Tags || !a.Dressing.Equals(b.Dressing) ||
            !Bits<System.Numerics.Vector3>(CollectionsMarshal.AsSpan(a.Positions), CollectionsMarshal.AsSpan(b.Positions)) ||
            a.Faces.Count != b.Faces.Count || !a.SoftEdges.SetEquals(b.SoftEdges)) return false;
        for (int i = 0; i < a.Faces.Count; i++)
        {
            var x = a.Faces[i]; var y = b.Faces[i];
            if (x.Material != y.Material || x.Dominant != y.Dominant || x.Tags != y.Tags || !x.Verts.AsSpan().SequenceEqual(y.Verts) ||
                !BitsValue(x.Map, y.Map) ||
                (x.UVs == null) != (y.UVs == null) || !Bits<System.Numerics.Vector2>(x.UVs, y.UVs)) return false;
        }
        return true;
    }

    static bool EntityEquals(Entity a, Entity b) => a.Id == b.Id && a.ClassName == b.ClassName && a.Name == b.Name &&
        BitsValue(a.Position, b.Position) && BitsValue(a.Rotation, b.Rotation) &&
        a.Tags == b.Tags && a.Outputs.Count == b.Outputs.Count && a.Outputs.Zip(b.Outputs).All(p => System.Text.Json.Nodes.JsonNode.DeepEquals(p.First.ToJson(), p.Second.ToJson())) &&
        a.Hidden == b.Hidden && a.Locked == b.Locked && a.Meshes.SequenceEqual(b.Meshes) && EntityValues.SameProperties(a.Properties, b.Properties);

    static bool GroupEquals(Group a, Group b) => a.Id == b.Id && a.Name == b.Name && a.Parent == b.Parent &&
        a.Hidden == b.Hidden && a.Locked == b.Locked && a.Folder == b.Folder && a.Members.SequenceEqual(b.Members);
}
