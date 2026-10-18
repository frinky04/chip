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

    void HandleHotkeys()
    {
        var io = ImGui.GetIO();
        // Modals own the keyboard (Esc closes them, not the selection).
        if (io.WantTextInput || _nav.Flying || ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId)) return;
        Ctx.Commands.Match(h => h.IsPressed(), Ctx.Interacting)?.Execute();
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
