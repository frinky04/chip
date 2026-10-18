using System.Numerics;
using ImGuiNET;

namespace Chip.Editor.UI;

public enum Icon { Select, Block, Vertex, Edge, Face, Object, Move, Rotate, Scale, Snap, Backfaces, FlipU, FlipV,
    Texture, Repeat, Grid, Coordinates, Photo, Ruler, KeepRatio, TreatAsOne, Lock,
    JustifyLeft, JustifyRight, JustifyTop, JustifyBottom, JustifyCenter, FitBoth, FitWidth, FitHeight }

/// <summary>Tool glyphs from the Tabler icon font (MIT), drawn on whole pixels so they stay crisp at small sizes.</summary>
public static class Icons
{
    static readonly Dictionary<Icon, char> Glyphs = new()
    {
        [Icon.Select] = '',    // pointer
        [Icon.Block] = '殺',     // cube-plus
        [Icon.Vertex] = '',    // vector
        [Icon.Edge] = '',      // line
        [Icon.Face] = '',      // square-half
        [Icon.Object] = '流',    // cube
        [Icon.Move] = '',      // arrows-move
        [Icon.Rotate] = '',    // rotate-clockwise
        [Icon.Scale] = '',     // resize
        [Icon.Snap] = '',      // magnet
        [Icon.Backfaces] = '', // layers-difference
        [Icon.FlipU] = '',     // flip-horizontal
        [Icon.FlipV] = '',     // flip-vertical
        [Icon.Texture] = '',       // texture
        [Icon.Repeat] = '',        // repeat
        [Icon.Grid] = 'ﲥ',          // grid-4x4
        [Icon.Coordinates] = '',   // number
        [Icon.Photo] = '',         // photo
        [Icon.Ruler] = '',         // ruler
        [Icon.KeepRatio] = '',     // aspect-ratio
        [Icon.TreatAsOne] = '',    // layers-union
        [Icon.Lock] = '',          // lock
        [Icon.JustifyLeft] = '',   // layout-align-left
        [Icon.JustifyRight] = '',  // layout-align-right
        [Icon.JustifyTop] = '',    // layout-align-top
        [Icon.JustifyBottom] = '', // layout-align-bottom
        [Icon.JustifyCenter] = '', // focus-centered
        [Icon.FitBoth] = '',       // arrows-maximize
        [Icon.FitWidth] = '',      // arrows-horizontal
        [Icon.FitHeight] = '',     // arrows-vertical
    };

    /// <summary>Codepoints to bake into the icon atlas.</summary>
    public static IEnumerable<ushort> Codepoints => Glyphs.Values.Select(c => (ushort)c);

    public static string Text(Icon icon) => Glyphs[icon].ToString();

    public static void Draw(ImDrawListPtr dl, Icon icon, Vector2 center, uint col)
    {
        var text = Text(icon);
        ImGui.PushFont(Fonts.Icons);
        var size = ImGui.CalcTextSize(text);
        ImGui.PopFont();
        var pos = center - size * 0.5f;
        dl.AddText(Fonts.Icons, Fonts.IconSize, new Vector2(MathF.Round(pos.X), MathF.Round(pos.Y)), col, text);
    }
}
