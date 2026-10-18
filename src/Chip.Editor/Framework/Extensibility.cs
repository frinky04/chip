using System.Numerics;
using System.Reflection;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor.Framework;

/// <summary>
/// A viewport tool: what the left mouse button does. Registered tools appear in the tool strip (by <see cref="Order"/>)
/// and get a "Tools" menu command with <see cref="Hotkey"/>. Only the active tool receives viewport input.
/// </summary>
public interface IViewportTool
{
    string Id { get; }
    string Name { get; }
    Icon Icon { get; }
    /// <summary>Hotkey that activates (or toggles back from) the tool, e.g. "Shift+B". Null for none.</summary>
    string? Hotkey { get; }
    int Order { get; }

    /// <summary>True while mid-operation: the tool keeps receiving input even off-viewport, and most hotkeys wait.</summary>
    bool Busy { get; }

    /// <summary>Key hints for the status bar, e.g. [("Drag", "draw a base")].</summary>
    (string Key, string Action)[] Hints { get; }

    void Activate(EditorContext ctx) { }

    /// <summary>On Esc, and before undo/new/open. Return true if an operation was in progress (and is now undone).</summary>
    bool Cancel(EditorContext ctx);

    /// <summary>
    /// Another tool is taking over. Defaults to <see cref="Cancel"/>; tools with a pending result the user has already
    /// set up (a bevel waiting for Enter) commit it instead.
    /// </summary>
    void Deactivate(EditorContext ctx) => Cancel(ctx);

    /// <summary>
    /// While true and the viewport is hovered, the mouse wheel goes to the tool (<see cref="EditorContext.ToolWheel"/>)
    /// instead of the camera.
    /// </summary>
    bool CapturesWheel => false;

    /// <summary>One frame of input while the viewport is hovered or the tool is busy. Read the mouse from <see cref="EditorContext"/>.</summary>
    void Update(EditorContext ctx);

    /// <summary>Overlay drawing in screen space; <paramref name="origin"/> is the viewport's top-left.</summary>
    void Draw(EditorContext ctx, ImDrawListPtr dl, Vector2 origin);

    /// <summary>Whether the inspector shows a "Tool" section with <see cref="DrawOptions"/> while this tool is active.</summary>
    bool HasOptions => false;

    /// <summary>Tool settings (shape, segments, mode...). Drawn under a heading with the tool's name.</summary>
    void DrawOptions(EditorContext ctx) { }
}

/// <summary>A block in the right-hand inspector. Visible sections draw in <see cref="Order"/>, separated by hairlines.</summary>
public interface IInspectorSection
{
    string Id { get; }
    /// <summary>Lower draws first. Built-ins: tool 0, selection 100, material 200, texture 300, export 900.</summary>
    int Order { get; }
    bool Visible(EditorContext ctx);
    /// <summary>Draw the section, starting with <see cref="Widgets.Heading"/>.</summary>
    void Draw(EditorContext ctx);
}

/// <summary>
/// The unit of extension. Every non-abstract implementation in the editor assembly is created and registered at startup,
/// so a feature adds files and never edits shared ones. Register commands, tools, sections and mouse bindings here.
/// </summary>
public interface IEditorFeature
{
    void Register(EditorContext ctx);
}

/// <summary>A mouse chord in the viewport, checked before camera navigation and tools (e.g. Shift+RMB pick up material).</summary>
public sealed record MouseBinding(ImGuiMouseButton Button, bool Ctrl, bool Shift, bool Alt, string Category, string Label, Action<EditorContext> Execute)
{
    public string KeysText => $"{(Ctrl ? "Ctrl+" : "")}{(Shift ? "Shift+" : "")}{(Alt ? "Alt+" : "")}{Button switch
    {
        ImGuiMouseButton.Left => "LMB", ImGuiMouseButton.Right => "RMB", _ => "MMB",
    }}";
}

public static class FeatureLoader
{
    /// <summary>Instantiates and registers every <see cref="IEditorFeature"/> in the assembly, in type-name order.</summary>
    public static IReadOnlyList<IEditorFeature> LoadAll(EditorContext ctx, Assembly? assembly = null)
    {
        var features = (assembly ?? typeof(FeatureLoader).Assembly).GetTypes()
            .Where(t => typeof(IEditorFeature).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false } && t.GetConstructor(Type.EmptyTypes) != null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .Select(t => (IEditorFeature)Activator.CreateInstance(t)!)
            .ToList();
        foreach (var f in features) f.Register(ctx);
        return features;
    }
}
