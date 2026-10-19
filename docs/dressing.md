# Dressing

Status: phase 0 (tags) built; the rest is design. Name: **Dressing** (as in set dressing).

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

| Kind | Stage | Targets | Key parameters |
|---|---|---|---|
| `bevel` | shape | edges of faces | `edges` (convex/concave), `minDeg`, `width`, `segments` |
| `railing` | shape | meshes | `height`, `balusterEvery`, `postEvery`, `rail` profile, `baluster`/`post` prop or size |
| `stairs` | shape | meshes | `stepHeight` (fits the nearest whole count to the box) |
| `sweep` | dress | edge chains | `profile` (`rect`, `round`, `points`), `offset`, `material`, `uv` (`along`) |
| `repeat` | dress | edge chains | `prop`, `every`, `start`, `align` |
| `scatter` | dress | faces | `props`, `perM2`, `minGap`, `yawDeg`, `scale`, `seed`, `nearEdges` |

Face selectors: `tags`, `notTags`, `materials` (globs), `facing` (`up`, `down`, `upright`). Mesh selectors: `tags`. Edge
kinds: `contact` (with `onto`: an edge of these faces lying on a matching face of **any** mesh, 1 mm tolerance; walls and
floors are separate meshes, so this is spatial), `boundary`, `convex`, `concave`, `border` (the outline of the matched
faces). All keys in a selector must hold.

Proxies read their box through `PrimitiveFrame`: run along the longest horizontal side, up is Y, so "top long edges" and
"ends" mean the same thing on every railing. Stairs reuse the existing stairs primitive.

### Checking rules: `Chip.exe --rules-check level.chip [--json]`

Loads the rules, dresses the level headlessly and exits 0 (clean), 1 (warnings) or 2 (errors). This is how an agent closes
the loop; `--shot` shows what the result looks like.

```
rules: hangar.dressing.json (+1 import)  chipDressing 1
ERROR E104 styles/industrial.dressing.json:18:7  rules.railing.balusterSpace
      unknown key "balusterSpace" for kind "railing"
      fix: did you mean "balusterEvery"? (allowed: height, balusterEvery, postEvery, rail, baluster, post, material)
WARN  W310 rules.wall-bevel  mesh 41 "corridor_b"
      width 0.02 is more than half the shortest selected edge (0.012); mesh skipped
      fix: lower width below 0.012, or tag its faces "skip:wall-bevel"
WARN  W402 rules.pipes  matched 0 edges
      no face has tag "ceiling" (tags in level: wall ×212, floor ×38, roof ×9)
      fix: tag the ceiling faces "ceiling", or use "tags": ["roof"]
WARN  W520 rules.baseboard  chain gap of 3 mm at (12.0, 0, 4.5) between meshes 7 and 9
rule       stage  targets   outputs    skipped  ms
wall-bevel  shape  37 mesh   36 mesh    1        4.1
railing    shape  3 mesh    3 mesh     0        2.0   (1 override)
stairs     shape  2 mesh    2 mesh     0        0.6
baseboard  dress  64 chain  64 mesh    0        3.3
crown      -      disabled
debris     dress  38 face   412 prop   0        1.9
pipes      dress  0         0          0        0.0
1 error, 3 warnings.  content 9f3a1c
```

Every diagnostic has a code, file:line:col, JSON path, mesh id when relevant, what happened and a fix. A selector that
matches nothing is always a warning: it is the commonest mistake. Chip writes `dressing-1.schema.json` (generated from the
same code that parses the rules, so they can't drift) beside any rules file it creates, so editors validate offline.

## Code

### Interface (`src/Chip.Core/Dressing/`)

```csharp
/// What the renderer and picker draw from: meshes plus change tokens. Two adapters: the blockout and the dressed level.
public interface IMeshSet
{
    IReadOnlyList<PolyMesh> Meshes { get; }
    int Revision(PolyMesh mesh);   // changes whenever that mesh's content does; never repeats for a mesh
}

public sealed class Dresser
{
    public Dresser(IDressingFiles files);
    /// Never mutates doc; never throws on rule or prop content. Keeps its cache between calls; one thread.
    public DressedLevel Dress(Document doc, Func<PolyMesh, int>? revision = null);   // null: content hash (CLI, export)
}

public sealed class DressedLevel : IMeshSet
{
    public MeshOrigin Origin(PolyMesh mesh);                 // (SourceId, RuleId?, Role: Passthrough|Modified|Proxy|Added)
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public IReadOnlyList<RuleStats> Stats { get; }
    public IReadOnlyList<RuleMatch> RulesFor(int meshId);   // inspector: which rules touch this mesh, with its parameters
    public string ContentHash { get; }
    public Document ForExport();                             // dressed meshes + blockout collision stand-ins
    public IReadOnlyList<PolyMesh> Bake(int sourceId);       // fresh editable copies of that source's output
}

public interface IDressingFiles { string? ReadText(string path); byte[]? ReadBytes(string path); long Stamp(string path); }
public static class RulesCheck { public static int Run(string levelPath, IDressingFiles files, TextWriter output, bool json); }
```

Invariants: blockout meshes pass through as the same objects with their own revisions. Generated meshes have negative,
stable ids per (source, rule, chain anchor) and revisions from the dresser's own counter, so per-mesh GPU and pick caches
keyed on id and revision stay correct with no extra work. Generated meshes are `Locked` and `Collision = None`. Every source
mesh appears exactly once as passthrough, modified, or omitted (a proxy).

Seams with two real adapters: `IMeshSet` (Blockout, DressedLevel), `IDressingFiles` (DiskFiles watching stamps four times
a second, MemoryFiles for tests). The revision function is a delegate: `Session.MeshRevision` in the editor, `MeshHash`
elsewhere.

### Behind the seam

- **Rules**: a position-tracking JSON reader (line, column, path), imports, overrides, vars, per-kind parameter tables that
  also generate the schema, did-you-mean.
- **Kinds**: one internal file per kind (`Kinds/Sweep.cs`, `Kinds/Railing.cs`...) behind an internal `IKind`; adding a kind
  touches nothing else and appears in the schema.
- **Selection**: face and mesh selectors; edge classification; a `ContactIndex` (spatial hash of matching faces across all
  meshes, 1 mm); `EdgeChains` (join collinear edges, chain across meshes by welding ends within 1 mm, split at T-junctions,
  mitre frames). Sweep and repeat share chains.
- **Jobs**: one job per (rule, target mesh), keyed by the rule's fingerprint, the target's revision, its overrides, and the
  ids and revisions of meshes within the rule's reach (bounds query). Moving a floor changes the neighbour sets of the walls
  it left and the walls it reached, so both rebuild. Dirty jobs run in parallel, each in its own try/catch.
- **Props**: `GltfImporter`, cached by path and stamp. Scatter merges a rule's props into one mesh per source in v1
  (instancing is a later change to `ForExport` and the renderer).

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

- `EditorContext.Scene : IMeshSet` is the dressed level or the blockout, per the view toggle.
- Renderer: solid faces from `Scene`; wires, selection and overlays from the blockout. `PickCache` takes an `IMeshSet`:
  vertex/edge/face modes pick the blockout; object mode picks `Scene` and selects `Origin(hit).SourceId`.
- Export: `GlbExporter.Build(dressed.ForExport(), ...)`; the exporter itself doesn't change, and collision stand-ins reuse
  `CollisionMode.Only` and the collision bake.
- Commands: toggle dressed view (`` ` ``, plus a header toggle beside Backfaces), bake, open rules file, new rules file.
  The tags popup also suggests tags the rules mention. Inspector: a Dressing section (rules touching this mesh, override
  fields from the parameter tables, diagnostics, Bake). The status bar shows the rules' error count.
- Proxy overlay: in the blockout view, meshes a proxy rule claims draw a striped overlay tinted by kind, labelled with the
  rule id on hover; a mesh tagged for a proxy with no matching rule has no overlay, so it reads as undressed.
- Drags re-dress only dirty jobs each frame. If `--bench` shows that's too slow, slow jobs wait for release and their
  source shows undressed meanwhile.

### Tests (through the interface)

`Dresser` + `MemoryFiles` + small documents: geometry per kind (mitres, contact across meshes, doorway gaps, stair counts);
`ContentHash` stable across runs and unchanged by edits elsewhere; a rebuild counter proving one wall edit rebuilds only its
jobs; incremental output equal to a fresh full dress after random edit sequences; golden diagnostics; the tag guard test;
`--rules-check` exit codes; captures via `--shot`.

## Phases

| Phase | Ships | Done when |
|---|---|---|
| **0. Tags** (built) | `TagSet`, `Face.Derive`, hash/serializer, Ctrl+T popup, Tags section, select by tag | Guard test passes; tags survive every op and undo; useful on its own for selection |
| **1. Engine, bevel, sweep** | `Dresser`, rules files with imports, `--rules-check`, schema, view toggle, render/pick/export wiring, Dressing inspector, `bevel`, `sweep` | Fixture level: baseboards and bevels follow wall edits; incremental test passes; one-wall drag stays under the frame budget in `--bench` |
| **2. Proxies and props** | `railing`, `stairs`, `repeat`, `scatter`, props, per-object overrides UI, Bake | Railing and stairs fixtures; scatter stable under unrelated edits |
| **Later** | Export instancing, height bands (chair rails), style library browser, more modifiers | |

## Known limits

- Sweeps follow the sharp blockout corner, so a baseboard overlaps a 2 cm bevel. Invisible at trim thickness; a
  `clipToShape` option can come later.
- Contact chains are the risky part: gaps over 1 mm, split coplanar faces and T-junctions break or double trims. Chains are
  welded and merged before sweeping, and every gap or junction becomes a warning with its position.
- Bake is a snapshot: the baked copy stops following the blockout.
- Only one Shape rule per mesh, so a mesh can't be both bevelled and a proxy.
