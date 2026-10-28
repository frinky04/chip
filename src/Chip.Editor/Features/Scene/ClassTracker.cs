using System.Diagnostics;
using Chip.Core.Scene;
using Chip.Editor.Features.Pipeline;
using Chip.Editor.Framework;

namespace Chip.Editor.Features.Scene;

/// <summary>
/// Keeps <see cref="EntityClasses"/> on the game project the level exports into (else the one it's saved in), reloading
/// its manifest when it changes, and says so in the status bar.
/// </summary>
public sealed class ClassTracker
{
    /// <summary>How often the manifest is checked for changes on disk.</summary>
    static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(0.5);
    readonly Stopwatch _sinceCheck = new();
    string? _lastSource, _lastProblem;
    // The level and the classes the last renames were applied for.
    Document? _migratedDoc;
    int _migratedRevision = -1;

    public void Update(EditorContext ctx)
    {
        if (_sinceCheck.IsRunning && _sinceCheck.Elapsed < CheckEvery) return;
        _sinceCheck.Restart();
        bool changed = EntityClasses.Track(PipelineActions.ResolveExportPath(ctx), ctx.Session.Path);
        Migrate(ctx);
        if (!changed) return;

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

    /// <summary>
    /// Moves values and connections stored under a class's old names (<c>"was"</c>) to the current ones, once per level and
    /// set of classes, as one undo step, so a rename in the game never strands what levels hold.
    /// </summary>
    void Migrate(EditorContext ctx)
    {
        if (ReferenceEquals(ctx.Doc, _migratedDoc) && EntityClasses.Revision == _migratedRevision) return;
        _migratedDoc = ctx.Doc;
        _migratedRevision = EntityClasses.Revision;
        if (EntityClasses.Current.Source == null || ctx.Interacting) return;
        // Renames touch entities only: try them on copies first, so a level with nothing to rename gets no undo step.
        var probe = new Document { Entities = ctx.Doc.Entities.Select(e => e.Clone()).ToList() };
        if (LevelMigration.Apply(probe, EntityClasses.Current) == 0) return;
        int renamed = 0;
        ctx.Session.Do("Apply Renamed Names", [], () => renamed = LevelMigration.Apply(ctx.Doc, EntityClasses.Current));
        _migratedDoc = ctx.Doc;
        ctx.Status($"Moved {renamed} value{(renamed == 1 ? "" : "s")} to the names {EntityClasses.Engine} uses now. Undo to see the old ones");
    }
}
