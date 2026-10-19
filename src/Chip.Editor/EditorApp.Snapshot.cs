using System.Numerics;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.Settings;
using Chip.Editor.UI;
using ImGuiNET;
using Silk.NET.OpenGL;
using StbImageWriteSharp;

namespace Chip.Editor;

/// <summary>
/// <c>Chip --shot [level.chip|-] out.png [state] [--run cmd.id,cmd.id]</c>: renders the whole editor UI from an invisible
/// window into an offscreen buffer, after an optional preset <c>state</c> (empty|objects|faces|block|blockpreview|polygonpreview|
/// backfaces|bevelpending|offsetpending|shortcuts|shortcuts-search|palette|palette-search|preferences|prefs-viewport|prefs-hotkeys|prefs-paths|recovery|rescale|hotspot-editor) and a
/// comma-separated list of command ids. Every feature can prove its UI headlessly this way.
/// </summary>
public sealed partial class EditorApp
{
    uint _captureFbo;
    string? _snapshotPaletteQuery;
    Action? _snapshotAfterPrefsOpen;
    Vector2? _shotDragMouse;
    bool _shotButtonHeld;

    static (Vector2 From, Vector2 To, Framework.Hotkey? Mods)? ParseShotDrag(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var p = v.Split(',', StringSplitOptions.TrimEntries);
        if (p.Length < 4) return null;
        float F(int i) => float.Parse(p[i], System.Globalization.CultureInfo.InvariantCulture);
        // Modifiers parse as a hotkey on a dummy key ("Ctrl+Alt" -> "Ctrl+Alt+F12").
        var mods = p.Length > 4 && p[4].Length > 0 ? Framework.Hotkey.Parse(p[4] + "+F12") : (Framework.Hotkey?)null;
        return (new Vector2(F(0), F(1)), new Vector2(F(2), F(3)), mods);
    }

    void RunShotDrag((Vector2 From, Vector2 To, Framework.Hotkey? Mods) d)
    {
        var io = ImGui.GetIO();
        void Mods(bool down)
        {
            if (d.Mods is not { } m) return;
            if (m.Ctrl) io.AddKeyEvent(ImGuiKey.ModCtrl, down);
            if (m.Shift) io.AddKeyEvent(ImGuiKey.ModShift, down);
            if (m.Alt) io.AddKeyEvent(ImGuiKey.ModAlt, down);
        }
        _shotDragMouse = d.From;
        Mods(true);
        Frame(1 / 60f);
        io.AddMouseButtonEvent(0, true);
        Frame(1 / 60f);
        _shotButtonHeld = true;
        const int steps = 16;
        for (int s = 1; s <= steps; s++)
        {
            _shotDragMouse = Vector2.Lerp(d.From, d.To, s / (float)steps);
            Frame(1 / 60f);
        }
        _shotButtonHeld = false;
        io.AddMouseButtonEvent(0, false);
        Frame(1 / 60f);
        Mods(false);
        Frame(1 / 60f);
    }

    /// <summary>"rescale" state: rebuild fonts and style at this scale before the first frame (the DPI-change path).</summary>
    float? _snapshotRescaleTo;

    /// <summary>Returns a process exit code: 0 ok, 2 unknown command id.</summary>
    public int RunSnapshot(string? chipPath, string pngPath, string state, IReadOnlyList<string> commands)
    {
        var ctx = Ctx;
        if (chipPath != null)
        {
            ctx.Session.Reset(DocumentSerializer.Load(chipPath), chipPath);
            ctx.Camera.Position = new System.Numerics.Vector3(-3, 8, 14);
            ctx.Camera.LookAt(new System.Numerics.Vector3(10, 0, 4));
            ctx.FrameAll();
        }
        var doc = ctx.Doc;
        switch (state)
        {
            case "objects" when doc.Meshes.Count > 2:
                ctx.SetMode(SelectMode.Object);
                ctx.Sel.Add(ElementRef.Object(doc.Meshes[2].Id));
                break;
            case "faces" when doc.Meshes.Count > 0:
                ctx.SetMode(SelectMode.Face);
                var floor = doc.Meshes[0];
                for (int f = 0; f < floor.Faces.Count; f++)
                    if (floor.FaceNormal(f).Y > 0.9f) ctx.Sel.Add(ElementRef.Face(floor.Id, f));
                break;
            case "shortcuts":
                ctx.ShowShortcuts = true;
                break;
            case "shortcuts-search": // the sheet filtered by a search
                ctx.ShowShortcuts = true;
                _sheetSearch = "texture";
                break;
            case "backfaces":
                ctx.Backfaces = true;
                break;
            case "block":
                ctx.SetTool("block");
                break;
            case "blockpreview": // mid-drag shape preview; pick the shape with --run block.shapeArch etc.
                ctx.SetTool("block");
                (ctx.ActiveTool as Tools.BlockTool)?.PreviewForCapture();
                break;
            case "bevelpending" when doc.Meshes.Count > 2: // a wall's top edges, 3-segment bevel waiting for Enter
            {
                ctx.SetMode(SelectMode.Edge);
                var wall = doc.Meshes[2];
                float top = wall.Positions.Max(p => p.Y);
                foreach (var e in wall.Edges())
                    if (wall.Positions[e.A].Y > top - 1e-4f && wall.Positions[e.B].Y > top - 1e-4f) ctx.Sel.Add(ElementRef.EdgeOf(wall.Id, e));
                ctx.SetTool("bevel");
                if (ctx.ActiveTool is Tools.BevelTool bevel) { bevel.Width = 0.125f; bevel.Segments = 3; bevel.Refresh(ctx); }
                break;
            }
            case "offsetpending" when doc.Meshes.Count > 2: // a wall's upright faces pushed out 25 cm, waiting for Enter
            {
                ctx.SetMode(SelectMode.Face);
                var wall = doc.Meshes[2];
                for (int f = 0; f < wall.Faces.Count; f++)
                    if (MathF.Abs(wall.FaceNormal(f).Y) < 0.1f) ctx.Sel.Add(ElementRef.Face(wall.Id, f));
                ctx.Commands.Execute("mesh.offset");
                (ctx.ActiveTool as Features.MeshEditing.OffsetTool)?.SetAmount(ctx, 0.25f);
                break;
            }
            case "polygonpreview":
                ctx.SetTool("polygon");
                (ctx.ActiveTool as Tools.PolygonTool)?.PreviewForCapture();
                break;
            case "palette":
                _settings.Current.RecentCommands.AddRange(["view.focus", "edit.undo"]);
                _shell.PaletteRequested = true;
                break;
            case "palette-search":
                _shell.PaletteRequested = true;
                _snapshotPaletteQuery = "grid";
                break;
            case "preferences" or "prefs-general":
                _shell.PreferencesRequested = true;
                break;
            case "prefs-viewport":
                _prefSection = 1;
                _shell.PreferencesRequested = true;
                break;
            case "prefs-hotkeys":
                _prefSection = 2;
                ctx.Commands.Rebind("view.focus", [Hotkey.Parse("Shift+F2")]);
                _hotkeySearch = "view";
                _snapshotAfterPrefsOpen = () =>
                {
                    _captureId = "view.frameAll";
                    _hotkeyMessage = "Focus selection is now Shift+F2";
                };
                _shell.PreferencesRequested = true;
                break;
            case "prefs-paths":
                _prefSection = 3;
                _settings.Current.TextureFolders.AddRange([@"D:\Art\textures\dev", @"C:\missing\folder"]);
                _shell.PreferencesRequested = true;
                break;
            case "hotspot-editor": // editor open on the active material's asset with a patch selected (properties visible)
                Features.Hotspots.HotspotEditor.For(ctx).Show(ctx, null);
                Features.Hotspots.HotspotEditor.For(ctx).Selected = 1;
                break;
            case "rescale":
                _snapshotRescaleTo = Theme.Scale >= 1.5f ? 1f : 2f;
                break;
            case "recovery":
                _recovery = [new AutosaveInfo("level-0000", chipPath == null ? null : Path.GetFullPath(chipPath), DateTime.UtcNow.AddMinutes(-3), 0, DateTime.UtcNow)];
                break;
        }
        foreach (var id in commands)
        {
            if (ctx.Commands.Find(id) == null) { Console.Error.WriteLine($"Unknown command '{id}'"); return 2; }
            ctx.Commands.Execute(id);
        }

        var fb = _window.FramebufferSize;
        uint w = (uint)fb.X, h = (uint)fb.Y;
        _captureFbo = _gl.GenFramebuffer();
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        unsafe { _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, w, h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null); }
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _captureFbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);

        // CHIP_SHOT_KEYS=F1 (or "Ctrl+P", comma-separated) presses those hotkeys inside ImGui on frame 1, so the real
        // keyboard path (hotkey matching, popups) is proven headlessly. Never touches OS input.
        var keys = (Environment.GetEnvironmentVariable("CHIP_SHOT_KEYS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Framework.Hotkey.Parse).ToList();
        // CHIP_SHOT_DRAG="x0,y0,x1,y1[,Ctrl+Alt]" (viewport fractions) presses the left button at the start, sweeps to the
        // end over 16 frames holding the modifiers, and releases: proves drag gestures (paint select, box select).
        var drag = ParseShotDrag(Environment.GetEnvironmentVariable("CHIP_SHOT_DRAG"));
        for (int i = 0; i < 6; i++) // ImGui settles layout over a couple of frames
        {
            var io = ImGui.GetIO();
            if (i == 3 && drag is { } d) RunShotDrag(d);
            foreach (var k in keys)
            {
                if (i is not (1 or 2)) continue;
                bool down = i == 1;
                if (k.Ctrl) io.AddKeyEvent(ImGuiKey.ModCtrl, down);
                if (k.Shift) io.AddKeyEvent(ImGuiKey.ModShift, down);
                if (k.Alt) io.AddKeyEvent(ImGuiKey.ModAlt, down);
                io.AddKeyEvent(k.Key, down);
            }
            Frame(1 / 60f);
        }

        var pixels = new byte[w * h * 4];
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _captureFbo);
        _gl.ReadPixels<byte>(0, 0, w, h, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        int stride = (int)w * 4;
        var flipped = new byte[pixels.Length];
        for (int y = 0; y < h; y++) Array.Copy(pixels, y * stride, flipped, ((int)h - 1 - y) * stride, stride);
        for (int i = 3; i < flipped.Length; i += 4) flipped[i] = 255;
        using var file = File.Create(pngPath);
        new ImageWriter().WritePng(flipped, (int)w, (int)h, ColorComponents.RedGreenBlueAlpha, file);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.DeleteFramebuffer(_captureFbo);
        _gl.DeleteTexture(tex);
        _captureFbo = 0;
        return 0;
    }
}
