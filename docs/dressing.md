# Dressing

Status: phases 0 (tags) and 1 (engine, `bevel`, `sweep`) built; proxies, `repeat` and `scatter` are design. Name:
**Dressing** (as in set dressing). Try it: Tools > Open dressing sample.

You build the blockout. Dressing turns it into the level you ship, following rules written in a JSON file: trims along every
wall base, bevels on every outside corner, a railing built from a box, stairs from a ramp, rubble on the floors. Change the
blockout and the level follows. Swap the rules file and the same blockout becomes a different level.

## Principles

1. **One direction.** The blockout is the only thing you edit. The dressed level is computed from it and never edited
   by hand (per-object Bake is the escape hatch).
2. **Two stages, both reading only the blockout.**
   - *Shape* replaces a blockout mesh: a **modifier** (bevel) swaps in a modified copy; a **proxy** (railing, stairs) hides
     the mesh and builds from its box.
   - *Dress* adds: **sweep** (a profile along edges), **repeat** (a prop every N m along edges), **scatter** (props over
     faces).
   - No rule reads another rule's output. A mesh takes at most one Shape rule; two claiming the same mesh is an error
     naming both.
3. **Failures stay local.** A rule that fails on a target skips that target (a failed proxy leaves its box visible) and
   reports why. A broken rules file keeps the last good rules. The level always renders and exports.
4. **Stable.** Same blockout + same rules = same output, byte for byte (`ContentHash`). Randomness is seeded from quantised
   world positions and the rule id, never from face indices, so editing one room never reshuffles another.
5. **Incremental.** Editing one wall rebuilds that wall's results and its neighbours', nothing else.
6. **No** node graphs, scripting, expressions, booleans or holes, or rules reading rules.

## A session, end to end

1. You block out a hangar: floor slabs, wall boxes, a ramp, a box where the balcony railing goes.
2. You tag surfaces: select the wall faces, Ctrl+T, type `wall`, Enter; the floors get `floor`. In object mode, Ctrl+T
   tags the ramp mesh `ramp` and the box `railing`. (In the blockout view, meshes a proxy rule claims get a striped
   overlay tinted by kind, so you can spot them; their own material is left alone.)
3. The level references `hangar.dressing.json`, which imports `styles/industrial.dressing.json`. An agent wrote that file;
   Chip reloads it whenever it changes on disk.
4. The viewport shows the dressed level: walls with 2 cm bevelled corners, a 12 cm baseboard where every wall meets a floor,
   mitred at corners and stopping at doorways, steps in place of the ramp, a railing with balusters every 12 cm.
5. You drag a wall out 2 m. Its bevel, its baseboard and the baseboards of the two walls it touches rebuild; nothing else
   does. Wireframe and selection stay on the blockout, so editing reads cleanly. `` ` `` (left of 1) flips between the
   dressed and blockout views.
6. You click a baluster: the railing box is selected. Its inspector lists the rules that apply, with the railing's
   parameters as fields; you set this one's baluster spacing to 10 cm (stored on the mesh, not in the rules file).
7. One corner looks wrong. You tag that face `skip:baseboard`, or Bake that wall and fix it by hand.
8. Ctrl+E exports the dressed level. Collision still comes from the blockout (a solid box for the railing, a ramp for the
   stairs), which is usually what you want.
9. Next level: same style file, different blockout.

## Rules files

Rules are keyed by id, so every value has a stable path (`rules.debris.perM2`), diffs are one line, and a level file
overrides an imported rule by deep merge. `kind` decides the stage. Lengths are metres as plain numbers; angles live in keys
ending `Deg`; density is `perM2`. `$name` substitutes a whole value from `vars`; there are no expressions. Unknown keys are
errors with a did-you-mean.

A shared style, `styles/industrial.dressing.json`:

```json
{
  "$schema": "./dressing-1.schema.json",
  "chipDressing": 1,
  "name": "Industrial",
  "vars": { "trim": "metal_trim", "baseHeight": 0.12 },
  "rules": {
    "wall-bevel": { "kind": "bevel", "faces": { "tags": ["wall"] }, "edges": "convex", "minDeg": 60, "width": 0.02, "segments": 2 },
    "baseboard": { "kind": "sweep", "faces": { "tags": ["wall"] }, "edges": "contact", "onto": { "facing": "up" },
                   "profile": { "rect": [0.015, "$baseHeight"] }, "material": "$trim" },
    "crown":     { "kind": "sweep", "faces": { "tags": ["wall"] }, "edges": "contact", "onto": { "facing": "down" },
                   "profile": { "points": [[0, 0], [0.08, 0], [0, 0.08]] }, "material": "$trim" },
    "railing":   { "kind": "railing", "meshes": { "tags": ["railing"] }, "height": 1.0, "balusterEvery": 0.12,
                   "postEvery": 1.5, "rail": { "round": 0.05 }, "material": "$trim" },
    "stairs":    { "kind": "stairs", "meshes": { "tags": ["ramp"] }, "stepHeight": 0.18 },
    "debris":    { "kind": "scatter", "faces": { "facing": "up", "materials": ["concrete*"], "notTags": ["clean"] },
                   "props": ["props/rubble_a.glb", "props/rubble_b.glb"], "perM2": 0.3, "minGap": 0.4,
                   "scale": [0.8, 1.2], "seed": 7 }
  }
}
```

The level's own file, `hangar.dressing.json` (the `.chip` stores `"dressing": "hangar.dressing.json"`):

```json
{
  "chipDressing": 1,
  "imports": ["../styles/industrial.dressing.json"],
  "vars": { "baseHeight": 0.2 },
  "rules": {
    "crown": { "enabled": false },
    "debris": { "perM2": 0.8 },
    "pipes": { "kind": "sweep", "faces": { "tags": ["ceiling"] }, "edges": "concave",
               "profile": { "round": 0.1 }, "offset": [0, -0.15], "material": "pipe_red" }
  }
}
```

An entry with `kind` defines a rule; one without patches an imported rule (patching an id nothing defines is an error).
Later imports win; the importing file wins over all. Import cycles are errors.

**Defaults make the short version work.** `"baseboard": { "kind": "sweep", "faces": { "tags": ["wall"] } }` sweeps a
1.5 × 12 cm strip along wherever wall faces meet up-facing faces, in the wall's material. Bevel defaults to convex edges
over 30°, 2 cm, 1 segment.

**On the blockout**, a mesh carries its tags, per-rule overrides and opt-outs (in the `.chip`):

```json
{ "id": 42, "name": "balcony_rail", "tags": ["railing"],
  "dressing": { "set": { "railing": { "balusterEvery": 0.10 } }, "skip": ["debris"] }, ... }
```

**Names.** Tags and rule ids share one form: lowercase letters, digits, `-` and `_`, starting with a letter or digit, at
most 64 characters (`wall`, `trim-base`, `wall-bevel`). A face opts out of one rule with the reserved tag
`skip:<rule id>`. Typed tags are normalised (`Wall Trim` becomes `wall-trim`); files with invalid tags fail to load with
the mesh and the reason.

### Vocabulary (v1)

| Kind | Stage | Targets | Key parameters | |
|---|---|---|---|---|
| `bevel` | shape | edges between two matching faces | `faces`, `edges` (convex/concave), `minDeg`, `width`, `segments` | built |
| `sweep` | dress | edges of matching faces, chained across meshes | `faces`, `edges`, `onto`, `minDeg`, `profile` (`rect`, `round` + `sides`, `points`), `offset`, `material` | built |
| `railing` | shape | meshes | `height`, `balusterEvery`, `postEvery`, `rail` profile, `baluster`/`post` prop or size | phase 2 |
| `stairs` | shape | meshes | `stepHeight` (fits the nearest whole count to the box) | phase 2 |
| `repeat` | dress | edge chains | `prop`, `every`, `start`, `align` | phase 2 |
| `scatter` | dress | faces | `props`, `perM2`, `minGap`, `yawDeg`, `scale`, `seed`, `nearEdges` | phase 2 |

The full, current parameter list with descriptions is the generated schema: `Chip --rules-schema` (Chip also writes
`dressing-1.schema.json` beside rules files it creates or opens). Sweeps are textured world-aligned with the source face's
scale; a texture running along the trim (`uv: along`) is later.

Face selectors: `tags`, `notTags`, `materials` (globs), `facing` (`up`, `down`, `upright`). Mesh selectors: `tags`. Edge
kinds: `contact` (with `onto`: an edge of these faces lying on a matching face of **any** mesh, 1 mm tolerance; walls and
floors are separate meshes, so this is spatial), `boundary`, `convex`, `concave`, `border` (the outline of the matched
faces). All keys in a selector must hold.

Proxies read their box through `PrimitiveFrame`: run along the longest horizontal side, up is Y, so "top long edges" and
"ends" mean the same thing on every railing. Stairs reuse the existing stairs primitive.

### Checking rules: `Chip.exe --rules-check level.chip [--json]`

Loads the rules, dresses the level headlessly and exits 0 (clean), 1 (warnings), 2 (errors), 3 (no rules file) or 4 (the
level can't be read). `--json` gives the same as structured data. This is how an agent closes the loop; `--shot` shows
what the result looks like. Real output, for the sample with a typo and a misspelt tag:

```
rules: .../chip-dressing-broken/room.dressing.json  chipDressing 1
ERROR E104 .../room.dressing.json:17:7  rules.pillar-bevel.widht
      unknown key "widht" for kind "bevel"
      fix: did you mean "width"? (allowed: kind, enabled, note, faces, edges, minDeg, width, segments)
WARN  W402 .../room.dressing.json:20:13  rules.pipe
      pipe matched nothing: no face matches tags plinht, facing upright
      fix: did you mean the tag "plinth"?
rule             stage  targets      outputs    skipped  ms
baseboard        dress  11 edge      6 mesh     0        52.0
pipe             dress  0 edge       0 mesh     0        2.0
pillar-bevel     -      not run (errors above)
1 error, 1 warning.  content 37880ade
```

Other codes: W310 (bevel wider than half the shortest edge; the mesh is left as is), W311 (the bevel broke a mesh), W520
(two trim ends within 5 cm that don't meet: a blockout gap, with its position), E330 (two shape rules on one mesh; neither
applies), E901 (a rule threw on a mesh; that mesh is skipped).

Every diagnostic has a code, file:line:col, JSON path, mesh id when relevant, what happened and a fix. A selector that
matches nothing is always a warning: it is the commonest mistake. Chip writes `dressing-1.schema.json` (generated from the
same code that parses the rules, so they can't drift) beside any rules file it creates, so editors validate offline.

## Code

### Interface (`src/Chip.Core/Dressing/`)

```csharp
/// Meshes with change tokens. Two adapters: Blockout (the session) and DressedLevel. The renderer draws from it.
public interface IMeshSet { IReadOnlyList<PolyMesh> Meshes { get; } int Revision(PolyMesh mesh); }

public sealed class Dresser(IDressingFiles files)
{
    /// Never mutates doc; never throws on rule content. Caches between calls; one thread. Returns the same object when
    /// nothing changed. Rules reload when the path changes or (checkFiles) a rules file changed.
    public DressedLevel Dress(Document doc, string? rulesPath, Func<PolyMesh, int>? revision = null, bool checkFiles = true);
    public bool RulesChanged(string? rulesPath);   // cheap poll for the editor's four-times-a-second check
    public RuleSet Rules { get; }
    public int LastRebuilds { get; }               // jobs run (not reused) by the last pass: tests and benches
}

public sealed class DressedLevel : IMeshSet
{
    public MeshOrigin Origin(PolyMesh mesh);       // (SourceId, Rule?, Role: Passthrough | Modified | Added)
    public IReadOnlyList<(string Rule, DressRole Role)> RulesFor(int meshId);
    public IReadOnlyList<Diagnostic> Diagnostics { get; }   // rule-file problems, then dressing problems
    public IReadOnlyList<RuleStats> Stats { get; }
    public RuleSet Rules { get; }
    public string ContentHash { get; }             // stable across runs and machines: did an edit change the output?
    public Document ForExport();                   // dressed meshes + collision-only blockout stand-ins
}

public interface IDressingFiles { string? ReadText(string path); long Stamp(string path); }   // DiskFiles, MemoryFiles
public static class DressingRules { string? PathFor(Document, string? levelPath, IDressingFiles); string DefaultPathFor(string levelPath); }
public static class RulesCheck { int Run(Document, string? levelPath, IDressingFiles, TextWriter, bool json); }
public static class DressingSchema { string Generate(); string Starter(string? levelName); }
```

Invariants: blockout meshes pass through as the same objects with the session's revisions. Generated meshes have negative
ids, stable per (rule, source) for the dresser's life, and revisions from a counter shared by every dresser, so GPU caches
keyed on id and revision are always right. Generated meshes are `Locked` and `Collision = None`. Outputs come in blockout
order whatever was rebuilt, so exports are stable.

### Behind the seam (`src/Chip.Core/Dressing/`)

- **`JNode`**: a JSON reader that keeps line and column for every value (comments and trailing commas allowed).
- **`RuleSet`** (`Rules.cs`): imports (relative paths, cycles reported), vars, per-level patches by rule id, rule ids
  checked like tags, kinds looked up with did-you-mean, every parameter typed through **`ParamSpec`** tables
  (`Params.cs`), which also generate the schema. A rule with errors is left out; the rest run.
- **`BevelKind`**: the edges between two matching faces (convex or concave, at `minDeg` or more), bevelled with
  `MeshOps.BevelEdges` on a clone.
- **`SweepKind`**: segments per mesh: contact (the matching face's edges lying on an `onto` face of any neighbouring mesh,
  inside its outline, with the face standing on it; spans over split floors merge), boundary, border, convex, concave.
  Contact segments drop stretches where the space just off the edge is inside another closed mesh, so walls overlapping at
  corners end their trims at the corner and mitre into each other. `Build` sweeps the profile with mitred joints (closest
  approach of each profile point's lines, capped) and end caps, skips faces lying against the wall or floor, and auto-smooths
  at 50° so round profiles shade smooth.
- **`Dresser`**: jobs per (rule, mesh). Bevel jobs key on the mesh's revision. Sweep segment jobs also key on the ids and
  revisions of meshes touching it (a `NeighbourIndex` kept between passes, re-queried only around meshes that changed).
  Segment ends live in a per-rule `JoinIndex` (tolerance-sized cells) that recomputes joints only near changed ends;
  sweep outputs rebuild when their segments or joints change. Dirty jobs run in parallel, each in its own try/catch, and
  keep their diagnostics with their results.

Performance (stress level: 2,000 meshes, 100k faces, every upright face tagged, a baseboard and a bevel rule; Release,
`dotnet run -c Release --project tests/Chip.Bench -- dress`): opening (cold) about 120 ms; a frame with nothing changed
about 30 µs; a drag frame moving one wall about 1.5 ms.

### Tags

Built (phase 0):

- `Face.Tags` and `PolyMesh.Tags`: an immutable, interned, sorted `TagSet` (`src/Chip.Core/Geometry/TagSet.cs`) with value
  equality; `default` is empty. Phase 2 adds `PolyMesh.Dressing` (`Set` overrides, `Skip`).
- `Face.Derive(int[] verts)` copies material, mapping and tags. Every op that builds a face from a source face uses it, so
  the next surface property costs nothing. It doesn't copy explicit UVs or dominance: a bevel strip or extrude side isn't
  the dominant face. Pieces of the same face (clip, knife, connect) set those themselves.
- `MeshHash`, undo's mesh comparison, `Clone` and the serializer include tags (optional `tags` arrays; no version bump).
  Without the hash, tagging wouldn't rebuild anything downstream; without the comparison, a tag-only edit wouldn't undo.
- `TagOps` (`src/Chip.Core/Scene/TagOps.cs`): usage counts, coverage of the selection, set/remove, select by tag. Face mode
  tags faces, object mode tags meshes.
- `TagTests.Every_op_keeps_tags_on_every_face` runs every mesh op on a tagged cube. It caught Connect dropping tags (it built
  its pieces with a target-typed `new`) on its first run.
- Editor (`src/Chip.Editor/Features/Tagging/`): Ctrl+T popup at the mouse (type to filter or add, Enter adds or removes,
  Ctrl+Enter selects everything with the tag, suggestions from the level so typos show as new tags); inspector Tags section
  with chips (`wall 2/5` when partial; click adds to the rest, × removes, Ctrl+click selects all).

### Editor (`src/Chip.Editor/Features/Dressing/`)

Built (phase 1):

- `DressingFeature` owns a `Dresser` per open level and sets `EditorContext.Dressed`: it re-dresses when the document
  changes and polls the rules files four times a second, so saving the JSON (by hand or by an agent) updates the viewport.
  Reloads say so in the status bar; rules with errors show there in red.
- Renderer: `BeginFrame(..., scene)` draws solid faces from the dressed level when `ShowDressed`; wires, selection and
  overlays stay on the blockout, so editing reads cleanly. Picking stays on the blockout in every mode for now (clicking a
  trim selects nothing; phase 2 maps generated meshes back to their source for proxies).
- Export (GLB and OBJ) writes `EditorContext.ExportDocument()`: the dressed level when there are rules, even with the
  blockout view showing. The exporter is unchanged; collision stand-ins reuse `CollisionMode.Only` and the collision bake.
- `` ` `` toggles dressed / blockout (View > Dressed view), and so do the "Dressed" header toggle (only shown for levels
  with rules) and the inspector's Dressed / Blockout switch.
- Tools > New dressing rules file writes `<level>.dressing.json` (a starter: baseboards on `wall` faces, a disabled corner
  bevel) and the schema beside a saved level. Tools > Open dressing rules opens it in the default app. Tools > Open
  dressing sample writes the sample room to a temp folder and opens it.
- Inspector Dressing section: the switch, Open rules, which rules touch the selection, what each rule made, and problems
  with their fixes (the selection's first; a button selects the mesh a problem is about).
- `Chip --rules-check level.chip [--json]` and `Chip --rules-schema [out.json]` run without a window.

### Tests (through the interface)

`DressingTests` (core): mitred room of separate overlapping walls, doorway with wrapped jambs, round pipe on concave
corners, bevels and their export stand-ins, width and conflict warnings, imports/vars/overrides, every diagnostic's fix,
zero-match hints, live reload, `--rules-check` exit codes, schema and starter, a rebuild count proving one edit reruns four
jobs, and a randomised test (four seeds, 60 edits each: moves, retags, hide, delete, add) that the incremental result always
equals a fresh dress, diagnostics included. `DressingFeatureTests` (editor): the sample dresses and exports, the view toggle,
blockout and rules-file edits redress, New rules file. Captures: `--shot - out.png dressing` (and `dressing-blockout`);
`CHIP_SHOT_CAMERA` frames close-ups.

## Phases

| Phase | Ships | Done when |
|---|---|---|
| **0. Tags** (built) | `TagSet`, `Face.Derive`, hash/serializer, Ctrl+T popup, Tags section, select by tag | Guard test passes; tags survive every op and undo; useful on its own for selection |
| **1. Engine, bevel, sweep** (built) | `Dresser`, rules files with imports, `--rules-check`, schema, view toggle, render/export wiring, Dressing inspector, `bevel`, `sweep`, the sample | Baseboards and bevels follow wall edits; incremental equals fresh under random edits; a one-wall drag costs 1.5 ms on the stress level |
| **2. Proxies and props** | `railing`, `stairs`, `repeat`, `scatter`, props, per-object overrides UI, Bake | Railing and stairs fixtures; scatter stable under unrelated edits |
| **Later** | Export instancing, height bands (chair rails), style library browser, more modifiers | |

## Known limits

- Sweeps follow the sharp blockout corner, so a baseboard overlaps a 2 cm bevel. Invisible at trim thickness; a
  `clipToShape` option can come later.
- Contact ends join when exactly two meet within 1 mm. Overlapping walls are handled (hidden stretches are dropped, so ends
  meet at the corner); three or more ends at one point (a T-junction) stay open with caps. Ends within 5 cm that don't meet
  are reported (W520) with their position.
- Only closed meshes hide trim stretches; an open mesh (a single plane) never does.
- Convex sweeps sit in the outside quadrant of the corner (a bead), not wrapped round it.
- Picking ignores generated meshes (phase 2). Bake isn't built yet (phase 2); when it is, it's a snapshot that stops following
  the blockout.
- Only one Shape rule per mesh, so a mesh can't be both bevelled and a proxy.
