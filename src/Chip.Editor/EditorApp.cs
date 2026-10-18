using System.Numerics;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Editor.Rendering;
using Chip.Editor.UI;
using Chip.Editor.Viewport;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;

namespace Chip.Editor;

/// <summary>Frame loop, viewport input routing and tool state. Commands live in EditorApp.Commands, panels in EditorApp.Panels.</summary>
public sealed partial class EditorApp : IDisposable
{
    enum Tool { Select, Block }

    readonly IWindow _window;
    readonly GL _gl;
    readonly IInputContext _input;
    readonly ImGuiController _imgui;
    readonly Session _session = new();
    readonly MaterialLibrary _lib;
    readonly Renderer _renderer;
    readonly Camera _cam = new();
    readonly Gizmo _gizmo = new();
    readonly BlockTool _block = new();

    Tool _tool = Tool.Select;
    string _activeMaterial;
    float _grid = 0.25f;
    bool _backfaces;
    bool _snap = true;

    ViewInfo _view;
    Vector2 _vpOrigin, _vpSize;
    bool _vpHovered;
    ElementRef? _hover;
    TransformGesture? _gesture;
    bool _flying, _panning, _orbiting, _marqueeArmed, _marquee;
    Vector3 _orbitPivot;
    float _panDepth;
    Vector2 _pressPos;

    string _title = "";

    public EditorApp(IWindow window)
    {
        _window = window;
        _gl = window.CreateOpenGL();
        _input = window.CreateInput();
        float scale = Dpi.ScaleFor(window);
        _imgui = new ImGuiController(_gl, window, _input, null, () => ConfigureImGui(scale));
        Theme.Apply(scale);

        _lib = MaterialLibrary.LoadFolder(Path.Combine(AppContext.BaseDirectory, "assets", "textures"));
        _activeMaterial = _lib.All[0].Name;
        _renderer = new Renderer(_gl, _lib);
        _session.Reset(new Document(), null);
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
        _gl.BindFramebuffer(Silk.NET.OpenGL.FramebufferTarget.Framebuffer, _captureFbo);
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
        _vpOrigin = ImGui.GetCursorScreenPos();
        _vpSize = Vector2.Max(ImGui.GetContentRegionAvail(), Vector2.One);
        var mousePos = ImGui.GetIO().MousePos;
        bool overView = mousePos.X >= _vpOrigin.X && mousePos.Y >= _vpOrigin.Y && mousePos.X < _vpOrigin.X + _vpSize.X && mousePos.Y < _vpOrigin.Y + _vpSize.Y;
        _vpHovered = overView && ImGui.IsWindowHovered() && !ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId);
        _view = _cam.View(_vpSize);

        HandleViewportInput(dt);
        _view = _cam.View(_vpSize);

        uint tex = _renderer.Render(_vpSize, _view, _session, _hover, _grid, _backfaces);
        ImGui.Image((nint)tex, _vpSize, new Vector2(0, 1), new Vector2(1, 0));

        var dl = ImGui.GetWindowDrawList();
        if (_tool == Tool.Select && SelectionPivot() is { } pivot) _gizmo.Draw(dl, _vpOrigin, _view, pivot);
        if (_tool == Tool.Block) _block.Draw(dl, _vpOrigin, _view);
        if (_marquee)
        {
            var a = _vpOrigin + _pressPos;
            var b = ImGui.GetIO().MousePos;
            dl.AddRectFilled(Vector2.Min(a, b), Vector2.Max(a, b), (Theme.AccentU32 & 0x00FFFFFF) | 0x22000000);
            dl.AddRect(Vector2.Min(a, b), Vector2.Max(a, b), Theme.AccentU32);
        }
        DrawViewportHud(dl);
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    void DrawViewportHud(ImDrawListPtr dl)
    {
        var p = _vpOrigin + Theme.Px(12, 10);
        uint dim = ImGui.ColorConvertFloat4ToU32(Theme.TextDim);
        DrawAxisIndicator(dl, _vpOrigin + new Vector2(Theme.Px(40), _vpSize.Y - Theme.Px(40)));
        if (_flying) dl.AddText(p, Theme.AccentU32, $"Flying at {_cam.FlySpeed:0.#} m/s, scroll to change speed");

        if (_session.Doc.Meshes.Count == 0 && _tool == Tool.Select)
        {
            var center = _vpOrigin + _vpSize * 0.5f;
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
        float len = Theme.Px(24);
        string[] names = ["X", "Y", "Z"];
        var axes = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ }
            .Select((axis, i) => (i, dir: new Vector2(Vector3.Dot(axis, _cam.Right), -Vector3.Dot(axis, _cam.Up)), depth: Vector3.Dot(axis, _cam.Forward)))
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

    static string ModeName(SelectMode m) => m switch
    {
        SelectMode.Vertex => "Vertex", SelectMode.Edge => "Edge", SelectMode.Face => "Face", _ => "Object",
    };

    void HandleViewportInput(float dt)
    {
        var io = ImGui.GetIO();
        var mouse = io.MousePos - _vpOrigin;
        bool captured = _gesture != null || _flying || _panning || _orbiting || _marqueeArmed || _block.Busy;
        if (!_vpHovered && !captured) { _hover = null; return; }

        // Camera: RMB fly (Shift+RMB is lift material), MMB pan, Alt+LMB orbit, wheel dolly.
        if (_vpHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            if (io.KeyShift) LiftMaterial(mouse);
            else { _flying = true; SetCursorCaptured(true); }
        }
        if (_flying)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Right)) { _flying = false; SetCursorCaptured(false); }
            else
            {
                _cam.Rotate(io.MouseDelta.X * 0.0032f, -io.MouseDelta.Y * 0.0032f);
                var move = Vector3.Zero;
                if (ImGui.IsKeyDown(ImGuiKey.W)) move += _cam.Forward;
                if (ImGui.IsKeyDown(ImGuiKey.S)) move -= _cam.Forward;
                if (ImGui.IsKeyDown(ImGuiKey.D)) move += _cam.Right;
                if (ImGui.IsKeyDown(ImGuiKey.A)) move -= _cam.Right;
                if (ImGui.IsKeyDown(ImGuiKey.E)) move += Vector3.UnitY;
                if (ImGui.IsKeyDown(ImGuiKey.Q)) move -= Vector3.UnitY;
                if (io.MouseWheel != 0) _cam.FlySpeed = Math.Clamp(_cam.FlySpeed * (io.MouseWheel > 0 ? 1.25f : 0.8f), 0.5f, 256);
                if (move != Vector3.Zero) _cam.Position += Vector3.Normalize(move) * _cam.FlySpeed * (io.KeyShift ? 3f : 1f) * dt;
                _hover = null;
                return;
            }
        }

        if (_vpHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
        {
            _panning = true;
            _panDepth = Picker.RaycastFace(_session.Doc, _view.ScreenRay(mouse), _backfaces)?.Distance ?? 8;
        }
        if (_panning)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Middle)) _panning = false;
            else
            {
                float upp = _cam.UnitsPerPixel(_panDepth, _vpSize.Y);
                _cam.Position += (-_cam.Right * io.MouseDelta.X + _cam.Up * io.MouseDelta.Y) * upp;
                _hover = null;
                return;
            }
        }

        if (_vpHovered && io.KeyAlt && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _orbiting = true;
            _orbitPivot = SelectionPivot()
                ?? Picker.RaycastFace(_session.Doc, _view.ScreenRay(mouse), _backfaces)?.Point
                ?? _cam.Position + _cam.Forward * 8;
        }
        if (_orbiting)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _orbiting = false;
            else
            {
                _cam.Orbit(_orbitPivot, io.MouseDelta.X * 0.006f, -io.MouseDelta.Y * 0.006f);
                _hover = null;
                return;
            }
        }

        if (_vpHovered && io.MouseWheel != 0)
        {
            var ray = _view.ScreenRay(mouse);
            float dist = Picker.RaycastFace(_session.Doc, ray, _backfaces)?.Distance ?? 8;
            _cam.Position += ray.Dir * io.MouseWheel * MathF.Max(dist * 0.15f, 0.1f);
        }

        if (_tool == Tool.Block)
        {
            _hover = null;
            if (_block.Update(_view, _session.Doc, mouse, _vpHovered, _grid, _snap, _backfaces) is { } box) CreateBlock(box.Min, box.Max);
            return;
        }

        // Select tool: an active gizmo drag owns the mouse until release.
        if (_gesture != null)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var (m, rigid) = _gizmo.DragTransform(_view, mouse, _grid, _snap);
                _gesture.Apply(m, rigid);
            }
            else EndGesture(commit: true);
            return;
        }

        var pivot = SelectionPivot();
        if (pivot is { } pv) _gizmo.UpdateHover(_view, pv, mouse);
        if (_vpHovered && pivot != null && _gizmo.Hover != Handle.None && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            bool extrude = io.KeyShift && _session.Sel.Mode == SelectMode.Face && _gizmo.Mode == GizmoMode.Translate;
            string label = extrude ? "Extrude" : _gizmo.Mode switch { GizmoMode.Rotate => "Rotate", GizmoMode.Scale => "Scale", _ => "Move" };
            _gesture = TransformGesture.Start(_session, label, extrude);
            if (_gesture != null) _gizmo.BeginDrag(_gizmo.Hover, _view, _gesture.Pivot, mouse);
            return;
        }

        _hover = _vpHovered && (pivot == null || _gizmo.Hover == Handle.None) ? Picker.Pick(_session.Doc, _view, mouse, _session.Sel.Mode, Theme.Scale, _backfaces) : null;

        if (_vpHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _marqueeArmed = true;
            _marquee = false;
            _pressPos = mouse;
        }
        if (_marqueeArmed)
        {
            if (Vector2.Distance(mouse, _pressPos) > Theme.Px(4)) _marquee = true;
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var picked = _marquee
                    ? Picker.Marquee(_session.Doc, _view, _pressPos, mouse, _session.Sel.Mode, _backfaces)
                    : _hover is { } h ? [h] : new List<ElementRef>();
                ApplyPick(picked, io.KeyShift, io.KeyCtrl);
                _marqueeArmed = _marquee = false;
            }
        }
    }

    void ApplyPick(List<ElementRef> picked, bool add, bool toggle)
    {
        var sel = _session.Sel;
        if (!add && !toggle) sel.Clear();
        foreach (var e in picked)
            if (toggle) sel.Toggle(e);
            else sel.Add(e);
    }

    void EndGesture(bool commit)
    {
        if (_gesture == null) return;
        if (commit) _gesture.Commit();
        else _gesture.Cancel();
        _gesture = null;
        _gizmo.EndDrag();
    }

    Vector3? SelectionPivot()
    {
        var b = SelectionBounds();
        return b is { } bb ? (bb.Min + bb.Max) * 0.5f : null;
    }

    (Vector3 Min, Vector3 Max)? SelectionBounds()
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool any = false;
        foreach (var (mesh, verts) in _session.Sel.VertexSets(_session.Doc))
            foreach (var v in verts)
            {
                var p = mesh.Positions[v];
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
                any = true;
            }
        return any ? (min, max) : null;
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
        string name = _session.Path != null ? Path.GetFileName(_session.Path) : "untitled";
        string title = $"Chip — {name}{(_session.Dirty ? " *" : "")}";
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
