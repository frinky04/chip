using System.Numerics;
using ImGuiNET;

namespace Chip.Editor.UI;

public enum Icon { Select, Block, Vertex, Edge, Face, Object, Move, Rotate, Scale, Snap, Backfaces, FlipU, FlipV,
    Clip, Knife, Bevel, KeepFront, KeepBack, KeepBoth, Eye }

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
        [Icon.Clip] = '',      // cut
        [Icon.Knife] = '',     // slice
        [Icon.Bevel] = '',     // border-radius
        [Icon.KeepFront] = '', // box-align-top
        [Icon.KeepBack] = '',  // box-align-bottom
        [Icon.KeepBoth] = '',  // layout-rows
        [Icon.Eye] = '',       // eye
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
