using System.Numerics;
using System.Runtime.CompilerServices;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Materials;

/// <summary>
/// Texture > Replace material: swaps one material for another on every face of the level, as one undo step.
/// "Replace" lists the materials the level uses (with face counts); "With" searches the whole library.
/// </summary>
public sealed class ReplaceMaterialDialog
{
    const string Title = "Replace material";
    static readonly ConditionalWeakTable<EditorContext, ReplaceMaterialDialog> Instances = new();

    public static ReplaceMaterialDialog For(EditorContext ctx) => Instances.GetValue(ctx, _ => new ReplaceMaterialDialog());

    bool _request;
    string _from = "", _to = "", _filter = "";

    /// <summary>Opens the dialog. Defaults: replace the selection's material (else the most used), with the active material.</summary>
    public void Show(EditorContext ctx, string? from = null, string? to = null)
    {
        var usage = MaterialLibrary.Usage(ctx.Doc);
        var selected = ctx.Sel.FaceSets(ctx.Doc).SelectMany(kv => kv.Value.Select(f => kv.Key.Faces[f].Material)).FirstOrDefault();
        _from = from ?? selected ?? (usage.Count > 0 ? usage[0].Material : "");
        _to = to ?? ctx.ActiveMaterial;
        if (string.Equals(_from, _to, StringComparison.OrdinalIgnoreCase) && from == null)
            _from = usage.FirstOrDefault(u => !string.Equals(u.Material, _to, StringComparison.OrdinalIgnoreCase)).Material ?? _from;
        _filter = "";
        _request = true;
    }

    public void Draw(EditorContext ctx)
    {
        if (_request)
        {
            ImGui.OpenPopup(Title);
            _request = false;
        }
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f));
        ImGui.SetNextWindowSize(new Vector2(Theme.Px(440), 0));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(20, 16));
        bool open = true;
        if (ImGui.BeginPopupModal(Title, ref open, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize))
        {
            var browser = MaterialBrowser.For(ctx);
            var usage = MaterialLibrary.Usage(ctx.Doc);
            ImGui.TextColored(Theme.TextDim, "Swap one material for another on every face in the level.");
            ImGui.Dummy(Theme.Px(0, 4));

            Label("Replace");
            ImGui.SetNextItemWidth(-1);
            int fromCount = usage.FirstOrDefault(u => string.Equals(u.Material, _from, StringComparison.OrdinalIgnoreCase)).Faces;
            if (ImGui.BeginCombo("##from", _from.Length > 0 ? $"{_from}  ({fromCount})" : "Pick a material", ImGuiComboFlags.HeightLarge))
            {
                if (usage.Count == 0) ImGui.TextColored(Theme.TextDim, "The level has no faces yet.");
                foreach (var (material, faces) in usage)
                    if (Option(ctx, browser, ctx.Materials.Get(material), material, faces == 1 ? "1 face" : $"{faces} faces", _from)) _from = material;
                ImGui.EndCombo();
            }

            Label("With");
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##to", _to.Length > 0 ? _to : "Pick a material", ImGuiComboFlags.HeightLarge))
            {
                if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##filter", "Search", ref _filter, 128);
                foreach (var m in ctx.Materials.All)
                {
                    if (_filter.Length > 0 && !m.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Option(ctx, browser, m, m.Name, m.Folder, _to)) _to = m.Name;
                }
                ImGui.EndCombo();
            }

            ImGui.Dummy(Theme.Px(0, 6));
            bool same = string.Equals(_from, _to, StringComparison.OrdinalIgnoreCase);
            bool can = fromCount > 0 && _to.Length > 0 && !same;
            ImGui.BeginDisabled(!can);
            string label = fromCount == 1 ? "Replace 1 face" : $"Replace {fromCount} faces";
            if (ImGui.Button(label, new Vector2(Theme.Px(160), 0)))
            {
                string from = _from, to = _to;
                int n = 0;
                ctx.Session.Do("Replace Material", () => n = ctx.Materials.Replace(ctx.Doc, from, to));
                ctx.Status($"Replaced {from} with {to} on {(n == 1 ? "1 face" : $"{n} faces")}");
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            if (!can) Tip(same ? "Pick two different materials" : "Pick a material the level uses");
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(Theme.Px(96), 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        ImGui.PopStyleVar();
    }

    /// <summary>Dim label in a column wide enough for "Replace" (the inspector's column is narrower).</summary>
    static void Label(string text)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, text);
        ImGui.SameLine(ImGui.GetStyle().WindowPadding.X + Theme.Px(80));
    }

    /// <summary>One combo row: thumbnail (requested only when on screen), name, dim detail.</summary>
    static bool Option(EditorContext ctx, MaterialBrowser browser, MaterialInfo m, string name, string detail, string current)
    {
        float s = Theme.Px(22);
        var p = ImGui.GetCursorScreenPos();
        bool picked = ImGui.Selectable($"##{name}", string.Equals(name, current, StringComparison.OrdinalIgnoreCase), ImGuiSelectableFlags.None, new Vector2(0, s));
        if (ImGui.IsItemVisible())
        {
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(p, p + new Vector2(s), ImGui.ColorConvertFloat4ToU32(Theme.Frame), Theme.Px(2));
            if (browser.Thumbs.Get(ctx, m) is var tex and not 0) dl.AddImage(tex, p, p + new Vector2(s));
            float ty = p.Y + (s - ImGui.GetTextLineHeight()) * 0.5f;
            dl.AddText(new Vector2(p.X + s + Theme.Px(8), ty), ImGui.ColorConvertFloat4ToU32(Theme.Text), name);
            if (detail.Length > 0)
            {
                float dw = ImGui.CalcTextSize(detail).X;
                dl.AddText(new Vector2(ImGui.GetItemRectMax().X - dw - Theme.Px(6), ty), ImGui.ColorConvertFloat4ToU32(Theme.TextDim), detail);
            }
        }
        return picked;
    }
}
