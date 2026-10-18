using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Viewport;
using ImGuiNET;

namespace Chip.Editor.Framework;

/// <summary>
/// Everything a feature may use, in one place. Features register against it and act through it; they never reach into
/// the host (<c>EditorApp</c>). It holds no GL or window state, so it can be built in tests.
/// Geometry edits still go through <see cref="Session"/> (one undo step per gesture).
/// </summary>
public sealed class EditorContext
{
    /// <summary>Metric powers of two: 1/32 m (~3 cm) to 16 m.</summary>
    public static readonly float[] GridSizes = [1 / 32f, 1 / 16f, 1 / 8f, 1 / 4f, 1 / 2f, 1, 2, 4, 8, 16];

    readonly Func<double> _clock;
    readonly List<IViewportTool> _tools = new();
    readonly List<IInspectorSection> _sections = new();
    readonly List<MouseBinding> _mouseBindings = new();
    readonly List<Action<EditorContext>> _panels = new();

    public EditorContext(Session session, MaterialLibrary materials, Func<double>? clock = null)
    {
        Session = session;
        Materials = materials;
        ActiveMaterial = materials.All.Count > 0 ? materials.All[0].Name : MaterialLibrary.Missing.Name;
        _clock = clock ?? ImGui.GetTime;
    }

    // ---- Document ----

    public Session Session { get; }
    public Document Doc => Session.Doc;
    public Selection Sel => Session.Sel;
    public MaterialLibrary Materials { get; }
    /// <summary>Material used by new geometry and by Apply.</summary>
    public string ActiveMaterial { get; set; }

    // ---- Registries ----

    public CommandRegistry Commands { get; } = new();
    public IReadOnlyList<IViewportTool> Tools => _tools;
    public IReadOnlyList<IInspectorSection> Sections => _sections;
    public IReadOnlyList<MouseBinding> MouseBindings => _mouseBindings;
    /// <summary>Floating windows (e.g. the material browser) the host draws every frame after the fixed panels.</summary>
    public IReadOnlyList<Action<EditorContext>> Panels => _panels;

    public void AddPanel(Action<EditorContext> draw) => _panels.Add(draw);

    /// <summary>Registers a tool and its "Tools" menu command. The first tool registered with id "select" is the default.</summary>
    public void AddTool(IViewportTool tool)
    {
        if (_tools.Any(t => t.Id == tool.Id)) throw new InvalidOperationException($"Duplicate tool id '{tool.Id}'");
        _tools.Add(tool);
        _tools.Sort((a, b) => a.Order.CompareTo(b.Order));
        if (tool.Id == "select") DefaultTool = tool;
        ActiveTool ??= tool;
        Commands.Add(new Command
        {
            Id = $"tool.{tool.Id}", Label = tool.Name, Menu = "Tools", Group = "1-tools", Order = tool.Order,
            Hotkeys = tool.Hotkey == null ? [] : [Hotkey.Parse(tool.Hotkey)],
            Execute = () => ToggleTool(tool), IsChecked = () => ActiveTool == tool,
        });
    }

    public void AddSection(IInspectorSection section)
    {
        if (_sections.Any(s => s.Id == section.Id)) throw new InvalidOperationException($"Duplicate inspector section '{section.Id}'");
        _sections.Add(section);
        _sections.Sort((a, b) => a.Order.CompareTo(b.Order));
    }

    public void AddMouseBinding(MouseBinding binding)
    {
        if (_mouseBindings.Any(b => b.Button == binding.Button && b.Ctrl == binding.Ctrl && b.Shift == binding.Shift && b.Alt == binding.Alt))
            throw new InvalidOperationException($"Mouse binding {binding.KeysText} is already taken");
        _mouseBindings.Add(binding);
        Commands.AddReference(binding.Category, binding.KeysText, binding.Label);
    }

    // ---- Tools ----

    public IViewportTool? ActiveTool { get; private set; }
    public IViewportTool? DefaultTool { get; private set; }
    public bool Interacting => ActiveTool?.Busy ?? false;

    public void SetTool(IViewportTool tool)
    {
        if (ActiveTool == tool) return;
        ActiveTool?.Cancel(this);
        ActiveTool = tool;
        tool.Activate(this);
    }

    /// <summary>Activates the tool, or returns to the default tool if it is already active.</summary>
    public void ToggleTool(IViewportTool tool) => SetTool(ActiveTool == tool && DefaultTool != null ? DefaultTool : tool);

    public void SetTool(string id)
    {
        if (_tools.Find(t => t.Id == id) is { } t) SetTool(t);
    }

    /// <summary>Abandons whatever the active tool is doing (used before undo, new, open).</summary>
    public void CancelInteraction() => ActiveTool?.Cancel(this);

    /// <summary>Switches selection mode (converting the selection) and returns to the default tool.</summary>
    public void SetMode(SelectMode mode)
    {
        Sel.ConvertTo(mode, Doc);
        if (DefaultTool != null) SetTool(DefaultTool);
    }

    // ---- View settings ----

    public float Grid { get; set; } = 0.25f;
    public bool Snap { get; set; } = true;
    /// <summary>Scythe-style backface selection: picking and rendering include faces pointing away.</summary>
    public bool Backfaces { get; set; }
    public Camera Camera { get; } = new();
    public Gizmo Gizmo { get; } = new();

    public void StepGrid(int dir)
    {
        int i = Array.IndexOf(GridSizes, Grid);
        Grid = GridSizes[Math.Clamp((i < 0 ? 3 : i) + dir, 0, GridSizes.Length - 1)];
    }

    // ---- Per-frame viewport state (written by the host before tools update) ----

    public ViewInfo View { get; set; }
    public Vector2 ViewportOrigin { get; set; }
    public Vector2 ViewportSize { get; set; } = Vector2.One;
    public bool ViewportHovered { get; set; }
    /// <summary>Mouse position relative to the viewport's top-left, in pixels.</summary>
    public Vector2 Mouse { get; set; }
    /// <summary>Element under the cursor, highlighted by the renderer. Tools set it.</summary>
    public ElementRef? Hover { get; set; }

    // ---- Status and host requests ----

    public string StatusMessage { get; private set; } = "";
    public bool StatusIsError { get; private set; }
    public double StatusUntil { get; private set; }
    public double Now => _clock();

    /// <summary>Report a result in the status bar. Errors stay until dismissed and should say how to fix the problem.</summary>
    public void Status(string message, bool error = false)
    {
        StatusMessage = message;
        StatusIsError = error;
        StatusUntil = Now + (error ? double.MaxValue : 4);
    }

    public void ClearStatus() => StatusMessage = "";

    /// <summary>Runs <c>then</c> now, or after the user resolves unsaved changes. The host supplies the dialog.</summary>
    public Action<Action> ConfirmDiscard { get; set; } = then => then();
    public Action Quit { get; set; } = () => { };
    public bool ShowShortcuts { get; set; }

    /// <summary>Extra texture folders from Preferences (besides the bundled <c>assets/textures</c>). The host fills this from settings.</summary>
    public IReadOnlyList<string> TextureFolders { get; set; } = [];

    /// <summary>ImGui texture handle for a material's preview. The host wires this to the renderer.</summary>
    public Func<string, nint> TextureId { get; set; } = _ => 0;

    /// <summary>Uploads top-down RGBA8 pixels (width, height, data) as an ImGui texture handle, for thumbnails. 0 when headless.</summary>
    public Func<int, int, byte[], nint> UploadTexture { get; set; } = (_, _, _) => 0;

    // ---- Shared helpers ----

    public (Vector3 Min, Vector3 Max)? SelectionBounds()
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool any = false;
        foreach (var (mesh, verts) in Sel.VertexSets(Doc))
            foreach (var v in verts)
            {
                var p = mesh.Positions[v];
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
                any = true;
            }
        return any ? (min, max) : null;
    }

    public Vector3? SelectionPivot() => SelectionBounds() is { } b ? (b.Min + b.Max) * 0.5f : null;

    public Ray MouseRay() => View.ScreenRay(Mouse);

    /// <summary>Face under the mouse, honouring backface mode. <paramref name="selectable"/> skips locked meshes.</summary>
    public FaceHit? RaycastFace(bool selectable = false) => Picker.RaycastFace(Doc, MouseRay(), Backfaces, selectable);

    /// <summary>Element of the current mode under the mouse.</summary>
    public ElementRef? PickAtMouse(float pixelScale) => Picker.Pick(Doc, View, Mouse, Sel.Mode, pixelScale, Backfaces);

    /// <summary>Runs one undoable edit over the faces the selection addresses (faces, or all faces of objects).</summary>
    public void ForFaces(string label, Action<PolyMesh, List<int>> edit)
    {
        if (Sel.FaceSets(Doc).Count == 0) { Status($"Select faces or objects to {label.ToLowerInvariant()}"); return; }
        Session.Do(label, () =>
        {
            foreach (var (mesh, faces) in Sel.FaceSets(Doc)) edit(mesh, faces);
        });
    }

    public void EditMapping(string label, Func<TextureMapping, TextureMapping> edit) => ForFaces(label, (mesh, faces) =>
    {
        foreach (var f in faces) mesh.Faces[f].Map = edit(mesh.Faces[f].Map);
    });

    public void FrameAll()
    {
        var visible = Doc.Meshes.Where(m => !m.Hidden && m.Positions.Count > 0).ToList();
        if (visible.Count == 0) { Camera.Frame(new Vector3(-4), new Vector3(4)); return; }
        Camera.Frame(visible.Select(m => m.Bounds().Min).Aggregate(Vector3.Min), visible.Select(m => m.Bounds().Max).Aggregate(Vector3.Max));
    }

    /// <summary>Frames the selection, else the point under the cursor, else everything.</summary>
    public void Focus()
    {
        if (SelectionBounds() is { } b) { Camera.Frame(b.Min, b.Max); return; }
        if (ViewportHovered && RaycastFace() is { } hit) { Camera.Position = hit.Point - Camera.Forward * 4; return; }
        FrameAll();
    }
}
