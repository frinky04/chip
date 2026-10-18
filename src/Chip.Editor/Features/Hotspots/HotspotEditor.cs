using System.Numerics;
using System.Runtime.CompilerServices;
using Chip.Core.Hotspots;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Hotspots;

/// <summary>
/// The hotspot editor (Ctrl+Shift+H): a floating window over the viewport for one hotspot asset. The texture fills a
/// canvas with a pixel grid; LMB-drag on empty texture draws a patch, LMB on a patch selects and moves it, its handles
/// resize it, RMB/MMB pans and the wheel zooms. The toolbar holds the flags new patches get (they also toggle the
/// selected patch) and the view toggles; the side column lists patches as <c>[R][X][Y][T][inset]</c>, edits the selected
/// one, the asset's target materials, and applies the selected patch to the selected faces. Edits are kept in a working
/// copy that Shift+H already uses; Save writes the <c>.hotspot.json</c>. Keys below act only while the window has focus.
/// </summary>
public sealed class HotspotEditor
{
    static readonly ConditionalWeakTable<EditorContext, HotspotEditor> Instances = new();

    public static HotspotEditor For(EditorContext ctx) => Instances.GetValue(ctx, _ => new HotspotEditor());

    enum Drag { None, Draw, Move, Resize, Pan }

    static readonly int[] GridSizes = [4, 8, 16, 32, 64, 128, 256];
    static readonly uint[] RectTags = [0xFFA8A0A0, 0xFFA8A0A0, 0xFFA8A0A0, 0xFFA8A0A0];

    // New-patch defaults (Alt+A / Alt+D / Alt+E / Alt+T) and inset.
    bool _defRotate, _defMirrorX, _defMirrorY, _defTiling;
    float _defInset = 4;

    // View: screen = centre + (n - pan) * aspect * zoom, with n the normalised texture position.
    Vector2 _pan = new(0.5f);
    float _zoom;
    Vector2 _aspect = Vector2.One;
    Vector2 _c0, _c1;
    bool _frameNext = true, _wasOpen;
    int _grid = 3;
    bool _lit, _hideTexture, _hidePatches;

    // Gesture
    Drag _drag;
    Vector2 _press;
    int _handle = -1;
    (float X, float Y, float W, float H) _start;

    public bool Open { get; set; }
    /// <summary>Index of the selected patch in the edited asset, or -1.</summary>
    public int Selected { get; set; } = -1;

    public static void RegisterReferences(CommandRegistry c)
    {
        c.AddReference("Hotspot editor", "drag", "draw a patch (drag a patch or handle to move or resize)");
        c.AddReference("Hotspot editor", "Alt+A", "allow rotation");
        c.AddReference("Hotspot editor", "Alt+D / Alt+E", "allow mirror X / mirror Y");
        c.AddReference("Hotspot editor", "Alt+T", "tiling patch");
        c.AddReference("Hotspot editor", "[ / ]", "grid finer / coarser");
        c.AddReference("Hotspot editor", "Alt+V / Alt+B", "hide texture / hide patches");
        c.AddReference("Hotspot editor", "Del", "delete the patch");
        c.AddReference("Hotspot editor", "F", "frame the texture");
    }

    public void ToggleOpen(EditorContext ctx)
    {
        if (Open) { Open = false; return; }
        Show(ctx, null);
    }

    /// <summary>Opens the editor on <paramref name="asset"/>, or on the active material's asset (else the first one).</summary>
    public void Show(EditorContext ctx, HotspotAsset? asset)
    {
        var state = HotspotState.For(ctx);
        Open = true;
        if (state.Dirty) return; // keep unsaved edits on screen
        asset ??= state.Editing != null ? null : state.AssetFor(ctx.ActiveMaterial) ?? state.Library.Assets.FirstOrDefault();
        if (asset != null) Edit(state, asset);
    }

    void Edit(HotspotState state, HotspotAsset asset)
    {
        state.Editing = ReferenceEquals(asset, state.Editing) ? asset : asset.Clone();
        state.Dirty = false;
        Selected = -1;
        _frameNext = true;
    }

    // ---------------------------------------------------------------- window

    public void Draw(EditorContext ctx)
    {
        if (!Open) { _wasOpen = false; return; }
        var state = HotspotState.For(ctx);
        float margin = Theme.Px(10);
        var vp = ctx.ViewportSize;
        float top = Theme.Px(44);
        var size = new Vector2(MathF.Min(Theme.Px(940), MathF.Max(Theme.Px(560), vp.X - margin * 2)), MathF.Max(Theme.Px(360), vp.Y - top - margin));
        ImGui.SetNextWindowPos(ctx.ViewportOrigin + new Vector2(margin, top), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(size, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(Theme.Px(520, 320), new Vector2(float.MaxValue));
        if (!_wasOpen) ImGui.SetNextWindowFocus();
        _wasOpen = true;
        bool open = true;
        string title = state.Editing is { } e ? $"Hotspot editor: {e.Name}{(state.Dirty ? " *" : "")}" : "Hotspot editor";
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(10, 8));
        bool visible = ImGui.Begin($"{title}###hotspotEditor", ref open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar();
        if (visible)
        {
            bool focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
            DrawTop(ctx, state);
            DrawToolbar(ctx, state);
            var avail = ImGui.GetContentRegionAvail();
            float side = MathF.Min(Theme.Px(270), avail.X * 0.4f);
            float footer = ImGui.GetFrameHeightWithSpacing();
            var body = new Vector2(avail.X - side - Theme.Px(8), avail.Y - footer);
            DrawCanvas(ctx, state, body);
            ImGui.SameLine(0, Theme.Px(8));
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Panel);
            if (ImGui.BeginChild("##hsside", new Vector2(side, body.Y), ImGuiChildFlags.None))
                DrawSide(ctx, state);
            ImGui.EndChild();
            ImGui.PopStyleColor();
            KeyHints([("Drag", "draw patch"), ("Handles", "resize"), ("RMB", "pan"), ("Wheel", "zoom"), ("[ ]", "grid"), ("Del", "delete")]);
            if (focused) HandleKeys(ctx, state);
        }
        ImGui.End();
        if (!open) { Open = false; CancelDrag(state); }
    }

    void DrawTop(EditorContext ctx, HotspotState state)
    {
        var assets = state.AllAssets();
        var editing = state.Editing;
        float gap = Theme.Px(6);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, "Asset");
        ImGui.SameLine(0, gap);
        ImGui.SetNextItemWidth(Theme.Px(240));
        ImGui.BeginDisabled(state.Dirty);
        if (ImGui.BeginCombo("##hsasset", editing == null ? "No hotspot assets" : editing.Name, ImGuiComboFlags.HeightLarge))
        {
            foreach (var a in assets)
                if (ImGui.Selectable($"{Describe(a)}##{a.Path ?? a.Name}", ReferenceEquals(a, editing)))
                    Edit(state, a);
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();
        if (state.Dirty) Tip("Save or revert this asset before opening another");
        ImGui.SameLine(0, gap);
        if (ImGui.Button("New")) NewAsset(ctx, state);
        Tip($"New hotspot asset for the active material ({ctx.ActiveMaterial}), saved beside its texture");
        ImGui.SameLine(0, gap);
        ImGui.BeginDisabled(editing == null || !state.Dirty);
        if (ImGui.Button("Save")) Save(ctx, state);
        Tip(editing?.Path is { } p ? $"Save to {p}" : "Save the asset");
        ImGui.SameLine(0, gap);
        if (ImGui.Button("Revert")) Revert(ctx, state);
        Tip("Discard unsaved changes");
        ImGui.EndDisabled();
        if (editing != null)
        {
            ImGui.SameLine(0, Theme.Px(14));
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextDim, editing.Patches.Count == 1 ? "1 patch" : $"{editing.Patches.Count} patches");
        }
        ImGui.Dummy(Theme.Px(0, 1));
    }

    static string Describe(HotspotAsset a) => a.DisplayMaterial is { } m && !string.Equals(m, a.Name, StringComparison.OrdinalIgnoreCase) ? $"{a.Name}  ({m})" : a.Name;

    void DrawToolbar(EditorContext ctx, HotspotState state)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, "New patches");
        Tip("Flags for patches you draw. The keys also toggle the selected patch");
        ImGui.SameLine(0, Theme.Px(8));
        if (Toggle("hsdefr", Icon.Rotate, "", "Allow rotation: may turn 90° to fit, and 180° at random  (Alt+A)", _defRotate)) ToggleFlag(ctx, state, 'R');
        ImGui.SameLine(0, Theme.Px(2));
        if (Toggle("hsdefx", Icon.FlipU, "", "Mirror X: may mirror horizontally at random  (Alt+D)", _defMirrorX)) ToggleFlag(ctx, state, 'X');
        ImGui.SameLine(0, Theme.Px(2));
        if (Toggle("hsdefy", Icon.FlipV, "", "Mirror Y: may mirror vertically at random  (Alt+E)", _defMirrorY)) ToggleFlag(ctx, state, 'Y');
        ImGui.SameLine(0, Theme.Px(2));
        if (Toggle("hsdeft", Icon.Repeat, "", "Tiling: repeats along its length. Only for patches spanning the full width or height  (Alt+T)", _defTiling)) ToggleFlag(ctx, state, 'T');
        ImGui.SameLine(0, Theme.Px(8));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, "Inset");
        ImGui.SameLine(0, Theme.Px(4));
        ImGui.SetNextItemWidth(Theme.Px(52));
        ImGui.InputFloat("##hsdefinset", ref _defInset, 0, 0, "%.0f");
        _defInset = Math.Clamp(_defInset, 0, 256);
        Tip("Inset for new patches, in pixels at the asset's reference resolution");
        VSeparator();
        if (HotspotSection.TextToggle("hslit", "Lit", "Preview the texture under a soft key light", _lit)) _lit = !_lit;
        ImGui.SameLine(0, Theme.Px(2));
        if (HotspotSection.TextToggle("hstex", "Texture", "Show the texture  (Alt+V)", !_hideTexture)) _hideTexture = !_hideTexture;
        ImGui.SameLine(0, Theme.Px(2));
        if (HotspotSection.TextToggle("hspatch", "Patches", "Show the patches  (Alt+B)", !_hidePatches)) _hidePatches = !_hidePatches;
        ImGui.SameLine(0, Theme.Px(10));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, $"Grid {GridSizes[_grid]} px");
        Tip("[ finer, ] coarser. Hold Ctrl while dragging to place freely");
        ImGui.Dummy(Theme.Px(0, 1));
    }

    // ---------------------------------------------------------------- canvas

    Vector2 Center => (_c0 + _c1) * 0.5f;
    Vector2 ToScreen(Vector2 n) => Center + (n - _pan) * _aspect * _zoom;
    Vector2 ToTex(Vector2 screen) => _pan + (screen - Center) / (_aspect * _zoom);

    static (Vector2 Min, Vector2 Max) Rect(HotspotPatch p) => (new Vector2(p.X, p.Y), new Vector2(p.X + p.W, p.Y + p.H));

    void DrawCanvas(EditorContext ctx, HotspotState state, Vector2 size)
    {
        var asset = state.Editing;
        _c0 = ImGui.GetCursorScreenPos();
        _c1 = _c0 + size;
        ImGui.InvisibleButton("##hscanvas", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        bool hovered = ImGui.IsItemHovered();
        var info = ctx.Materials.Get(asset?.DisplayMaterial ?? ctx.ActiveMaterial);
        _aspect = new Vector2(info.Width, info.Height) / MathF.Max(MathF.Max(info.Width, info.Height), 1);
        if (_frameNext || _zoom <= 0) { FrameTexture(); _frameNext = false; }
        if (asset != null) Interact(state, asset, hovered);

        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(_c0, _c1, true);
        dl.AddRectFilled(_c0, _c1, ImGui.ColorConvertFloat4ToU32(Theme.ViewportBg));
        var t0 = ToScreen(Vector2.Zero);
        var t1 = ToScreen(Vector2.One);
        nint tex = asset != null && !_hideTexture ? ctx.TextureId(info.Name) : 0;
        if (tex != 0) dl.AddImage(tex, t0, t1);
        else dl.AddRectFilled(t0, t1, 0xFF3A3A3E);
        if (_lit && tex != 0)
        {
            // Soft key light from the top left: brighten there, shade toward the bottom right.
            dl.AddRectFilledMultiColor(t0, t1, 0x18FFFFFF, 0x00000000, 0x50000000, 0x00000000);
        }
        if (asset != null)
        {
            DrawGrid(dl, asset);
            if (!_hidePatches) DrawPatches(dl, asset);
            if (_drag == Drag.Draw)
            {
                var (a, b) = DrawRect(asset, ImGui.GetIO().MousePos, ImGui.GetIO().KeyCtrl);
                dl.AddRectFilled(ToScreen(a), ToScreen(b), (Theme.AccentU32 & 0x00FFFFFF) | 0x30000000);
                dl.AddRect(ToScreen(a), ToScreen(b), Theme.AccentU32, 0, ImDrawFlags.None, Theme.Px(1.5f));
                var px = (b - a) * new Vector2(asset.ReferenceWidth, asset.ReferenceHeight);
                dl.AddText(Fonts.Small, Fonts.SmallSize, ToScreen(b) + Theme.Px(6, 2), ImGui.ColorConvertFloat4ToU32(Theme.Text), $"{px.X:0} × {px.Y:0}");
            }
        }
        dl.AddRect(t0, t1, 0x80FFFFFF);

        uint dim = ImGui.ColorConvertFloat4ToU32(Theme.TextDim);
        if (asset == null)
        {
            const string msg = "No hotspot asset. Pick a library texture as the active material, then New";
            var ts = ImGui.CalcTextSize(msg);
            dl.AddText(Center - ts * 0.5f, ImGui.ColorConvertFloat4ToU32(Theme.Text), msg);
        }
        else if (hovered)
        {
            var n = ToTex(ImGui.GetIO().MousePos);
            dl.AddText(new Vector2(_c0.X + Theme.Px(8), _c1.Y - ImGui.GetTextLineHeight() - Theme.Px(6)), dim,
                $"{n.X * asset.ReferenceWidth:0}, {n.Y * asset.ReferenceHeight:0} px");
        }
        dl.PopClipRect();
        dl.AddRect(_c0, _c1, ImGui.ColorConvertFloat4ToU32(Theme.Border));
    }

    Vector2 GridStep(HotspotAsset a) => new(GridSizes[_grid] / (float)a.ReferenceWidth, GridSizes[_grid] / (float)a.ReferenceHeight);

    void DrawGrid(ImDrawListPtr dl, HotspotAsset asset)
    {
        var step = GridStep(asset);
        var px = step * _aspect * _zoom;
        uint col = 0x30FFFFFF;
        if (px.X >= Theme.Px(6))
            for (float x = step.X; x < 1 - 1e-6f; x += step.X)
            {
                float sx = ToScreen(new Vector2(x, 0)).X;
                dl.AddLine(new Vector2(sx, ToScreen(Vector2.Zero).Y), new Vector2(sx, ToScreen(Vector2.One).Y), col);
            }
        if (px.Y >= Theme.Px(6))
            for (float y = step.Y; y < 1 - 1e-6f; y += step.Y)
            {
                float sy = ToScreen(new Vector2(0, y)).Y;
                dl.AddLine(new Vector2(ToScreen(Vector2.Zero).X, sy), new Vector2(ToScreen(Vector2.One).X, sy), col);
            }
    }

    void DrawPatches(ImDrawListPtr dl, HotspotAsset asset)
    {
        int hot = _drag == Drag.None ? PatchAt(asset, ImGui.GetIO().MousePos) : -1;
        uint text = ImGui.ColorConvertFloat4ToU32(Theme.Text);
        for (int i = 0; i < asset.Patches.Count; i++)
        {
            var p = asset.Patches[i];
            var (a, b) = Rect(p);
            var s0 = ToScreen(a);
            var s1 = ToScreen(b);
            bool sel = i == Selected;
            uint line = sel ? Theme.AccentU32 : i == hot ? Theme.HoverU32 : 0xC8F0F0F0;
            dl.AddRectFilled(s0, s1, sel ? (Theme.AccentU32 & 0x00FFFFFF) | 0x28000000 : 0x14FFFFFF);
            var (ix, iy) = asset.InsetFraction(p);
            if (p.TileAxis == TileAxis.U) ix = 0;
            if (p.TileAxis == TileAxis.V) iy = 0;
            if (ix > 0 || iy > 0)
                dl.AddRect(ToScreen(a + new Vector2(ix, iy)), ToScreen(b - new Vector2(ix, iy)), (line & 0x00FFFFFF) | 0x60000000);
            dl.AddRect(s0, s1, line, 0, ImDrawFlags.None, Theme.Px(sel ? 2f : 1f));
            string label = $"{i + 1} {p.FlagText()}";
            var ts = ImGui.CalcTextSize(label) * (Fonts.SmallSize / Fonts.BodySize);
            if (s1.X - s0.X > ts.X + Theme.Px(8) && s1.Y - s0.Y > ts.Y + Theme.Px(4))
            {
                var at = s0 + Theme.Px(4, 2);
                dl.AddRectFilled(at - Theme.Px(2, 1), at + ts + Theme.Px(2, 1), 0xA0000000, Theme.Px(2));
                dl.AddText(Fonts.Small, Fonts.SmallSize, at, sel ? Theme.AccentU32 : text, label);
            }
        }
        if (Selected >= 0 && Selected < asset.Patches.Count) DrawHandles(dl, Rect(asset.Patches[Selected]));
    }

    // ---- handles: 0-3 corners (min,min) (max,min) (max,max) (min,max); 4-7 edge midpoints top, right, bottom, left

    static Vector2 HandlePoint((Vector2 Min, Vector2 Max) b, int i)
    {
        var mid = (b.Min + b.Max) * 0.5f;
        return i switch
        {
            0 => b.Min, 1 => new(b.Max.X, b.Min.Y), 2 => b.Max, 3 => new(b.Min.X, b.Max.Y),
            4 => new(mid.X, b.Min.Y), 5 => new(b.Max.X, mid.Y), 6 => new(mid.X, b.Max.Y), _ => new(b.Min.X, mid.Y),
        };
    }

    int HandleAt((Vector2 Min, Vector2 Max) b, Vector2 mouse)
    {
        for (int i = 0; i < 8; i++)
            if (Vector2.Distance(ToScreen(HandlePoint(b, i)), mouse) <= Theme.Px(7)) return i;
        return -1;
    }

    void DrawHandles(ImDrawListPtr dl, (Vector2 Min, Vector2 Max) b)
    {
        int hot = _drag == Drag.Resize ? _handle : _drag == Drag.None ? HandleAt(b, ImGui.GetIO().MousePos) : -1;
        float r = Theme.Px(4);
        for (int i = 0; i < 8; i++)
        {
            var p = ToScreen(HandlePoint(b, i));
            dl.AddRectFilled(p - new Vector2(r), p + new Vector2(r), i == hot ? Theme.HoverU32 : 0xFFF0F0F0);
            dl.AddRect(p - new Vector2(r), p + new Vector2(r), 0xFF202020);
        }
    }

    /// <summary>Topmost patch under the screen point (later patches draw on top), or -1.</summary>
    int PatchAt(HotspotAsset asset, Vector2 mouse)
    {
        var n = ToTex(mouse);
        for (int i = asset.Patches.Count - 1; i >= 0; i--)
        {
            var p = asset.Patches[i];
            if (n.X >= p.X && n.X <= p.X + p.W && n.Y >= p.Y && n.Y <= p.Y + p.H) return i;
        }
        return -1;
    }

    // ---------------------------------------------------------------- input

    Vector2 Snap(HotspotAsset asset, Vector2 n, bool free)
    {
        n = Vector2.Clamp(n, Vector2.Zero, Vector2.One);
        if (free) return n;
        var step = GridStep(asset);
        return new Vector2(MathF.Round(n.X / step.X) * step.X, MathF.Round(n.Y / step.Y) * step.Y);
    }

    (Vector2 Min, Vector2 Max) DrawRect(HotspotAsset asset, Vector2 mouse, bool free)
    {
        var a = Snap(asset, _press, free);
        var b = Snap(asset, ToTex(mouse), free);
        return (Vector2.Min(a, b), Vector2.Max(a, b));
    }

    void Interact(HotspotState state, HotspotAsset asset, bool hovered)
    {
        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        if (hovered && io.MouseWheel != 0)
        {
            var before = ToTex(mouse);
            _zoom = Math.Clamp(_zoom * MathF.Pow(1.2f, io.MouseWheel), 16, 100000);
            _pan += before - ToTex(mouse);
        }
        if (_drag == Drag.None && hovered && (ImGui.IsMouseClicked(ImGuiMouseButton.Right) || ImGui.IsMouseClicked(ImGuiMouseButton.Middle)))
            _drag = Drag.Pan;
        if (_drag == Drag.Pan)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Right) || ImGui.IsMouseDown(ImGuiMouseButton.Middle)) _pan -= io.MouseDelta / (_aspect * _zoom);
            else _drag = Drag.None;
            return;
        }

        if (_drag == Drag.None && hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _press = ToTex(mouse);
            _handle = Selected >= 0 && Selected < asset.Patches.Count && !_hidePatches ? HandleAt(Rect(asset.Patches[Selected]), mouse) : -1;
            int under = _hidePatches ? -1 : PatchAt(asset, mouse);
            if (_handle >= 0) _drag = Drag.Resize;
            else if (under >= 0) { Selected = under; _drag = Drag.Move; }
            else { Selected = -1; _drag = Drag.Draw; }
            if (Selected >= 0) { var p = asset.Patches[Selected]; _start = (p.X, p.Y, p.W, p.H); }
        }
        if (_drag is Drag.None or Drag.Pan) return;

        bool free = io.KeyCtrl;
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if (_drag == Drag.Draw)
            {
                var (a, b) = DrawRect(asset, mouse, free);
                var sizePx = (ToScreen(b) - ToScreen(a));
                if (b.X - a.X > 1e-4f && b.Y - a.Y > 1e-4f && sizePx.X >= 3 && sizePx.Y >= 3)
                {
                    var p = new HotspotPatch
                    {
                        X = a.X, Y = a.Y, W = b.X - a.X, H = b.Y - a.Y, AllowRotation = _defRotate, MirrorX = _defMirrorX,
                        MirrorY = _defMirrorY, Tiling = _defTiling, Inset = _defInset,
                    };
                    p.Normalize();
                    asset.Patches.Add(p);
                    Selected = asset.Patches.Count - 1;
                    state.Dirty = true;
                }
            }
            else if (Selected >= 0 && Selected < asset.Patches.Count)
            {
                var p = asset.Patches[Selected];
                p.Normalize();
                if ((p.X, p.Y, p.W, p.H) != _start) state.Dirty = true;
            }
            _drag = Drag.None;
            _handle = -1;
            return;
        }
        if (_drag == Drag.Draw || Selected < 0 || Selected >= asset.Patches.Count) return;

        var patch = asset.Patches[Selected];
        if (_drag == Drag.Move)
        {
            var d = ToTex(mouse) - _press;
            var at = Snap(asset, new Vector2(_start.X, _start.Y) + d, free);
            patch.X = Math.Clamp(at.X, 0, 1 - _start.W);
            patch.Y = Math.Clamp(at.Y, 0, 1 - _start.H);
        }
        else
        {
            var to = Snap(asset, ToTex(mouse), free);
            float x0 = _start.X, y0 = _start.Y, x1 = _start.X + _start.W, y1 = _start.Y + _start.H;
            bool left = _handle is 0 or 3 or 7, right = _handle is 1 or 2 or 5, topEdge = _handle is 0 or 1 or 4, bottom = _handle is 2 or 3 or 6;
            if (left) x0 = to.X;
            if (right) x1 = to.X;
            if (topEdge) y0 = to.Y;
            if (bottom) y1 = to.Y;
            patch.X = MathF.Min(x0, x1);
            patch.Y = MathF.Min(y0, y1);
            patch.W = MathF.Max(MathF.Abs(x1 - x0), 1f / asset.ReferenceWidth);
            patch.H = MathF.Max(MathF.Abs(y1 - y0), 1f / asset.ReferenceHeight);
        }
    }

    /// <summary>Esc mid-drag: put the patch back where it was.</summary>
    bool CancelDrag(HotspotState state)
    {
        if (_drag == Drag.None) return false;
        if (_drag is Drag.Move or Drag.Resize && state.Editing is { } a && Selected >= 0 && Selected < a.Patches.Count)
        {
            var p = a.Patches[Selected];
            (p.X, p.Y, p.W, p.H) = _start;
        }
        _drag = Drag.None;
        _handle = -1;
        return true;
    }

    /// <summary>Window-local keys. A key handled here is hidden from the global hotkeys this frame.</summary>
    void HandleKeys(EditorContext ctx, HotspotState state)
    {
        var io = ImGui.GetIO();
        if (io.WantTextInput) return;
        bool K(ImGuiKey key, bool alt = false) => ImGui.IsKeyPressed(key, false) && !io.KeyCtrl && !io.KeyShift && io.KeyAlt == alt;
        bool handled = true;
        if (K(ImGuiKey.Escape)) { if (!CancelDrag(state)) Selected = -1; }
        else if (_drag != Drag.None) handled = false;
        else if (K(ImGuiKey.LeftBracket)) _grid = Math.Max(_grid - 1, 0);
        else if (K(ImGuiKey.RightBracket)) _grid = Math.Min(_grid + 1, GridSizes.Length - 1);
        else if (K(ImGuiKey.A, alt: true)) ToggleFlag(ctx, state, 'R');
        else if (K(ImGuiKey.D, alt: true)) ToggleFlag(ctx, state, 'X');
        else if (K(ImGuiKey.E, alt: true)) ToggleFlag(ctx, state, 'Y');
        else if (K(ImGuiKey.T, alt: true)) ToggleFlag(ctx, state, 'T');
        else if (K(ImGuiKey.V, alt: true)) _hideTexture = !_hideTexture;
        else if (K(ImGuiKey.B, alt: true)) _hidePatches = !_hidePatches;
        else if (K(ImGuiKey.F)) _frameNext = true;
        else if (K(ImGuiKey.Delete) || K(ImGuiKey.Backspace)) DeleteSelected(ctx, state);
        else handled = false;
        if (handled) io.WantTextInput = true; // the host skips global hotkeys while text input is wanted
    }

    /// <summary>Toggles a flag on the new-patch defaults and, when one is selected, on the selected patch.</summary>
    void ToggleFlag(EditorContext ctx, HotspotState state, char flag)
    {
        var p = state.Editing is { } a && Selected >= 0 && Selected < a.Patches.Count ? a.Patches[Selected] : null;
        switch (flag)
        {
            case 'R': _defRotate = p != null ? !p.AllowRotation : !_defRotate; if (p != null) p.AllowRotation = _defRotate; break;
            case 'X': _defMirrorX = p != null ? !p.MirrorX : !_defMirrorX; if (p != null) p.MirrorX = _defMirrorX; break;
            case 'Y': _defMirrorY = p != null ? !p.MirrorY : !_defMirrorY; if (p != null) p.MirrorY = _defMirrorY; break;
            case 'T':
                _defTiling = p != null ? !p.Tiling : !_defTiling;
                if (p != null)
                {
                    p.Tiling = _defTiling && p.CanTile;
                    if (_defTiling && !p.CanTile) ctx.Status("Tiling needs a patch that spans the full width or height of the texture");
                }
                break;
        }
        if (p != null) state.Dirty = true;
    }

    void DeleteSelected(EditorContext ctx, HotspotState state)
    {
        if (state.Editing is not { } a || Selected < 0 || Selected >= a.Patches.Count) return;
        a.Patches.RemoveAt(Selected);
        Selected = -1;
        state.Dirty = true;
        ctx.Status("Deleted the patch. Revert brings it back until you save");
    }

    // ---------------------------------------------------------------- side column

    void DrawSide(EditorContext ctx, HotspotState state)
    {
        if (state.Editing is not { } asset)
        {
            ImGui.TextColored(Theme.TextDim, "Make an asset with New.");
            return;
        }

        Heading("Patches", asset.Patches.Count.ToString());
        float listH = MathF.Min(Theme.Px(170), MathF.Max(ImGui.GetTextLineHeightWithSpacing() * 3, ImGui.GetContentRegionAvail().Y * 0.35f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Bg);
        if (ImGui.BeginChild("##hslist", new Vector2(0, listH), ImGuiChildFlags.None))
        {
            for (int i = 0; i < asset.Patches.Count; i++)
            {
                var p = asset.Patches[i];
                string size = $"{p.W * asset.ReferenceWidth:0}×{p.H * asset.ReferenceHeight:0}";
                if (ImGui.Selectable($"{i + 1,2}  {size,-9} {p.FlagText()}##hsp{i}", i == Selected)) Selected = i;
            }
            if (asset.Patches.Count == 0) ImGui.TextColored(Theme.TextDim, "Drag on the texture to draw one");
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();

        if (Selected >= 0 && Selected < asset.Patches.Count) DrawPatchProperties(ctx, state, asset, asset.Patches[Selected]);

        ImGui.Dummy(Theme.Px(0, 4));
        Heading("Asset");
        RowLabel("Name");
        string name = asset.Name;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##hsname", ref name, 64) && name.Trim().Length > 0) { asset.Name = name; state.Dirty = true; }
        RowLabel("Targets", "Materials painted with this pattern. Apply uses the active one when it is listed");
        ImGui.BeginGroup();
        string? remove = null;
        foreach (var m in asset.Materials)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.Text(m);
            ImGui.SameLine(ImGui.GetContentRegionMax().X - ImGui.GetFrameHeight());
            if (ImGui.Button($"×##hsrm{m}", new Vector2(ImGui.GetFrameHeight(), 0))) remove = m;
            Tip($"Remove {m} from this asset");
        }
        if (asset.Materials.Count == 0) { ImGui.AlignTextToFramePadding(); ImGui.TextColored(Theme.TextDim, "None"); }
        ImGui.EndGroup();
        if (remove != null) { asset.Materials.Remove(remove); state.Dirty = true; }
        ImGui.BeginDisabled(asset.Targets(ctx.ActiveMaterial) || ctx.ActiveMaterial == MaterialLibrary.Missing.Name);
        if (ImGui.Button("Add active material", new Vector2(-1, 0))) { asset.Materials.Add(ctx.ActiveMaterial); state.Dirty = true; }
        ImGui.EndDisabled();
        Tip($"Add {ctx.ActiveMaterial} to the materials this pattern is painted on");
        RowLabel("Preview", "Material shown here; defaults to the first material");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##hspreview", asset.PreviewMaterial ?? "First material"))
        {
            if (ImGui.Selectable("First material", asset.PreviewMaterial == null)) { asset.PreviewMaterial = null; state.Dirty = true; }
            foreach (var m in asset.Materials.Append(ctx.ActiveMaterial).Distinct(StringComparer.OrdinalIgnoreCase))
                if (ImGui.Selectable(m, string.Equals(m, asset.PreviewMaterial, StringComparison.OrdinalIgnoreCase))) { asset.PreviewMaterial = m; state.Dirty = true; }
            ImGui.EndCombo();
        }
    }

    void DrawPatchProperties(EditorContext ctx, HotspotState state, HotspotAsset asset, HotspotPatch p)
    {
        ImGui.Dummy(Theme.Px(0, 2));
        Heading($"Patch {Selected + 1}", p.TileAxis != TileAxis.None ? "tiles along " + (p.TileAxis == TileAxis.U ? "U" : "V") : null);
        float rw = asset.ReferenceWidth, rh = asset.ReferenceHeight;
        if (TaggedFields("##hsrect", ["X", "Y", "W", "H"], RectTags, [p.X * rw, p.Y * rh, p.W * rw, p.H * rh], [false, false, false, false], "%.0f", out int idx, out float val))
        {
            switch (idx)
            {
                case 0: p.X = val / rw; break;
                case 1: p.Y = val / rh; break;
                case 2: p.W = val / rw; break;
                default: p.H = val / rh; break;
            }
            p.Normalize(1f / MathF.Max(rw, rh));
            state.Dirty = true;
        }
        if (Toggle("hspr", Icon.Rotate, "R", "Allow rotation  (Alt+A)", p.AllowRotation)) { p.AllowRotation = !p.AllowRotation; state.Dirty = true; }
        ImGui.SameLine(0, Theme.Px(2));
        if (Toggle("hspx", Icon.FlipU, "X", "Mirror X  (Alt+D)", p.MirrorX)) { p.MirrorX = !p.MirrorX; state.Dirty = true; }
        ImGui.SameLine(0, Theme.Px(2));
        if (Toggle("hspy", Icon.FlipV, "Y", "Mirror Y  (Alt+E)", p.MirrorY)) { p.MirrorY = !p.MirrorY; state.Dirty = true; }
        ImGui.SameLine(0, Theme.Px(2));
        ImGui.BeginDisabled(!p.CanTile && !p.Tiling);
        if (Toggle("hspt", Icon.Repeat, "T", p.CanTile ? "Tiling: repeats along its length  (Alt+T)" : "Tiling needs a patch spanning the full width or height", p.Tiling))
        {
            p.Tiling = !p.Tiling && p.CanTile;
            state.Dirty = true;
        }
        ImGui.EndDisabled();
        RowLabel("Inset", "Pixels kept clear inside the rect, at the reference resolution");
        float inset = p.Inset;
        ImGui.SetNextItemWidth(Theme.Px(64));
        if (ImGui.InputFloat("##hsinset", ref inset, 0, 0, "%.0f px", ImGuiInputTextFlags.EnterReturnsTrue)) { p.Inset = Math.Clamp(inset, 0, 256); state.Dirty = true; }
        ImGui.SameLine(0, Theme.Px(6));
        if (ImGui.Button("Delete")) DeleteSelected(ctx, state);
        Tip("Delete this patch  (Del)");

        bool hasFaces = ctx.Sel.FaceSets(ctx.Doc).Count > 0;
        float gap = Theme.Px(4);
        float w = (ImGui.GetContentRegionAvail().X - gap) / 2;
        ImGui.BeginDisabled(!hasFaces);
        if (ImGui.Button("Apply to faces", new Vector2(w, 0))) state.Apply(individual: false, asset, Selected);
        Tip("Fit each coplanar region of the selection into this patch (settings from the inspector's Hotspot section)");
        ImGui.SameLine(0, gap);
        if (ImGui.Button("Each face", new Vector2(w, 0))) state.Apply(individual: true, asset, Selected);
        Tip("Fit every selected face into this patch");
        ImGui.EndDisabled();
    }

    // ---------------------------------------------------------------- asset files

    void NewAsset(EditorContext ctx, HotspotState state)
    {
        var material = ctx.Materials.Get(ctx.ActiveMaterial);
        if (state.AssetFor(material.Name) is { } existing)
        {
            if (!state.Dirty) Edit(state, existing);
            ctx.Status($"{material.Name} already has a hotspot asset: {existing.Name}");
            return;
        }
        if (material.Path == null)
        {
            ctx.Status($"{material.Name} is built in and has no folder for an asset. Pick a texture from a library folder first", error: true);
            return;
        }
        var asset = new HotspotAsset
        {
            Name = material.Name, ReferenceWidth = material.Width, ReferenceHeight = material.Height, Materials = [material.Name],
            Path = Path.Combine(Path.GetDirectoryName(material.Path)!, material.Name + HotspotAsset.Extension),
        };
        state.Editing = asset;
        state.Dirty = true;
        Selected = -1;
        _frameNext = true;
        ctx.Status($"New hotspot asset for {material.Name}. Drag on the texture to draw patches, then Save");
    }

    void Save(EditorContext ctx, HotspotState state)
    {
        if (state.Editing is not { Path: { } path } asset) return;
        try
        {
            asset.Save(path);
            state.Dirty = false;
            state.Rescan();
            ctx.Status($"Saved {Path.GetFileName(path)}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ctx.Status($"Could not save {path}: {e.Message}. Check the folder is writable", error: true);
        }
    }

    void Revert(EditorContext ctx, HotspotState state)
    {
        if (state.Editing is not { } asset) return;
        var saved = asset.Path != null ? state.Library.Assets.FirstOrDefault(a => string.Equals(a.Path, asset.Path, StringComparison.OrdinalIgnoreCase)) : null;
        state.Dirty = false;
        if (saved != null) Edit(state, saved);
        else
        {
            state.Editing = state.Library.Assets.FirstOrDefault()?.Clone();
            Selected = -1;
        }
        ctx.Status(saved != null ? $"Reverted {saved.Name}" : "Discarded the unsaved asset");
    }

    void FrameTexture()
    {
        _pan = new Vector2(0.5f);
        var size = _c1 - _c0;
        if (size.X <= 0 || size.Y <= 0) { _zoom = 256; return; }
        _zoom = MathF.Max(MathF.Min(size.X / _aspect.X, size.Y / _aspect.Y) * 0.94f, 16);
    }
}
