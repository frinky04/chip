using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor.Features.Logic;

/// <summary>
/// The eyedropper behind a reference field: the next click on an entity the field accepts fills it, then the previous
/// tool comes back. Esc, or a click on nothing, gives up. Never on the tool strip; <see cref="Start"/> arms it.
/// </summary>
public sealed class EntityPickTool : IViewportTool
{
    IViewportTool? _previous;
    Func<Entity, bool> _accepts = _ => true;
    Action<Entity> _picked = _ => { };
    string _what = "an entity";
    Entity? _hover;

    public string Id => "logic.pick";
    public string Name => "Pick entity";
    public Icon Icon => Icon.Target;
    public string? Hotkey => null;
    public int Order => int.MaxValue;
    public bool Busy => false;
    public (string Key, string Action)[] Hints => [("Click", $"pick {_what}"), ("Esc", "cancel")];

    /// <summary>True while armed for the field <paramref name="owner"/> named (so the field can show it's waiting).</summary>
    public bool Armed(EditorContext ctx, string owner) => ctx.ActiveTool == this && _owner == owner;
    string _owner = "";

    /// <summary>
    /// Arms the eyedropper. <paramref name="owner"/> identifies the field; <paramref name="what"/> names what it wants
    /// ("a door") for the status bar.
    /// </summary>
    public void Start(EditorContext ctx, string owner, string what, Func<Entity, bool> accepts, Action<Entity> picked)
    {
        _owner = owner;
        _what = what;
        _accepts = accepts;
        _picked = picked;
        if (ctx.ActiveTool != this) _previous = ctx.ActiveTool;
        ctx.SetTool(this);
        ctx.Status($"Click {what} in the viewport, or Esc to cancel");
    }

    public void Update(EditorContext ctx)
    {
        _hover = ctx.ViewportHovered && ctx.PickAtMouse(Theme.Scale) is { } h ? EntityAt(ctx.Doc, h.Mesh) : null;
        ctx.Hover = _hover != null && _accepts(_hover) ? ElementRef.Object(_hover.Id) : null;
        if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;
        if (_hover != null && _accepts(_hover))
        {
            var picked = _picked;
            var e = _hover;
            Finish(ctx);
            picked(e);
        }
        else
        {
            ctx.Status(_hover != null ? $"{SceneOps.DisplayName(_hover)} isn't {_what}" : $"No entity there. Click {_what}, or Esc to cancel", error: _hover != null);
            if (_hover == null) Finish(ctx);
        }
    }

    public bool Cancel(EditorContext ctx)
    {
        if (ctx.ActiveTool == this) Finish(ctx);
        return false;
    }

    public void Deactivate(EditorContext ctx) { _owner = ""; _hover = null; }

    void Finish(EditorContext ctx)
    {
        var back = _previous ?? ctx.DefaultTool;
        _previous = null;
        _owner = "";
        if (back != null && back != this) ctx.SetTool(back);
    }

    /// <summary>The entity a picked object is: the entity itself, or the mesh entity owning a picked mesh.</summary>
    static Entity? EntityAt(Document doc, int id) =>
        doc.FindEntity(id) ?? doc.Entities.FirstOrDefault(e => e.Meshes.Contains(id));

    public void Draw(EditorContext ctx, ImDrawListPtr dl, Vector2 origin)
    {
        if (!ctx.ViewportHovered) return;
        var mouse = ImGui.GetMousePos();
        string text = _hover == null ? $"Pick {_what}" : _accepts(_hover) ? SceneOps.DisplayName(_hover) : $"Not {_what}";
        uint col = _hover != null && !_accepts(_hover) ? ImGui.ColorConvertFloat4ToU32(Theme.Error) : LinkDraw.OutColor;
        var p = mouse + Theme.Px(14, 14);
        var size = ImGui.CalcTextSize(text);
        dl.AddRectFilled(p - Theme.Px(4, 2), p + size + Theme.Px(4, 2), 0xE6100F0E, Theme.Px(3));
        dl.AddText(p, col, text);
    }
}
