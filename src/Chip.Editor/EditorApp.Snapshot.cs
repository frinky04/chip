using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.Settings;
using Chip.Editor.UI;
using Silk.NET.OpenGL;
using StbImageWriteSharp;

namespace Chip.Editor;

/// <summary>
/// <c>Chip --shot [level.chip|-] out.png [state] [--run cmd.id,cmd.id]</c>: renders the whole editor UI from an invisible
/// window into an offscreen buffer, after an optional preset <c>state</c> (empty|objects|faces|block|blockpreview|polygonpreview|
/// backfaces|bevelpending|shortcuts|palette|palette-search|preferences|prefs-viewport|prefs-hotkeys|prefs-paths|recovery|rescale|hotspot-editor) and a
/// comma-separated list of command ids. Every feature can prove its UI headlessly this way.
/// </summary>
public sealed partial class EditorApp
{
    uint _captureFbo;
    string? _snapshotPaletteQuery;
    Action? _snapshotAfterPrefsOpen;
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

        for (int i = 0; i < 4; i++) Frame(1 / 60f); // ImGui settles layout over a couple of frames

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
