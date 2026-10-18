using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chip.Editor.Viewport;

/// <summary>
/// Nine camera bookmarks (Hammer: Ctrl+Shift+1..9 store, Ctrl+1..9 recall). Serialises to a small JSON object keyed by
/// slot, so a settings or document layer can persist it.
/// </summary>
public sealed class CameraBookmarks
{
    public const int Slots = 9;
    readonly CameraState?[] _slots = new CameraState?[Slots];

    public CameraState? Get(int slot) => slot is >= 1 and <= Slots ? _slots[slot - 1] : null;

    public void Set(int slot, CameraState? state)
    {
        if (slot is < 1 or > Slots) throw new ArgumentOutOfRangeException(nameof(slot), "Bookmark slots are 1 to 9");
        _slots[slot - 1] = state;
    }

    public int Count => _slots.Count(s => s != null);

    sealed class Dto
    {
        [JsonPropertyName("projection")] public string Projection { get; set; } = "Perspective";
        [JsonPropertyName("position")] public float[] Position { get; set; } = [0, 0, 0];
        [JsonPropertyName("yaw")] public float Yaw { get; set; }
        [JsonPropertyName("pitch")] public float Pitch { get; set; }
        [JsonPropertyName("orthoHeight")] public float OrthoHeight { get; set; } = 20;
    }

    public string ToJson()
    {
        var map = new SortedDictionary<string, Dto>(StringComparer.Ordinal);
        for (int i = 0; i < Slots; i++)
            if (_slots[i] is { } s)
                map[(i + 1).ToString()] = new Dto
                {
                    Projection = s.Projection.ToString(), Position = [s.Position.X, s.Position.Y, s.Position.Z],
                    Yaw = s.Yaw, Pitch = s.Pitch, OrthoHeight = s.OrthoHeight,
                };
        return JsonSerializer.Serialize(map);
    }

    /// <summary>Reads <see cref="ToJson"/> output. Unknown slots and malformed entries are skipped, not fatal.</summary>
    public static CameraBookmarks FromJson(string json)
    {
        var result = new CameraBookmarks();
        var map = JsonSerializer.Deserialize<Dictionary<string, Dto>>(json) ?? new();
        foreach (var (key, d) in map)
        {
            if (!int.TryParse(key, out int slot) || slot is < 1 or > Slots || d.Position is not { Length: 3 }) continue;
            if (!Enum.TryParse<Projection>(d.Projection, out var p)) continue;
            result._slots[slot - 1] = new CameraState(p, new Vector3(d.Position[0], d.Position[1], d.Position[2]), d.Yaw, d.Pitch, d.OrthoHeight);
        }
        return result;
    }

    public void CopyFrom(CameraBookmarks other) => Array.Copy(other._slots, _slots, Slots);
}
