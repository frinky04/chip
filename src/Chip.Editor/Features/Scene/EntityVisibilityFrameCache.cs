using System.Numerics;
using Chip.Core.Picking;
using Chip.Core.Scene;

namespace Chip.Editor.Features.Scene;

/// <summary>Entity occlusion depends on geometry, eye and backface policy, not hover or selection.</summary>
public sealed class EntityVisibilityFrameCache
{
    readonly Rendering.FrameCache<Dictionary<int, bool>> _cache = new(selection: false);
    Vector3 _eye;
    bool _backfaces;

    /// <summary>
    /// Whether the entity's centre is visible from <paramref name="eye"/>; rays go through <paramref name="picks"/> when given.
    /// <paramref name="compute"/> replaces the ray test (tests count recomputes with it).
    /// </summary>
    public bool Get(Session session, Vector3 eye, bool backfaces, Entity entity, PickCache? picks = null, Func<bool>? compute = null)
    {
        if (!_cache.IsCurrent(session) || _eye != eye || _backfaces != backfaces)
        {
            _cache.Store(session, new Dictionary<int, bool>());
            _eye = eye; _backfaces = backfaces;
        }
        if (!_cache.Value.TryGetValue(entity.Id, out bool visible))
            _cache.Value[entity.Id] = visible = compute?.Invoke() ?? Visibility.IsVisible(session.Doc, eye, EntityOps.Center(entity), backfaces, picks);
        return visible;
    }
}
