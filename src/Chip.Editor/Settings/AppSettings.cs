using Chip.Editor.Framework;

namespace Chip.Editor.Settings;

/// <summary>
/// User preferences, persisted as JSON in <c>%APPDATA%/Chip/settings.json</c> (see <see cref="SettingsStore"/>).
/// Plain data: every field has a safe default, and <see cref="Normalize"/> repairs hand-edited or outdated values.
/// Panel widths are logical pixels (multiplied by the UI scale at draw time).
/// </summary>
public sealed class AppSettings
{
    public const int MaxRecentFiles = 10;
    public const int MaxRecentCommands = 8;
    public static readonly int[] AutosaveChoices = [0, 30, 60, 120, 300, 600];
    public static readonly float[] UiScaleChoices = [1f, 1.25f, 1.5f, 1.75f, 2f, 2.5f];
    public const float DefaultInspectorWidth = 320, DefaultToolStripWidth = 44, DefaultDockWidth = 320;
    public const float MinDockWidth = 240, MaxDockWidth = 640;

    public int Version { get; set; } = 1;

    /// <summary>Forced interface scale (1 = 100%), or null to follow the monitor's DPI.</summary>
    public float? UiScale { get; set; }
    /// <summary>Grid size in metres when Chip starts. One of <see cref="EditorContext.GridSizes"/>.</summary>
    public float GridDefault { get; set; } = 0.25f;
    /// <summary>Snap space when Chip starts: "World" (moves land on grid lines) or "Relative" (whole grid steps).</summary>
    public string SnapSpace { get; set; } = "World";
    /// <summary>Seconds between autosaves of a changed document; 0 turns autosave off.</summary>
    public int AutosaveSeconds { get; set; } = 60;

    public List<string> TextureFolders { get; set; } = new();
    /// <summary>Most recent first.</summary>
    public List<string> RecentFiles { get; set; } = new();
    /// <summary>Command ids run from the palette, most recent first.</summary>
    public List<string> RecentCommands { get; set; } = new();
    /// <summary>Command id → hotkey strings ("Ctrl+Shift+P"). An empty array unbinds the command.</summary>
    public Dictionary<string, string[]> Hotkeys { get; set; } = new();

    public float InspectorWidth { get; set; } = DefaultInspectorWidth;
    public float ToolStripWidth { get; set; } = DefaultToolStripWidth;
    public float DockWidth { get; set; } = DefaultDockWidth;
    /// <summary>The left dock's tab (a dock panel id), or null for the first.</summary>
    public string? DockTab { get; set; }
    /// <summary>False when the left dock is collapsed to its icon rail.</summary>
    public bool DockOpen { get; set; } = true;
    /// <summary>Ids of inspector sections folded to their title bar.</summary>
    public List<string> CollapsedSections { get; set; } = new();

    public void AddRecentFile(string path)
    {
        path = Path.GetFullPath(path);
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecentFiles) RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
    }

    public void AddRecentCommand(string id)
    {
        RecentCommands.Remove(id);
        RecentCommands.Insert(0, id);
        if (RecentCommands.Count > MaxRecentCommands) RecentCommands.RemoveRange(MaxRecentCommands, RecentCommands.Count - MaxRecentCommands);
    }

    /// <summary>Clamps and repairs values (nulls from JSON, out-of-range numbers, duplicates). Returns this.</summary>
    public AppSettings Normalize()
    {
        if (UiScale is { } s && (!float.IsFinite(s) || s < 0.75f || s > 4f)) UiScale = null;
        if (Array.IndexOf(EditorContext.GridSizes, GridDefault) < 0) GridDefault = 0.25f; // e.g. 12.5 cm from the old power-of-two steps
        if (!Enum.TryParse<Viewport.SnapSpace>(SnapSpace, out _)) SnapSpace = nameof(Viewport.SnapSpace.World);
        AutosaveSeconds = AutosaveSeconds <= 0 ? 0 : Math.Clamp(AutosaveSeconds, 10, 3600);
        TextureFolders = Clean(TextureFolders);
        RecentFiles = Clean(RecentFiles).Take(MaxRecentFiles).ToList();
        RecentCommands = Clean(RecentCommands).Take(MaxRecentCommands).ToList();
        Hotkeys = (Hotkeys ?? new()).Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value ?? []);
        InspectorWidth = Width(InspectorWidth, DefaultInspectorWidth, 240, 720);
        ToolStripWidth = Width(ToolStripWidth, DefaultToolStripWidth, 40, 96);
        DockWidth = Width(DockWidth, DefaultDockWidth, MinDockWidth, MaxDockWidth);
        CollapsedSections = Clean(CollapsedSections);
        return this;
    }

    public static float Width(float value, float fallback, float min, float max) =>
        float.IsFinite(value) && value > 0 ? Math.Clamp(value, min, max) : fallback;

    static List<string> Clean(List<string>? list) =>
        (list ?? new()).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
