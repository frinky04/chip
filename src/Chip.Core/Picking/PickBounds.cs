using System.Numerics;

namespace Chip.Core.Picking;

// Bounds are deliberately conservative: the exact legacy tests still decide every hit.
internal readonly record struct PickBounds(Vector3 Min, Vector3 Max)
{
    public static PickBounds Union(PickBounds a, PickBounds b) => new(Vector3.Min(a.Min, b.Min), Vector3.Max(a.Max, b.Max));
    public bool Intersects(Ray ray, float limit)
    {
        float near = 0, far = limit + MathF.Max(1, limit) * 1e-4f;
        for (int axis = 0; axis < 3; axis++)
        {
            float lo = Min[axis], hi = Max[axis], o = ray.Origin[axis], d = ray.Dir[axis];
            float slack = 1e-5f * MathF.Max(1, MathF.Max(MathF.Abs(lo), MathF.Abs(hi)));
            lo -= slack;
            hi += slack;
            if (d == 0) { if (o < lo || o > hi) return false; continue; }
            float a = (lo - o) / d, b = (hi - o) / d;
            if (a > b) (a, b) = (b, a);
            near = MathF.Max(near, a);
            far = MathF.Min(far, b);
            if (near > far) return false;
        }
        return true;
    }
}

/// <summary>A balanced spatial tree. Membership changes rebuild it; moved leaves refit their ancestors.</summary>
internal sealed class PickTree
{
    internal struct Node
    {
        public PickBounds Bounds;
        public int Left, Right, Item, Parent;
    }

    public Node[] Nodes { get; }
    readonly int[] _leaves;

    public PickTree(PickBounds[] bounds)
    {
        Nodes = new Node[Math.Max(0, bounds.Length * 2 - 1)];
        _leaves = new int[bounds.Length];
        if (bounds.Length == 0) return;
        var items = Enumerable.Range(0, bounds.Length).ToArray();
        int next = 0;
        Build(0, items.Length, -1);

        int Build(int start, int count, int parent)
        {
            int index = next++;
            var box = bounds[items[start]];
            for (int i = start + 1; i < start + count; i++) box = PickBounds.Union(box, bounds[items[i]]);
            if (count == 1)
            {
                int item = items[start];
                Nodes[index] = new Node { Bounds = box, Item = item, Parent = parent, Left = -1, Right = -1 };
                _leaves[item] = index;
                return index;
            }
            var extent = box.Max - box.Min;
            int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
            Array.Sort(items, start, count, Comparer<int>.Create((a, b) =>
                (bounds[a].Min[axis] + bounds[a].Max[axis]).CompareTo(bounds[b].Min[axis] + bounds[b].Max[axis])));
            int left = Build(start, count / 2, index);
            int right = Build(start + count / 2, count - count / 2, index);
            Nodes[index] = new Node { Bounds = box, Item = -1, Parent = parent, Left = left, Right = right };
            return index;
        }
    }

    public void Refit(int item, PickBounds bounds)
    {
        int node = _leaves[item];
        Nodes[node].Bounds = bounds;
        while ((node = Nodes[node].Parent) >= 0)
            Nodes[node].Bounds = PickBounds.Union(Nodes[Nodes[node].Left].Bounds, Nodes[Nodes[node].Right].Bounds);
    }
}

/// <summary>Five homogeneous half-spaces enclosing a screen rectangle, with no far-plane clipping.</summary>
internal readonly struct PickScreenBox
{
    readonly Vector4 _left, _right, _top, _bottom, _w;

    public PickScreenBox(ViewInfo view, Vector2 min, Vector2 max)
    {
        var m = view.ViewProj;
        var x = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var y = new Vector4(m.M12, m.M22, m.M32, m.M42);
        _w = new Vector4(m.M14, m.M24, m.M34, m.M44);
        _left = x - (min.X / view.Size.X * 2 - 1) * _w;
        _right = (max.X / view.Size.X * 2 - 1) * _w - x;
        _top = (1 - min.Y / view.Size.Y * 2) * _w - y;
        _bottom = y - (1 - max.Y / view.Size.Y * 2) * _w;
    }

    public bool Intersects(PickBounds bounds) => Positive(_w, bounds) && Positive(_left, bounds) &&
        Positive(_right, bounds) && Positive(_top, bounds) && Positive(_bottom, bounds);

    static bool Positive(Vector4 plane, PickBounds bounds)
    {
        var p = new Vector3(plane.X >= 0 ? bounds.Max.X : bounds.Min.X,
            plane.Y >= 0 ? bounds.Max.Y : bounds.Min.Y, plane.Z >= 0 ? bounds.Max.Z : bounds.Min.Z);
        float x = plane.X * p.X, y = plane.Y * p.Y, z = plane.Z * p.Z;
        return x + y + z + plane.W >= -1e-5f * (1 + MathF.Abs(x) + MathF.Abs(y) + MathF.Abs(z) + MathF.Abs(plane.W));
    }
}
