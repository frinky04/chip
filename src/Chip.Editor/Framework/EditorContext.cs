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
    /// <summary>
    /// Grid steps, 1 cm to 16 m. Below 25 cm they go 10, 5 and 1 cm (round numbers to work in) rather than halving to
    /// 12.5 and 6.25. 10 cm doesn't nest in 25 cm, but 5 cm nests in both, so it is always one step to get back onto
    /// either. From 25 cm up every size is a whole number of 25 cm.
    /// </summary>
    public static readonly float[] GridSizes = [0.01f, 0.05f, 0.1f, 0.25f, 0.5f, 1, 2, 4, 8, 16];

    /// <summary>
    /// The size of the brighter lines drawn over a grid of <paramref name="minor"/>: a round number the minor size
    /// divides (1 cm → 10 cm, 5 cm → 25 cm, 10 cm → 50 cm), else four steps (25 cm → 1 m, as before).
    /// </summary>
    public static float MajorGrid(float minor) =>
        MathF.Abs(minor - 0.01f) < 1e-5f ? 0.1f
        : MathF.Abs(minor - 0.05f) < 1e-5f ? 0.25f
        : MathF.Abs(minor - 0.1f) < 1e-5f ? 0.5f
        : minor * 4;

    readonly Func<double> _clock;
    readonly List<IViewportTool> _tools = new();
    readonly List<IInspectorSection> _sections = new();
    readonly List<MouseBinding> _mouseBindings = new();
    readonly List<Action<EditorContext>> _panels = new();
    readonly List<IDockPanel> _dockPanels = new();

    public EditorContext(Session session, MaterialLibrary materials, Func<double>? clock = null)
    {
        Session = session;
        Picks = new PickCache(session) { DoubleSided = name => materials.Get(name).DoubleSided };
        Materials = materials;
        ActiveMaterial = materials.All.Count > 0 ? materials.All[0].Name : MaterialLibrary.Missing.Name;
        _clock = clock ?? ImGui.GetTime;
        session.ScopeViolation = msg =>
        {
            Console.Error.WriteLine(msg);
            Status("Undo for that edit may be incomplete (a mesh outside its declared scope changed). Save, then report it.", error: true);
        };
    }

    // ---- Document ----

    public Session Session { get; }
    public Document Doc => Session.Doc;
    public Selection Sel => Session.Sel;
    public MaterialLibrary Materials { get; }

    /// <summary>
    /// Points the material library's default density at the open level's (<see cref="Document.TexelDensity"/>). The host
    /// calls it every frame, so opening, new, undo and redo are all covered.
    /// </summary>
    public void SyncTexelDensity()
    {
        if (Materials.DefaultTexelsPerMeter != Doc.TexelDensity) Materials.DefaultTexelsPerMeter = Doc.TexelDensity;
    }
    /// <summary>Material used by new geometry and by Apply.</summary>
    public string ActiveMaterial { get; set; }

    // ---- Registries ----

    public CommandRegistry Commands { get; } = new();
    public IReadOnlyList<IViewportTool> Tools => _tools;
    public IReadOnlyList<IInspectorSection> Sections => _sections;
    public IReadOnlyList<MouseBinding> MouseBindings => _mouseBindings;
    /// <summary>
    /// Per-frame callbacks the host runs after the fixed panels: floating windows (replace material, the hotspot editor)
    /// and work that goes on whether or not a panel shows (thumbnail uploads, highlights).
    /// </summary>
    public IReadOnlyList<Action<EditorContext>> Panels => _panels;

    public void AddPanel(Action<EditorContext> draw) => _panels.Add(draw);

    /// <summary>Tabs of the left dock, in order.</summary>
    public IReadOnlyList<IDockPanel> DockPanels => _dockPanels;

    public void AddDockPanel(IDockPanel panel)
    {
        if (_dockPanels.Any(p => p.Id == panel.Id)) throw new InvalidOperationException($"Duplicate dock panel '{panel.Id}'");
        _dockPanels.Add(panel);
        _dockPanels.Sort((a, b) => a.Order.CompareTo(b.Order));
    }

    string? _dockTab;

    /// <summary>The dock tab showing (or that shows when the dock opens), the first tab until one is picked. The host persists it.</summary>
    public string? DockTab
    {
        get => _dockTab ?? (_dockPanels.Count > 0 ? _dockPanels[0].Id : null);
        set => _dockTab = value;
    }
    /// <summary>False when the dock is collapsed to its icon rail.</summary>
    public bool DockOpen { get; set; } = true;

    /// <summary>Whether a dock tab is on screen.</summary>
    public bool DockShowing(string id) => DockOpen && DockTab == id;

    /// <summary>Opens the dock on a tab.</summary>
    public void ShowDock(string id)
    {
        DockOpen = true;
        DockTab = id;
    }

    /// <summary>Opens the dock on a tab, or collapses the dock when that tab is already showing.</summary>
    public void ToggleDock(string id)
    {
        if (DockShowing(id)) DockOpen = false;
        else ShowDock(id);
    }

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
        ActiveTool?.Deactivate(this);
        ActiveTool = tool;
        tool.Activate(this);
    }

    /// <summary>Activates the tool, or returns to the default tool if it is already active.</summary>
    public void ToggleTool(IViewportTool tool) => SetTool(ActiveTool == tool && DefaultTool != null ? DefaultTool : tool);

    public void SetTool(string id)
    {
        if (_tools.Find(t => t.Id == id) is { } t) SetTool(t);
    }

    /// <summary>Abandons whatever the active tool is doing (used before undo, new, open). True if something was in progress.</summary>
    public bool CancelInteraction() => ActiveTool?.Cancel(this) ?? false;

    /// <summary>Switches selection mode (converting the selection) and returns to the default tool.</summary>
    public void SetMode(SelectMode mode)
    {
        Sel.ConvertTo(mode, Doc);
        if (DefaultTool != null) SetTool(DefaultTool);
    }

    // ---- View settings ----

    public float Grid { get; set; } = 0.25f;
    public bool Snap { get; set; } = true;

    /// <summary>
    /// What gizmo moves and nudges line up with: World puts the selection on world grid lines, Relative moves it by
    /// whole grid steps from where it was. The gizmo holds it so its maths stays testable on its own.
    /// </summary>
    public SnapSpace SnapSpace { get => Gizmo.SnapSpace; set => Gizmo.SnapSpace = value; }
    /// <summary>Scythe-style backface selection: picking can hit faces pointing away (rendering never changes). On by default.</summary>
    public bool Backfaces { get; set; } = true;

    /// <summary>
    /// The level dressed by its rules file (docs/dressing.md), re-dressed lazily when the document or the rules change;
    /// null when the level has no rules file. Export always uses it; the viewport only when <see cref="ShowDressed"/>.
    /// </summary>
    public Func<Core.Dressing.DressedLevel?> Dressed { get; set; } = () => null;

    /// <summary>Viewport shows the dressed level (true) or the bare blockout. Editing always works on the blockout.</summary>
    public bool ShowDressed { get; set; } = true;

    /// <summary>
    /// Blockout faces the viewport tints this frame (dressing: proxy stripes, the hovered rule's targets, tag colours).
    /// Return the same list while nothing changed; a new list rebuilds the overlay.
    /// </summary>
    public Func<IReadOnlyList<Rendering.FaceTint>?> ViewportTints { get; set; } = () => null;

    /// <summary>What exports write: the dressed level when the level has rules, else the document itself.</summary>
    public Document ExportDocument() => Dressed() is { } dressed ? dressed.ForExport() : Doc;
    /// <summary>The active viewport pane's camera. The view layout swaps it when another pane becomes active.</summary>
    public Camera Camera { get; set; } = new();
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
    /// <summary>Mouse wheel steps this frame, for a tool with <see cref="IViewportTool.CapturesWheel"/> (the camera doesn't see them).</summary>
    public float ToolWheel { get; set; }

    // ---- Rename (F2) ----

    Action? _renameOffer;
    bool _renameOfferHovered;

    /// <summary>
    /// Panels call this while drawing to say what Rename (F2) renames this frame: the row under the mouse
    /// (<paramref name="hovered"/>) wins over the item a focused panel has picked. With no offer, Rename falls back to the
    /// selected object. The host clears offers at the start of each frame.
    /// </summary>
    public void OfferRename(Action rename, bool hovered)
    {
        if (_renameOffer != null && (_renameOfferHovered || !hovered)) return;
        _renameOffer = rename;
        _renameOfferHovered = hovered;
    }

    public bool HasRenameOffer => _renameOffer != null;

    /// <summary>Runs this frame's rename offer, if a panel made one. Returns whether it ran.</summary>
    public bool TakeRenameOffer()
    {
        if (_renameOffer is not { } rename) return false;
        ClearRenameOffer();
        rename();
        return true;
    }

    public void ClearRenameOffer() => (_renameOffer, _renameOfferHovered) = (null, false);

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
        foreach (var e in Sel.Entities(Doc)) // entities count by their origin
        {
            min = Vector3.Min(min, e.Position);
            max = Vector3.Max(max, e.Position);
            any = true;
        }
        return any ? (min, max) : null;
    }

    /// <summary>Temporary pivot (Scythe: MMB-drag the gizmo). Cleared by the viewport when the selection changes.</summary>
    public Vector3? PivotOverride { get; set; }

    public Vector3? SelectionPivot() => SelectionBounds() is { } b ? PivotOverride ?? (b.Min + b.Max) * 0.5f : null;

    public Ray MouseRay() => View.ScreenRay(Mouse);

    public PickCache Picks { get; }

    /// <summary>Face under the mouse, honouring backface mode. <paramref name="selectable"/> skips locked meshes.</summary>
    public FaceHit? RaycastFace(bool selectable = false) => Picks.RaycastFace(MouseRay(), Backfaces, selectable);

    /// <summary>Element of the current mode under the mouse (in object mode, one <see cref="PickFilter"/> allows).</summary>
    public ElementRef? PickAtMouse(float pixelScale) => Picks.Pick(View, Mouse, Sel.Mode, pixelScale, Backfaces, PickFilter);

    /// <summary>
    /// What object mode picks, box selects and selects all: everything, plain geometry, or entities (and mesh entities'
    /// meshes). Component modes ignore it.
    /// </summary>
    public PickFilter PickFilter { get; set; }

    /// <summary>Runs one undoable edit over the faces the selection addresses (faces, or all faces of objects).</summary>
    public void ForFaces(string label, Action<PolyMesh, List<int>> edit)
    {
        if (Sel.FaceSets(Doc).Count == 0) { Status($"Select faces or objects to {label.ToLowerInvariant()}"); return; }
        Session.Do(label, Sel.MeshIds(), () =>
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
        if (ViewportHovered && RaycastFace() is { } hit) { Camera.FocusOn(hit.Point); return; }
        FrameAll();
    }
}
