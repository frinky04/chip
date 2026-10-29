using Chip.Editor.UI;
using ImGuiNET;
using Silk.NET.OpenGL;

namespace Chip.Editor;

/// <summary>
/// Live UI scale. The scale is CHIP_UI_SCALE, else the Preferences override, else the DPI of the monitor the window is
/// on. When it changes (window dragged to another monitor, or Preferences), the font atlas is rebuilt at the new pixel
/// size and the style is re-scaled, between frames. Silk's ImGuiController keeps its font-texture upload private, so
/// the new atlas is uploaded here and handed to ImGui with <c>SetTexID</c>; the controller's renderer just draws
/// whatever texture id the draw commands carry.
/// </summary>
public sealed partial class EditorApp
{
    float _uiScale;
    uint _fontTexture; // ours, once the atlas has been rebuilt; the controller owns the first one
    int _scaleCheckFrame;
    bool _scaleCheckNow;

    float TargetUiScale() => Dpi.Override ?? _settings.Current.UiScale ?? Dpi.ScaleFor(_window);

    /// <summary>Polls a few times a second (GetDpiForWindow is cheap), or at once after Preferences changed.</summary>
    void UpdateUiScale()
    {
        if (_snapshotRescaleTo is { } forced) { _snapshotRescaleTo = null; Rescale(forced); return; }
        if (Icons.Pending) RebuildFontAtlas(); // a manifest named icons the atlas lacks (captures need them too)
        if (!_persistent) return;
        if (!_scaleCheckNow && ++_scaleCheckFrame % 20 != 0) return;
        _scaleCheckNow = false;
        float target = TargetUiScale();
        if (MathF.Abs(target - _uiScale) > 0.01f) Rescale(target);
    }

    void Rescale(float scale)
    {
        _uiScale = scale;
        RebuildFontAtlas();
        Theme.Apply(scale);
        Ctx.Status($"Interface scale is now {scale:0.##}×"); // no '%': the status bar draws text as a printf format
    }

    /// <summary>Builds the font atlas at the current scale, with every icon asked for so far, and uploads it.</summary>
    void RebuildFontAtlas()
    {
        var io = ImGui.GetIO();
        io.Fonts.Clear();
        Fonts.Load(FontDir, _uiScale);
        io.Fonts.GetTexDataAsRGBA32(out nint pixels, out int w, out int h, out _);

        _gl.GetInteger(GetPName.TextureBinding2D, out int previous);
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        unsafe { _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)pixels); }
        _gl.BindTexture(TextureTarget.Texture2D, (uint)previous);
        io.Fonts.SetTexID((nint)tex);
        io.Fonts.ClearTexData();
        DeleteFontTexture();
        _fontTexture = tex;
    }

    void DeleteFontTexture()
    {
        if (_fontTexture != 0) _gl.DeleteTexture(_fontTexture);
        _fontTexture = 0;
    }
}
