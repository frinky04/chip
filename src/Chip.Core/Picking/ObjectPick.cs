using System.Numerics;
using Chip.Core.Scene;

namespace Chip.Core.Picking;

/// <summary>
/// What object mode can pick. Geometry: plain meshes (not part of a mesh entity). Entities: entities and the meshes of
/// mesh entities (a door's panel picks the door). Surfaces still hide entities behind them whatever the filter.
/// </summary>
public enum PickFilter { All, Geometry, Entities }

public static class PickFilters
{
    public static string Label(this PickFilter f) => f switch
    {
        PickFilter.Geometry => "Geometry", PickFilter.Entities => "Entities", _ => "All",
    };

    /// <summary>Whether object <paramref name="id"/> (a mesh or an entity) passes the filter.</summary>
    public static bool Accepts(Document doc, int id, PickFilter filter) => filter switch
    {
        PickFilter.Geometry => doc.Find(id) != null && MeshEntityOps.OwnerOf(doc, id) == null,
        PickFilter.Entities => doc.FindEntity(id) != null || MeshEntityOps.OwnerOf(doc, id) != null,
        _ => SceneOps.IsObject(doc, id),
    };
}

/// <summary>
/// Orders the objects under the cursor, best first. Shared by <see cref="Picker"/> and <see cref="PickCache"/> so the
/// fast and reference paths agree.
/// <list type="number">
/// <item>Entities whose glyph is under the cursor and isn't hidden by a surface, nearest glyph first: the glyph is drawn
/// on top, so it is what the user aimed at.</item>
/// <item>Entity boxes the ray passes all the way through before reaching a surface, by where the ray leaves them: a box
/// inside a bigger box leaves first, so the smaller, inner one wins.</item>
/// <item>Everything else by depth: meshes where the ray hits them, entities behind a surface, a box whose inside holds the
/// surface the user clicked (a trigger volume around a wall: the wall comes first, the volume where the ray leaves it),
/// and a box the camera is inside.</item>
/// </list>
/// Clicking again in the same spot walks down this list.
/// </summary>
internal static class ObjectRanking
{
    internal struct Candidate
    {
        public int Id, Unit, Tier, Order;
        public float Key, Key2;
        /// <summary>Not behind a surface (what <see cref="Picker.PickEntity"/> may return).</summary>
        public bool Visible;
        public bool IsEntity;
    }

    public static readonly Comparison<Candidate> Order = (a, b) =>
        a.Tier != b.Tier ? a.Tier.CompareTo(b.Tier)
        : a.Key != b.Key ? a.Key.CompareTo(b.Key)
        : a.Key2 != b.Key2 ? a.Key2.CompareTo(b.Key2)
        : a.Order.CompareTo(b.Order);

    /// <summary>Surface distance → how far an entity may be and still count as in front of it (the legacy tolerance).</summary>
    public static float Limit(float? surface) => surface is { } s ? s * 1.02f + 0.02f : float.MaxValue;

    /// <summary>The entity's model (<see cref="ReferenceModels.Get(Entity)"/>), straight from the cache.</summary>
    public static readonly Func<Entity, ReferenceModel?> Uncached = e => e.IsMeshEntity ? null : ReferenceModels.Get(e).Model;

    /// <summary>Adds every selectable entity under the cursor. <paramref name="models"/> looks up each entity's model.</summary>
    public static void AddEntities(Document doc, ViewInfo view, Ray ray, Vector2 mouse, float pixelScale, float? surface, List<Candidate> into,
        Func<Entity, ReferenceModel?> models)
    {
        float limit = Limit(surface);
        var entities = doc.Entities;
        for (int i = 0; i < entities.Count; i++)
        {
            var e = entities[i];
            if (e.Hidden || e.Locked) continue;
            var model = models(e);
            var hit = EntityOps.RayInterval(e, ray, model);
            float glyphPx = float.MaxValue, centerDepth = 0;
            // A loaded reference model has no glyph: its own surface is the target, and a doorway stays clickable.
            if (model == null)
            {
                var center = EntityOps.Center(e, null);
                if (view.Project(center, out var sc) && Vector2.Distance(sc, mouse) is var px && px < Picker.EntityGlyphRadius * pixelScale)
                {
                    glyphPx = px;
                    centerDepth = MathF.Max(0, Vector3.Dot(center - ray.Origin, ray.Dir));
                }
            }
            var c = new Candidate { Id = e.Id, Unit = e.Id, Order = i, IsEntity = true };
            if (glyphPx < float.MaxValue)
            {
                c.Visible = centerDepth <= limit;
                (c.Tier, c.Key, c.Key2) = c.Visible ? (0, glyphPx, centerDepth) : (2, centerDepth, 0);
            }
            else if (hit is { } h)
            {
                c.Visible = h.Enter <= limit;
                if (c.Visible && !h.Inside && h.Exit <= limit) (c.Tier, c.Key, c.Key2) = (1, h.Exit, h.Enter);
                else (c.Tier, c.Key) = (2, h.Inside || c.Visible ? h.Exit : h.Enter);
            }
            else continue;
            into.Add(c);
        }
    }

    /// <summary>Adds a mesh hit at distance <paramref name="t"/>; a mesh entity's meshes count as that entity.</summary>
    public static void AddMesh(int meshId, int owner, int meshIndex, float t, List<Candidate> into) =>
        into.Add(new Candidate { Id = meshId, Unit = owner >= 0 ? owner : meshId, Tier = 2, Key = t, Order = 1 << 24 | meshIndex, Visible = true });

    public static bool Accepts(PickFilter filter, bool owned) => filter switch
    {
        PickFilter.Geometry => !owned,
        PickFilter.Entities => owned,
        _ => true,
    };

    /// <summary>Sorts best first and keeps one candidate per selectable unit (a mesh entity and its meshes are one).</summary>
    public static void Finish(List<Candidate> list, HashSet<int> seen)
    {
        list.Sort(Order);
        seen.Clear();
        int w = 0;
        for (int r = 0; r < list.Count; r++)
            if (seen.Add(list[r].Unit)) list[w++] = list[r];
        list.RemoveRange(w, list.Count - w);
    }

    /// <summary>The single best candidate without sorting (hover runs every frame).</summary>
    public static int Best(List<Candidate> list)
    {
        int best = -1;
        for (int i = 0; i < list.Count; i++)
            if (best < 0 || Order(list[i], list[best]) < 0) best = i;
        return best;
    }

    /// <summary>Mesh id → the mesh entity that owns it.</summary>
    public static void Owners(Document doc, Dictionary<int, int> into)
    {
        into.Clear();
        foreach (var e in doc.Entities)
            foreach (int m in e.Meshes) into.TryAdd(m, e.Id);
    }
}
