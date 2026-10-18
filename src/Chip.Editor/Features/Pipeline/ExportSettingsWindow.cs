using System.Numerics;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using NativeFileDialogSharp;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Pipeline;

/// <summary>
/// File > Export settings: every per-document export option in one floating window. Each change is one undo step and
/// is saved with the document.
/// </summary>
public sealed class ExportSettingsWindow
{
    public bool Open;
    bool _wasOpen;
    string _pathBuffer = "";
    string _pathFor = "\0";
    readonly Dictionary<string, string> _remapBuffers = new(StringComparer.OrdinalIgnoreCase);
    string _newMaterial = "";

    public void Draw(EditorContext ctx)
    {
        if (!Open) { _wasOpen = false; return; }
        var vp = ctx.ViewportSize;
        ImGui.SetNextWindowPos(ctx.ViewportOrigin + new Vector2(MathF.Max(Theme.Px(10), (vp.X - Theme.Px(460)) * 0.5f), Theme.Px(56)), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(Theme.Px(460, 560), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(Theme.Px(380, 300), new Vector2(float.MaxValue));
        if (!_wasOpen) ImGui.SetNextWindowFocus();
        _wasOpen = true;
        bool open = true;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(14, 10));
        bool visible = ImGui.Begin("Export settings###exportSettings", ref open, ImGuiWindowFlags.NoCollapse);
        ImGui.PopStyleVar();
        if (visible) DrawContents(ctx);
        ImGui.End();
        if (!open) Open = false;
    }

    static float Column => Theme.Px(112);

    static void Label(string text, string? tip = null)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, text);
        if (tip != null) Tip(tip);
        ImGui.SameLine(ImGui.GetStyle().WindowPadding.X + Column);
    }

    void DrawContents(EditorContext ctx)
    {
        var doc = ctx.Doc;
        var opt = doc.Export;

        Heading("Output");
        if (_pathFor != (doc.ExportPath ?? "")) { _pathFor = doc.ExportPath ?? ""; _pathBuffer = _pathFor; }
        Label("File", "Where the .glb goes. A relative path starts at the .chip file's folder.");
        float browseW = Theme.Px(84);
        ImGui.SetNextItemWidth(-browseW - ImGui.GetStyle().ItemSpacing.X);
        ImGui.InputTextWithHint("##path", "Not set: Export GLB asks", ref _pathBuffer, 1024);
        if (ImGui.IsItemDeactivatedAfterEdit()) SetPath(ctx, _pathBuffer);
        ImGui.SameLine();
        if (ImGui.Button("Browse…", new Vector2(browseW, 0)))
        {
            var r = Dialog.FileSave("glb");
            if (r.IsOk) SetPath(ctx, r.Path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) ? r.Path : r.Path + ".glb");
        }
        Label("Watch");
        bool watch = opt.ExportOnSave;
        if (ImGui.Checkbox("Export on save", ref watch)) PipelineActions.ToggleExportOnSave(ctx);
        Tip("Every save also writes the .glb, so Godot re-imports it  (Ctrl+Alt+E)");

        Gap();
        Heading("Geometry");
        Label("Scale", "Multiplier on export. 1 for Godot (metres), 100 for centimetre engines");
        float scale = doc.ExportScale;
        ImGui.SetNextItemWidth(Theme.Px(120));
        if (ImGui.InputFloat("##scale", ref scale, 0, 0, "%.3g ×", ImGuiInputTextFlags.EnterReturnsTrue) && scale > 0)
            ctx.Session.Do("Export Scale", () => ctx.Doc.ExportScale = scale);
        Label("Collision");
        bool col = doc.ExportCollision;
        if (ImGui.Checkbox("Static collision by default", ref col)) ctx.Session.Do("Export Collision", () => ctx.Doc.ExportCollision = col);
        Tip("Meshes set to Default get -col: Godot builds a trimesh collider. Set per mesh in the inspector's Collision section.");
        Label("Normals");
        Check(ctx, "Smooth across soft edges", "Off exports every face flat", opt.Smoothing, "Export Smoothing", (d, v) => d.Export.Smoothing = v);
        Label("Hidden");
        Check(ctx, "Include hidden meshes", "Hidden meshes and entities still go into the file", opt.IncludeHidden, "Export Hidden", (d, v) => d.Export.IncludeHidden = v);

        Gap();
        Heading("Textures");
        Label("Images");
        Check(ctx, "Embed in the .glb", "Off: the .glb references the image files (relative paths), copying them beside it when needed",
            opt.EmbedTextures, "Embed Textures", (d, v) => d.Export.EmbedTextures = v);

        Gap();
        DrawRemap(ctx);

        Gap();
        if (ImGui.Button("Export now", new Vector2(Theme.Px(120), 0))) ctx.Commands.Execute("file.export");
        Tip(PipelineActions.ResolveExportPath(ctx) is { } p ? $"Write {p}  (Ctrl+E)" : "Choose where to write the .glb  (Ctrl+E)");
        ImGui.SameLine();
        if (ImGui.Button("Close", new Vector2(Theme.Px(96), 0))) Open = false;
    }

    void DrawRemap(EditorContext ctx)
    {
        var remap = ctx.Doc.Export.MaterialRemap;
        Heading("Material remap", remap.Count > 0 ? (remap.Count == 1 ? "1 material" : $"{remap.Count} materials") : null);
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(Theme.TextDim, "Swap Chip materials for Godot resources on import. Needs the chip_import addon.");
        ImGui.PopTextWrapPos();

        string? remove = null;
        if (remap.Count > 0 && ImGui.BeginTable("##remap", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Material", ImGuiTableColumnFlags.WidthStretch, 0.35f);
            ImGui.TableSetupColumn("Godot resource", ImGuiTableColumnFlags.WidthStretch, 0.65f);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
            ImGui.TableHeadersRow();
            foreach (var (material, path) in remap.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList())
            {
                ImGui.PushID(material);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(ctx.Materials.Contains(material) ? Theme.Text : Theme.TextDim, material);
                if (!ctx.Materials.Contains(material)) Tip("Not in the material library");
                ImGui.TableNextColumn();
                // The buffer only exists while the field is being edited, so undo shows through otherwise.
                var buf = _remapBuffers.TryGetValue(material, out var pending) ? pending : path;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputTextWithHint("##path", "res://materials/name.tres", ref buf, 512)) _remapBuffers[material] = buf;
                if (ImGui.IsItemDeactivatedAfterEdit()) SetRemap(ctx, material, buf.Trim());
                else if (!ImGui.IsItemActive()) _remapBuffers.Remove(material);
                bool invalid = path.Length > 0 && !path.StartsWith("res://", StringComparison.Ordinal);
                if (invalid) Tip("Godot paths start with res://");
                if (path.Length == 0) Tip("Empty: this material is exported as is");
                ImGui.TableNextColumn();
                if (ImGui.Button("×", new Vector2(ImGui.GetFrameHeight(), 0))) remove = material;
                Tip("Remove this remap");
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        if (remove != null)
        {
            string m = remove;
            ctx.Session.Do("Remove Material Remap", () => ctx.Doc.Export.MaterialRemap.Remove(m));
            _remapBuffers.Remove(m);
        }
        if (remap.Values.Any(p => p.Length > 0 && !p.StartsWith("res://", StringComparison.Ordinal)))
            ImGui.TextColored(Theme.Error, "Some paths don't start with res://. Godot can only load project resources.");

        var used = MaterialLibrary.Usage(ctx.Doc).Select(u => u.Material).Where(m => !remap.ContainsKey(m) && MaterialLibrary.ToolKind(m) == ToolMaterial.None).ToList();
        ImGui.SetNextItemWidth(Theme.Px(200));
        if (ImGui.BeginCombo("##add", _newMaterial.Length > 0 ? _newMaterial : "Add a material…"))
        {
            if (used.Count == 0) ImGui.TextColored(Theme.TextDim, "Every material in the level is listed.");
            foreach (var m in used)
                if (ImGui.Selectable(m)) { string pick = m; ctx.Session.Do("Add Material Remap", () => ctx.Doc.Export.MaterialRemap[pick] = $"res://materials/{pick}.tres"); }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(used.Count == 0);
        if (ImGui.Button("Add all used"))
            ctx.Session.Do("Add Material Remaps", () =>
            {
                foreach (var m in used) ctx.Doc.Export.MaterialRemap[m] = $"res://materials/{m}.tres";
            });
        ImGui.EndDisabled();
        Tip("One row per material the level uses, pointing at res://materials/<name>.tres. Edit the paths to match your project.");
    }

    static void SetPath(EditorContext ctx, string path)
    {
        string? value = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        if (value == ctx.Doc.ExportPath) return;
        ctx.Session.Do("Set Export Path", () => ctx.Doc.ExportPath = value);
    }

    void SetRemap(EditorContext ctx, string material, string path)
    {
        _remapBuffers.Remove(material);
        if (ctx.Doc.Export.MaterialRemap.GetValueOrDefault(material) == path) return;
        ctx.Session.Do("Edit Material Remap", () => ctx.Doc.Export.MaterialRemap[material] = path);
    }

    static void Check(EditorContext ctx, string label, string tip, bool value, string undo, Action<Document, bool> set)
    {
        if (ImGui.Checkbox(label, ref value)) { bool v = value; ctx.Session.Do(undo, () => set(ctx.Doc, v)); }
        Tip(tip);
    }

    static void Gap()
    {
        ImGui.Dummy(Theme.Px(0, 4));
        ImGui.Separator();
        ImGui.Dummy(Theme.Px(0, 2));
    }
}
