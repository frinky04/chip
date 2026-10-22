using System.Diagnostics;
using Chip.Core.Scene;
using Chip.Editor.Features.Pipeline;
using Chip.Editor.Framework;

namespace Chip.Editor.Features.Scene;

/// <summary>
/// Keeps <see cref="EntityClasses"/> on the Godot project the level exports into (else the one it's saved in), reloading
/// the addon's manifest when it changes, and says so in the status bar.
/// </summary>
public sealed class ClassTracker
{
    /// <summary>How often the manifest is checked for changes on disk.</summary>
    static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(0.5);
    readonly Stopwatch _sinceCheck = new();
    string? _lastSource, _lastProblem;

    public void Update(EditorContext ctx)
    {
        if (_sinceCheck.IsRunning && _sinceCheck.Elapsed < CheckEvery) return;
        _sinceCheck.Restart();
        if (!EntityClasses.Track(PipelineActions.ResolveExportPath(ctx), ctx.Session.Path)) return;

        var source = EntityClasses.Current.Source;
        var problem = EntityClasses.Problem;
        if (problem != null && problem != _lastProblem) ctx.Status(problem, error: true);
        else if (problem == null && source != null)
        {
            int count = EntityClasses.All.Count(c => !c.EditorOnly);
            ctx.Status(source == _lastSource
                ? $"Entity classes reloaded from {Path.GetFileName(EntityClasses.ProjectRoot)} ({count})"
                : $"Using the {count} entity classes of {Path.GetFileName(EntityClasses.ProjectRoot)}");
        }
        _lastSource = source;
        _lastProblem = problem;
    }
}
