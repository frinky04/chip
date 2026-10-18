using System.Numerics;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor;

/// <summary>Viewport host (stream G): panes, per-frame viewport state, input routing to bindings, navigation and tools, HUD.</summary>
public sealed partial class EditorApp
{
    void DrawViewport(Vector2 pos, Vector2 size, float dt)
    {
        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(size);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.Begin("##viewport", FixedPanel | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        DrawViewportHeader(size.X);

        var origin = ImGui.GetCursorScreenPos();
        var vpSize = Vector2.Max(ImGui.GetContentRegionAvail(), Vector2.One);
        var mousePos = ImGui.GetIO().MousePos;
        bool overView = mousePos.X >= origin.X && mousePos.Y >= origin.Y && mousePos.X < origin.X + vpSize.X && mousePos.Y < origin.Y + vpSize.Y;
        Ctx.ViewportOrigin = origin;
        Ctx.ViewportSize = vpSize;
        Ctx.Mouse = mousePos - origin;
        Ctx.ViewportHovered = overView && ImGui.IsWindowHovered() && !ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId);
        Ctx.View = Ctx.Camera.View(vpSize);

        UpdateViewport(dt);
        Ctx.View = Ctx.Camera.View(vpSize);

        uint tex = _renderer.Render(vpSize, Ctx.View, Ctx.Session, Ctx.Hover, Ctx.Grid, Ctx.Backfaces);
        ImGui.Image((nint)tex, vpSize, new Vector2(0, 1), new Vector2(1, 0));

        var dl = ImGui.GetWindowDrawList();
        Ctx.ActiveTool?.Draw(Ctx, dl, origin);
        DrawViewportHud(dl, origin, vpSize);
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    /// <summary>Mouse bindings first (they carry modifiers), then camera navigation, then the active tool.</summary>
    void UpdateViewport(float dt)
    {
        bool busy = Ctx.Interacting;
        if (!Ctx.ViewportHovered && !busy && !_nav.Active) { Ctx.Hover = null; return; }
        if (Ctx.ViewportHovered && !busy && !_nav.Active && TryMouseBinding()) return;
        if (_nav.Update(Ctx, dt)) { Ctx.Hover = null; return; }
        Ctx.ActiveTool?.Update(Ctx);
    }

    bool TryMouseBinding()
    {
        var io = ImGui.GetIO();
        foreach (var b in Ctx.MouseBindings)
        {
            if (!ImGui.IsMouseClicked(b.Button) || io.KeyCtrl != b.Ctrl || io.KeyShift != b.Shift || io.KeyAlt != b.Alt) continue;
            b.Execute(Ctx);
            return true;
        }
        return false;
    }

    void DrawViewportHud(ImDrawListPtr dl, Vector2 origin, Vector2 size)
    {
        var p = origin + Theme.Px(12, 10);
        uint dim = ImGui.ColorConvertFloat4ToU32(Theme.TextDim);
        DrawAxisIndicator(dl, origin + new Vector2(Theme.Px(40), size.Y - Theme.Px(40)));
        if (_nav.Flying) dl.AddText(p, Theme.AccentU32, $"Flying at {Ctx.Camera.FlySpeed:0.#} m/s, scroll to change speed");

        if (Ctx.Doc.Meshes.Count == 0 && Ctx.ActiveTool == Ctx.DefaultTool)
        {
            var center = origin + size * 0.5f;
            const string line1 = "Press Shift+B to draw your first block";
            const string line2 = "Drag a base on the grid, release, then move up to set its height";
            var s1 = ImGui.CalcTextSize(line1);
            var s2 = ImGui.CalcTextSize(line2);
            float lh = ImGui.GetTextLineHeight();
            var half = new Vector2(MathF.Max(s1.X, s2.X) * 0.5f + Theme.Px(20), 0);
            dl.AddRectFilled(center - half - new Vector2(0, lh * 2.2f), center + half + new Vector2(0, lh * 2f), ImGui.ColorConvertFloat4ToU32(Theme.Panel with { W = 0.92f }), Theme.Px(3));
            dl.AddText(Fonts.Strong, Fonts.BodySize, center - new Vector2(s1.X * 0.5f, lh * 1.4f), ImGui.ColorConvertFloat4ToU32(Theme.Text), line1);
            dl.AddText(center - new Vector2(s2.X * 0.5f, -lh * 0.3f), dim, line2);
        }
    }

    /// <summary>World-axis orientation in the corner: farther axes drawn first and dimmed.</summary>
    void DrawAxisIndicator(ImDrawListPtr dl, Vector2 center)
    {
        var cam = Ctx.Camera;
        float len = Theme.Px(24);
        string[] names = ["X", "Y", "Z"];
        var axes = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ }
            .Select((axis, i) => (i, dir: new Vector2(Vector3.Dot(axis, cam.Right), -Vector3.Dot(axis, cam.Up)), depth: Vector3.Dot(axis, cam.Forward)))
            .OrderByDescending(a => a.depth);
        dl.AddCircleFilled(center, len + Theme.Px(12), ImGui.ColorConvertFloat4ToU32(Theme.Bg with { W = 0.45f }));
        foreach (var (i, dir, depth) in axes)
        {
            uint col = Theme.AxisU32[i];
            if (depth > 0.2f) col = (col & 0x00FFFFFF) | 0x80000000; // pointing away from the viewer
            var tip = center + dir * len;
            dl.AddLine(center, tip, col, Theme.Px(2));
            var ts = ImGui.CalcTextSize(names[i]);
            dl.AddText(center + dir * (len + Theme.Px(8)) - ts * 0.5f, col, names[i]);
        }
    }
}
