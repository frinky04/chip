using System.Numerics;
using Chip.Core.Scene;

namespace Chip.Editor.Features.Scene;

/// <summary>Entity occlusion depends on geometry, eye and backface policy, not hover or selection.</summary>
public sealed class EntityVisibilityFrameCache
{
    readonly Rendering.FrameCache<Dictionary<int, bool>> _cache = new(selection: false);
    Vector3 _eye;
    bool _backfaces;
    public bool Get(Session session, Vector3 eye, bool backfaces, Entity entity, Func<bool>? compute = null)
    {
        if (!_cache.IsCurrent(session) || _eye != eye || _backfaces != backfaces)
        {
            _cache.Store(session, new Dictionary<int, bool>());
            _eye = eye; _backfaces = backfaces;
        }
        if (!_cache.Value.TryGetValue(entity.Id, out bool visible)) _cache.Value[entity.Id] = visible = compute != null ? compute() : Chip.Core.Picking.Visibility.IsVisible(session.Doc, eye, EntityOps.Center(entity), backfaces);
        return visible;
    }
}
