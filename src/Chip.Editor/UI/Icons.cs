using System.Numerics;
using ImGuiNET;

namespace Chip.Editor.UI;

public enum Icon { Select, Block, Vertex, Edge, Face, Object, Move, Rotate, Scale, Snap, Backfaces, FlipU, FlipV,
    Rectangle, Polygon, Clip, Knife, Bevel, KeepFront, KeepBack, KeepBoth, Eye,
    Texture, Repeat, Grid, Coordinates, Photo, Ruler, KeepRatio, TreatAsOne, Lock,
    JustifyLeft, JustifyRight, JustifyTop, JustifyBottom, JustifyCenter, FitBoth, FitWidth, FitHeight,
    // stream H: outliner and entities
    Folder, EyeOff, LockOpen, MapPin, Bulb, Lamp, User, Target, Flag, Box, Plus, Trash, ListTree, ChevronRight, ChevronDown,
    // the side dock and menu bar
    Palette, Brush, Search, Dots, SidebarCollapse, SidebarExpand, FileExport,
    // linked copies
    Link, Unlink, Package }

/// <summary>
/// Tool glyphs from the Tabler icon font (MIT), drawn on whole pixels so they stay crisp at small sizes. Besides the
/// named <see cref="Icon"/>s, a manifest's entity class can ask for any Tabler icon by name (<see cref="Named"/>): it
/// comes back as an <see cref="Icon"/> past the named ones, <see cref="NamedBase"/> plus its codepoint.
/// </summary>
public static class Icons
{
    const int NamedBase = 0x10000;

    // Icons a manifest named, and those the atlas holds. A new one waits for the atlas to rebuild between frames.
    static readonly HashSet<ushort> Wanted = [];
    static readonly HashSet<ushort> Baked = [];
    static readonly Dictionary<Icon, string> TextCache = [];

    /// <summary>A manifest named an icon the atlas lacks: the editor rebuilds the font atlas before the next frame.</summary>
    public static bool Pending { get; private set; }
    /// <summary>Changes when the atlas gains icons, so styles cached while they were missing pick them up.</summary>
    public static int Revision { get; private set; }

    /// <summary>
    /// The Tabler icon called <paramref name="name"/> (<c>door</c>), or null when there's none Chip can draw. Also null
    /// until the atlas holds it: the first ask queues it for the rebuild.
    /// </summary>
    public static Icon? Named(string name)
    {
        if (Chip.Core.Scene.EntityIcons.Codepoint(name) is not { } c || c > Chip.Core.Scene.EntityIcons.MaxDrawable) return null;
        if (Baked.Contains((ushort)c)) return (Icon)(NamedBase + c);
        if (Wanted.Add((ushort)c)) Pending = true;
        return null;
    }

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
        [Icon.Rectangle] = '', // rectangle
        [Icon.Polygon] = '',   // polygon
        [Icon.Clip] = '',      // cut
        [Icon.Knife] = '',     // slice
        [Icon.Bevel] = '',     // border-radius
        [Icon.KeepFront] = '', // box-align-top
        [Icon.KeepBack] = '',  // box-align-bottom
        [Icon.KeepBoth] = '',  // layout-rows
        [Icon.Eye] = '',       // eye
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
        // stream H: outliner and entities
        [Icon.Folder] = '',        // folder
        [Icon.EyeOff] = '',        // eye-off
        [Icon.LockOpen] = '',      // lock-open
        [Icon.MapPin] = '',        // map-pin
        [Icon.Bulb] = '',          // bulb
        [Icon.Lamp] = '',          // lamp
        [Icon.User] = '',          // user
        [Icon.Target] = '',        // target
        [Icon.Flag] = '',          // flag
        [Icon.Box] = '',           // box
        [Icon.Plus] = '',          // plus
        [Icon.Trash] = '',         // trash
        [Icon.ListTree] = '﫺',      // list-tree
        [Icon.ChevronRight] = '',  // chevron-right
        [Icon.ChevronDown] = '',   // chevron-down
        [Icon.Palette] = '',         // palette
        [Icon.Brush] = '',           // brush
        [Icon.Search] = '',          // search
        [Icon.Dots] = '',            // dots
        [Icon.SidebarCollapse] = '', // layout-sidebar-left-collapse
        [Icon.SidebarExpand] = '',   // layout-sidebar-left-expand
        [Icon.FileExport] = '',      // file-export
        [Icon.Link] = '',      // link
        [Icon.Unlink] = '',    // unlink
        [Icon.Package] = '',   // package (prefabs)
    };

    /// <summary>Codepoints to bake into the icon atlas, counted as baked from here on (call once per atlas build).</summary>
    public static IEnumerable<ushort> Bake()
    {
        Baked.UnionWith(Glyphs.Values.Select(c => (ushort)c));
        if (!Wanted.IsSubsetOf(Baked)) { Baked.UnionWith(Wanted); Revision++; }
        Pending = false;
        return Baked;
    }

    /// <summary>The glyph as text, made once per icon: entity glyphs repeat hundreds of times per frame.</summary>
    public static string Text(Icon icon)
    {
        if (TextCache.TryGetValue(icon, out var text)) return text;
        char c = (int)icon >= NamedBase ? (char)((int)icon - NamedBase) : Glyphs[icon];
        return TextCache[icon] = c.ToString();
    }

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
