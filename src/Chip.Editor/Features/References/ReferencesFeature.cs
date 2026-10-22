using System.Diagnostics;
using Chip.Core.Scene;
using Chip.Editor.Features.Scene;
using Chip.Editor.Framework;

namespace Chip.Editor.Features.References;

/// <summary>
/// Reference models: a glTF (a door, a prop, the built-in figure) drawn in the level for fit and scale, placed with the
/// entity tool and saved as a <c>ref_model</c> entity. Editor-only unless exported as a marker node. Edited files reload
/// on their own.
/// </summary>
public sealed class ReferencesFeature : IEditorFeature
{
    /// <summary>How often model files are checked for changes on disk.</summary>
    static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(0.5);
    readonly Stopwatch _sinceRefresh = Stopwatch.StartNew();

    public void Register(EditorContext ctx)
    {
        ctx.AddSection(new ReferenceSection());
        ctx.AddPanel(Track);

        var c = ctx.Commands;
        // Also in the Place tab of the dock; here for the palette.
        c.Add("references.place", "Place reference model", () => Activate(ctx, null));
        c.Add("references.placeFile", "Place reference model from file…", () =>
        {
            if (ReferencePicker.ChooseFile() is { } file) Activate(ctx, file);
        });
        c.AddReference("Entity", "Click a wall (reference model)", "fit the model into or against the wall");
    }

    /// <summary>Relative model paths follow the open level; edited model files reload.</summary>
    void Track(EditorContext ctx)
    {
        ReferenceModels.LevelPath = ctx.Session.Path;
        if (_sinceRefresh.Elapsed < RefreshEvery) return;
        _sinceRefresh.Restart();
        ReferenceModels.Refresh();
    }

    /// <summary>Switches to the entity tool set to place reference models (of <paramref name="model"/> when given).</summary>
    static void Activate(EditorContext ctx, string? model)
    {
        if (ctx.Tools.OfType<EntityTool>().FirstOrDefault() is not { } tool) return;
        tool.ClassName = ReferenceModels.ClassName;
        if (model != null) tool.Model = model;
        ctx.SetTool(tool);
        var (loaded, error) = ReferenceModels.Get(tool.Model);
        ctx.Status(loaded != null ? $"Click to place {ReferenceModels.DisplayName(tool.Model)}. On a wall it sits in the wall if it fits, else against it" : error!, error: loaded == null);
    }
}
