using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.Rendering;
using Chip.Editor.UI;
using Chip.Editor.Viewport;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;

namespace Chip.Editor;

/// <summary>
/// The host: window, GL, frame loop, fixed panel layout and input routing. Behaviour lives in features
/// (see <see cref="IEditorFeature"/>), which act through <see cref="Ctx"/>. Panels are in <c>Shell/</c>.
/// </summary>
public sealed partial class EditorApp : IDisposable
{
    readonly IWindow _window;
    readonly GL _gl;
    readonly IInputContext _input;
    readonly ImGuiController _imgui;
    readonly Renderer _renderer;
    readonly Navigator _nav = new();
    string _title = "";

    public EditorContext Ctx { get; }

    public EditorApp(IWindow window)
    {
        _window = window;
        _gl = window.CreateOpenGL();
        _input = window.CreateInput();
        float scale = Dpi.ScaleFor(window);
        _imgui = new ImGuiController(_gl, window, _input, null, () => ConfigureImGui(scale));
        Theme.Apply(scale);

        var lib = MaterialLibrary.LoadFolder(Path.Combine(AppContext.BaseDirectory, "assets", "textures"));
        _renderer = new Renderer(_gl, lib);
        var session = new Session();
        session.Reset(new Document(), null);
        Ctx = new EditorContext(session, lib)
        {
            TextureId = name => _renderer.TextureId(name),
            UploadTexture = (w, h, rgba) => _renderer.UploadTexture(w, h, rgba),
            ConfirmDiscard = ConfirmDiscard,
            Quit = () => _window.Close(),
        };
        _nav.CaptureCursor = SetCursorCaptured;
        FeatureLoader.LoadAll(Ctx);

        _window.Closing += OnClosing;
        DarkTitleBar.Apply(window);
    }

    static void ConfigureImGui(float scale)
    {
        var io = ImGui.GetIO();
        io.ConfigWindowsMoveFromTitleBarOnly = true;
        Fonts.Load(Path.Combine(AppContext.BaseDirectory, "assets", "fonts"), scale);
        unsafe { io.NativePtr->IniFilename = null; } // fixed layout; nothing to persist
    }

    public void Frame(float dt)
    {
        SyncModifiers();
        _imgui.Update(dt);
        var bg = Theme.Bg;
        _gl.ClearColor(bg.X, bg.Y, bg.Z, 1);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        DrawMenuBar();
        var vp = ImGui.GetMainViewport();
        float toolW = Theme.Px(44), inspectorW = Theme.Px(320), statusH = Theme.Px(30);
        var pos = vp.WorkPos;
        var size = vp.WorkSize;
        DrawToolStrip(pos, new Vector2(toolW, size.Y - statusH));
        DrawInspector(new Vector2(pos.X + size.X - inspectorW, pos.Y), new Vector2(inspectorW, size.Y - statusH));
        DrawStatusBar(new Vector2(pos.X, pos.Y + size.Y - statusH), new Vector2(size.X, statusH));
        DrawViewport(new Vector2(pos.X + toolW, pos.Y), new Vector2(size.X - toolW - inspectorW, size.Y - statusH), dt);
        foreach (var panel in Ctx.Panels) panel(Ctx);
        HandleHotkeys();
        DrawConfirmModal();
        DrawShortcutsSheet();
        UpdateTitle();

        var fb = _window.FramebufferSize;
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _captureFbo);
        _gl.Viewport(0, 0, (uint)fb.X, (uint)fb.Y);
        if (_captureFbo != 0) _gl.Clear(ClearBufferMask.ColorBufferBit);
        _imgui.Render();
    }

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

    void HandleHotkeys()
    {
        var io = ImGui.GetIO();
        // Modals own the keyboard (Esc closes them, not the selection).
        if (io.WantTextInput || _nav.Flying || ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId)) return;
        Ctx.Commands.Match(h => h.IsPressed(), Ctx.Interacting)?.Execute();
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

    /// <summary>
    /// Silk.NET's ImGui backend assigns io.KeyCtrl/KeyShift/KeyAlt directly, which ImGui 1.89+ overwrites in NewFrame
    /// from ImGuiMod_* key events it never sends, so every modifier read as up. Queue those events ourselves before Update.
    /// </summary>
    void SyncModifiers()
    {
        if (_input.Keyboards.Count == 0) return;
        var kb = _input.Keyboards[0];
        var io = ImGui.GetIO();
        io.AddKeyEvent(ImGuiKey.ModCtrl, kb.IsKeyPressed(Key.ControlLeft) || kb.IsKeyPressed(Key.ControlRight));
        io.AddKeyEvent(ImGuiKey.ModShift, kb.IsKeyPressed(Key.ShiftLeft) || kb.IsKeyPressed(Key.ShiftRight));
        io.AddKeyEvent(ImGuiKey.ModAlt, kb.IsKeyPressed(Key.AltLeft) || kb.IsKeyPressed(Key.AltRight));
        io.AddKeyEvent(ImGuiKey.ModSuper, kb.IsKeyPressed(Key.SuperLeft) || kb.IsKeyPressed(Key.SuperRight));
    }

    void SetCursorCaptured(bool captured)
    {
        if (_input.Mice.Count > 0) _input.Mice[0].Cursor.CursorMode = captured ? CursorMode.Raw : CursorMode.Normal;
    }

    void UpdateTitle()
    {
        string name = Ctx.Session.Path != null ? Path.GetFileName(Ctx.Session.Path) : "untitled";
        string title = $"Chip — {name}{(Ctx.Session.Dirty ? " *" : "")}";
        if (title != _title) _window.Title = _title = title;
    }

    const ImGuiWindowFlags FixedPanel = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus;

    public void Dispose()
    {
        _renderer.Dispose();
        _imgui.Dispose();
        _input.Dispose();
        _gl.Dispose();
    }
}
