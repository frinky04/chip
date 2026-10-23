# Dressing

Status: built (tags; the engine; `bevel`, `sweep`, `railing`, `stairs`, `repeat`, `scatter`; props; per-mesh overrides;
Bake; the Dressing tab, recipes, Dress with, styles and tag colours). Name: **Dressing** (as in set dressing).
Try it: the Dressing tab's ⋯ menu > Open dressing sample.

You build the blockout. Dressing turns it into the level you ship by following rules: a trim along every wall base,
bevels on outside corners, a railing built from a box, stairs from a ramp, rubble on the floors. Change the blockout and
the dressing follows. Swap the style and the same blockout becomes a different level.

You make and tune rules in the editor. They are saved as a JSON file beside the level, so agents and scripts can read and
write the same rules, and Chip reloads the file whenever it changes on disk.

## Principles

1. **One direction.** The blockout is the only thing you edit. The dressed level is computed from it and never edited
   by hand (per-object Bake is the escape hatch).
2. **The file is the truth.** The Dressing tab is a view of the rules file: every change is written to it at once,
   as an undo step. Hand and agent edits to the file show up in the tab and the viewport within a quarter second.
3. **Two stages, both reading only the blockout.**
   - *Shape* replaces a blockout mesh: a **modifier** (bevel) swaps in a modified copy; a **proxy** (railing, stairs) hides
     the mesh and builds from its box.
   - *Dress* adds: **trim** (`sweep`, a profile along edges), **props along edges** (`repeat`), **scatter** (props over
     faces).
   - No rule reads another rule's output. A mesh takes at most one Shape rule; two claiming the same mesh is an error
     naming both.
4. **Failures stay local.** A rule that fails on a target skips that target (a failed proxy leaves its box visible) and
   reports why. A broken rules file keeps the last good rules. The level always renders and exports.
5. **Stable.** Same blockout + same rules = same output, byte for byte (`ContentHash`). Randomness is seeded from quantised
   world positions and the rule id, never from face indices, so editing one room never reshuffles another.
6. **Incremental.** Editing one wall rebuilds that wall's results and its neighbours', nothing else.
7. **No** node graphs, scripting, expressions, booleans or holes, or rules reading rules.

## A session, end to end

1. You block out a hangar: floor slabs, wall boxes, a ramp, a box where the balcony railing goes. Save it.
2. Select the wall boxes and press Ctrl+Shift+T (**Dress with**). Pick *Add Baseboard*: Chip adds a baseboard rule and
   tags the walls' upright faces `wall`, in one undo step. A baseboard appears wherever a wall stands on a floor, mitred
   at corners and stopping at doorways.
3. Select the ramp, Dress with *Add Stairs*; select the railing box, Dress with *Add Railing*. In the blockout view
   (`` ` ``) meshes a proxy rule replaces get stripes tinted by kind.
4. Press Ctrl+Shift+D (the **Dressing** tab). The baseboard rule is selected: set the profile to *Cove*, 8 cm; pick a
   material. Each change redresses the level as you release the mouse. Hovering a rule in the list tints what it takes
   in the viewport.
5. Add *Corner bevel* from **+ Add rule**: 2 cm bevels on every outside corner between `wall` faces.
6. You drag a wall out 2 m. Its bevel, its baseboard and the baseboards of the two walls it touches rebuild; nothing
   else does. Wireframe and selection stay on the blockout, so editing reads cleanly.
7. You click a baluster: the railing box is selected. The inspector's Dressing section lists the rules that take it, with
   their settings; set this one's baluster spacing to 10 cm (stored on the mesh, not in the rules). **Use for all** puts
   that value into the rule for every railing.
8. One corner looks wrong. Switch the rule off for that mesh in the inspector, tag the face `skip:baseboard`, or Bake
   that wall (Edit > Bake dressing) and fix it by hand. A face that should be dressed and isn't? Select it: the inspector
   says why each rule skips it ("needs the tag wall (it has walls)").
9. **Save as style...** moves the level's rules into `styles/hangar.dressing.json` and uses it. The next level picks the
   style under **Styles**, and whatever it changes applies to that level only.
10. Ctrl+E exports the dressed level. Collision still comes from the blockout (a solid box for the railing, a ramp for
    the stairs), which is usually what you want.

## In the editor

### The Dressing tab (Ctrl+Shift+D)

A tab of the dock on the left, stacked top to bottom so it reads in a tall, narrow panel:

- **Top**: *Dressed | Blockout* (what the viewport shows, as `` ` ``), and the ⋯ menu: *Tag colours*, *Open JSON*,
  *New dressing rules file*, *Open dressing sample*, *Bake dressing*, *Save as style...*.
- **List**: every rule with a switch, its name over its kind, and how many meshes it made (`error`, `off` or
  `no matches` otherwise). Rules are grouped under *From <style>* and *This level*; "changed here" marks a style's rule
  this level changes. Hover a rule to tint what it takes. A filter appears once there are more than six rules. Under the
  list, **Shared** (the shared values) and **+ Add rule** (the recipes, below). Drag the bar under them to give the list
  or the rule more room.
- **Settings** of the selected rule, under the list, in words and units: lengths in centimetres, angles in degrees,
  density per m². The header says what the kind does and which file changes save to; problems with the rule come first,
  with their fixes. Rename it (its per-mesh overrides and `skip:` tags follow), write a note, and set every parameter of
  its kind. At the dock's usual width each label sits above its field; widen the dock and they move beside it, with the
  profile preview beside the profile fields rather than under them. Fields side by side are labelled inside (`depth`, `height`, `out`, `up`, `min`, `max`, `W` `H` `D`). Every row keeps a column at
  the right for × (put the style's value back) or $ (the field uses a shared value: click to edit it).
  - *Faces*: tags as chips (+ offers the level's tags or a new one), which way they face, and under *More filters* tags
    to leave out and material globs. *Meshes* (proxies): mesh tags.
  - *Where* (trim, props along edges): *Along the floor*, *Along the ceiling*, *Outside corners*, *Inside corners*,
    *Outline*, *Open edges*, or *Custom* (touching faces you pick by tag, material or facing).
  - *Profile*: *Flat strip*, *Wedge*, *Quarter round*, *Cove*, *Round* (with segment or side counts) or *Custom points*
    (a row per corner, out and up in cm; picking it starts from the current outline), with a preview of the
    cross-section against the face and the surface it runs along.
  - *Prop*: a box (W H D) or cylinder (Ø H, sides) sized in cm, or a model file (Browse..., with a warning when the file
    is missing). Scatter takes a list, each with a weight (×2 is picked twice as often as ×1).
  - *Material*: type one, or pick from the library or the active material. Empty means the default (the face's own).
  Edits keep shared values where they can: changing one side of a pair, or one corner, leaves a `"$name"` on the other.
- **Actions**: *Dress selection* (as Dress with, for this rule), *Select matches*, *Duplicate*, *Delete*.
- **Styles** (top): the rules files this level builds on, each a button that opens its file and a separate remove
  button. **+** picks one from the level's folder, a `styles` folder beside it or above it, or anywhere (Browse). A rule
  from a style shows *Changes to: This level | Shared style*: the shared choice sends changes to the style file (every
  level using it changes). A value this level changes from its style shows orange with × to put the style's back; with
  *Shared style* it is locked, since the level's value would still win.
- **Shared values**: the variables rules use by name (`"$trim"`), with which rules use each; change one and every rule
  using it follows. Lengths show and are typed in cm ("12 cm"; "0.12 m" works too).
- **Save as style...** (⋯ menu): moves the level's own rules and shared values into a new style file and imports it.
- **Footer**: counts rules and problems (click to jump to the first) and says "Saved <file>" after each change.
- **Open JSON** (⋯ menu): the file in your text editor.

Every change is one undo step that rewrites the rules file; Ctrl+Z puts the old file back. Drags commit on release, so
dragging a value redresses once. With no rules yet the tab lists the recipes with what each selects; for an unsaved
level it offers **Save level...**.

### Recipes

| Recipe | Kind | Selects | What |
|---|---|---|---|
| Baseboard | sweep | upright `wall` faces | 1.5 × 12 cm strip where walls stand on floors |
| Crown moulding | sweep | upright `wall` faces | 8 cm cove where walls meet ceilings |
| Frame | sweep | `frame` faces | trim round their outline (doors, windows, panels) |
| Corner pipe | sweep | `pipe` faces | a 10 cm pipe in their inside corners |
| Corner bevel | bevel | `wall` faces | 2 cm bevel on outside corners |
| Wall lights | repeat | upright `wall` faces | a light every 3 m at head height |
| Floor clutter | scatter | up-facing `floor` faces | boxes, 0.3 per m² |
| Railing | railing | meshes tagged `railing` | a railing filling each box |
| Stairs | stairs | meshes tagged `ramp` | stairs up each ramp's slope |

Recipes are a starting point: the rule they add is an ordinary rule you can change. They live in
`src/Chip.Core/Dressing/RuleAuthoring.cs` and each loads clean (`DressingEditingTests`).

### Dress with (Edit > Dress with, Ctrl+Shift+T)

A popup at the mouse lists *Rules in this level* (with a box showing whether the selection is all, partly or not
dressed with each) and then *Add from a recipe* (the recipes the level doesn't have yet). Picking a rule dresses the
selection with it: tags it, and clears any `skip:` tag or per-mesh switch that kept that rule off. Picking a rule the
whole selection already has takes it out of **that rule only**: faces get `skip:<rule>`, meshes switch the rule off, and
the tag stays, so Wall lights keep a wall you took out of Baseboard. Picking a recipe adds its rule first. One undo step
each. A tooltip names the other rules a tag also feeds; rows nothing selected can take are dimmed.

What gets tagged: in face mode, the selected faces. In object mode, the faces of each selected mesh the rule could use if
they carried its tag: for an "upright `wall` faces" rule, a box's four sides but not its top and bottom. Proxies tag the
meshes themselves. A rule that picks faces only by facing or material has no tag to add and isn't listed.

### Elsewhere

- **Inspector, Dressing section** (the last section, after Tags): which rules touch the selection, **Dress with...** and
  **Rules...** (the Dressing tab), and **Bake dressing** when the selection has dressing to bake. A saved level without
  rules shows a small version (Dress with, Rules) while something is selected.
  With one mesh selected: each rule that takes it, with a tick to turn it off for this mesh, › to open the rule, and its
  simple parameters as per-mesh overrides (orange; a *Differs* row with **Use for all** and **Reset**), and *Why
  don't N rules take this mesh?* (or face, with one face selected): a row per rule that skips it, with why; click one to
  open the rule.
- **View > Tag colours**: every tagged face tinted by its first tag (mesh tags striped), so untagged surfaces stand out.
  The Dressing tab's tag chips use the same colours.
- **Tags** (Ctrl+T, inspector Tags section) still work as before: Dress with is a shortcut for the common case.
- `` ` `` toggles dressed / blockout (View > Dressed view, the header toggle, the Dressing tab's switch). Export always ships
  the dressed level.
- *New dressing rules file* (the tab's ⋯ menu, or the command palette) writes a starter (from the recipes) beside a saved
  level; the first rule added from the tab or Dress with does the same. *Open dressing sample* opens a room using every
  kind.

## Rules files

The Dressing tab writes a canonical layout: top-level keys in a fixed order, each rule's keys as kind, enabled, note, then the
kind's parameters in table order, one per line, with short values on one line. Comments are not kept (use `note`). Hand-
and agent-written files may be laid out any way and may contain comments and trailing commas; the first edit from the
window rewrites them canonically.

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
    "wall-bevel": {
      "kind": "bevel",
      "faces": { "tags": ["wall"] },
      "edges": "convex",
      "minDeg": 60,
      "width": 0.02,
      "segments": 2
    },
    "baseboard": {
      "kind": "sweep",
      "faces": { "tags": ["wall"], "facing": "upright" },
      "profile": { "rect": [0.015, "$baseHeight"] },
      "material": "$trim"
    },
    "crown": {
      "kind": "sweep",
      "faces": { "tags": ["wall"], "facing": "upright" },
      "onto": { "facing": "down" },
      "profile": { "cove": 0.08 },
      "material": "$trim"
    },
    "railing": {
      "kind": "railing",
      "meshes": { "tags": ["railing"] },
      "balusterEvery": 0.12,
      "postEvery": 1.5,
      "rail": { "round": 0.05 },
      "material": "$trim"
    },
    "stairs": {
      "kind": "stairs",
      "meshes": { "tags": ["ramp"] },
      "stepHeight": 0.18
    },
    "debris": {
      "kind": "scatter",
      "faces": { "facing": "up", "materials": ["concrete*"], "notTags": ["clean"] },
      "props": ["props/rubble_a.glb", "props/rubble_b.glb"],
      "perM2": 0.3,
      "minGap": 0.4,
      "scale": [0.8, 1.2],
      "seed": 7
    }
  }
}
```

The level's own file, `hangar.dressing.json` (beside `hangar.chip`; a `.chip` can name another with `"dressing"`):

```json
{
  "chipDressing": 1,
  "imports": ["../styles/industrial.dressing.json"],
  "vars": { "baseHeight": 0.2 },
  "rules": {
    "crown": { "enabled": false },
    "debris": { "perM2": 0.8 },
    "pipes": {
      "kind": "sweep",
      "faces": { "tags": ["ceiling"] },
      "edges": "concave",
      "profile": { "round": 0.1 },
      "offset": [0, -0.15],
      "material": "pipe_red"
    }
  }
}
```

An entry with `kind` defines a rule; one without patches an imported rule (patching an id nothing defines is an error).
Later imports win; the importing file wins over all. Import cycles are errors. The Dressing tab writes patches holding only
the keys that differ from the style, and removes a key when it is set back to the style's value. A patch merges into the
style's objects, so it can add or change a selector key but not remove one; the tab says so and offers editing the
style or duplicating the rule into the level.

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

### Vocabulary

The editor's names are in brackets.

| Kind | Stage | Targets | Key parameters |
|---|---|---|---|
| `bevel` (Bevel corners) | shape | edges between two matching faces | `faces`, `edges` (convex/concave), `minDeg`, `width`, `segments` |
| `sweep` (Trim) | dress | edges of matching faces, chained across meshes | `faces`, `edges`, `onto`, `minDeg`, `profile`, `offset`, `material` |
| `railing` (Railing from box) | shape | meshes by mesh tag | `meshes`, `balusterEvery`, `baluster`, `postEvery`, `post`, `rail` (profile), `bottomRail`, `material` |
| `stairs` (Stairs from ramp) | shape | meshes by mesh tag | `meshes`, `stepHeight` (rounded to fit), `rise` (`auto` follows the ramp's slope, or `+x` `-x` `+z` `-z`), `material` |
| `repeat` (Props along edges) | dress | the edges a sweep would follow | `faces`, `edges`, `onto`, `minDeg`, `prop`, `every` (rounded so a whole number fits, centred), `offset` [out, along], `material` |
| `scatter` (Scatter props) | dress | matching faces | `faces`, `props`, `weights`, `perM2`, `minGap`, `scale` [min, max], `yawDeg` [min, max], `align` (`up`/`normal`), `offset`, `seed`, `material` |

**Profiles** are cross-sections in metres, x out of the face, y along the surface the edge sits on, (0, 0) in the corner:
`{ "rect": [depth, height] }`, `{ "wedge": [depth, height] }` (a triangle in the corner), `{ "round": diameter, "sides": 8 }`,
`{ "quarter": radius }` (quarter round), `{ "cove": radius }` (a square with a quarter circle cut out; `sides` sets the
curve's segments, default 6), or `{ "points": [[x, y], ...] }`.

**Proxies** take whole meshes by mesh tag (`"meshes": { "tags": ["railing"] }`) and replace them with what they build
from the mesh's bounding box: the run is the longer horizontal side, up is Y. A railing fills the box's length and height
(posts at both ends and every `postEvery`, handrail centred on the top, bottom rail, balusters between). Stairs fill the
box, climbing toward the end where the mesh is highest, so a ramp turns into steps up the same slope. Export keeps the box
or ramp as collision.

**Props** are `{ "box": [w, h, d] }`, `{ "cylinder": [diameter, height], "sides": 12 }`, or a `.glb` / `.gltf` path relative
to the rules file that names it. A file's own textures (embedded, or images beside a `.gltf`) are used: its faces get a
material named `file/material` that the editor adds to the library. Materials without an image keep their glTF names, so
name them after your library textures. Explicit UVs are kept.
The window rewrites prop paths when it copies a rule to another file (Duplicate, Save as style). A prop stands on its
origin: +Y up (along the surface it sits on), +X along the edge, +Z out from the face. Edited prop files reload; a missing
one warns once (W610).

**Scatter** seeds each face from its own corners (to the millimetre), the rule id and `seed`, so the same level always
scatters the same way and editing one face never moves props on another. `minGap` applies within a face.

The full, current parameter list with descriptions is the generated schema: `Chip --rules-schema` (Chip also writes
`dressing-1.schema.json` beside rules files it creates or opens). Sweeps are textured world-aligned with the source face's
scale; a texture running along the trim (`uv: along`) is later.

Face selectors: `tags`, `notTags`, `materials` (globs), `facing` (`up`, `down`, `upright`). Mesh selectors: `tags`. Edge
kinds: `contact` (with `onto`: an edge of these faces lying on a matching face of **any** mesh, 1 mm tolerance; walls and
floors are separate meshes, so this is spatial), `boundary`, `convex`, `concave`, `border` (the outline of the matched
faces). All keys in a selector must hold. The window's *Where* presets are `contact` onto `facing: up` (the default) or
`down`, and the other edge kinds.

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
matches nothing is always a warning: it is the commonest mistake. The same diagnostics show under each rule in the Dressing tab
and in the inspector.

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
    public MeshOrigin Origin(PolyMesh mesh);       // (SourceId, Rule?, Role: Passthrough | Modified | Proxy | Added)
    public IReadOnlyList<(string Rule, DressRole Role)> RulesFor(int meshId);
    public IReadOnlyList<(int Mesh, int Face)> Targets(string rule);           // what a rule takes (face -1: the mesh)
    public IReadOnlyList<(Rule Rule, string? Reason)> Explain(PolyMesh mesh, int face); // why each rule skips it
    public IReadOnlyList<Diagnostic> Diagnostics { get; }   // rule-file problems, then dressing problems
    public IReadOnlyList<RuleStats> Stats { get; }
    public RuleSet Rules { get; }
    public string ContentHash { get; }             // stable across runs and machines: did an edit change the output?
    public Document ForExport();                   // dressed meshes + collision-only blockout stand-ins
}

/// Editing (the Dressing tab). A workspace reads the level's file and its styles fresh; edits mark documents
/// changed; Changes() gives every file's new text, which the editor writes in one Session step.
public sealed class RulesWorkspace(IDressingFiles files, string mainPath, string? levelName = null)
{
    public IReadOnlyList<RuleEntry> Rules { get; } // raw definitions, styles merged, the level's patch laid over
    public bool Set(string id, string key, JsonNode? value, bool style = false);  // style rules: a minimal level patch
    public bool SetEnabled(string id, bool on, bool style = false);
    public void Reset(string id, string? key = null);
    public void Add(string id, JsonObject definition);
    public bool Delete(string id, bool style = false);
    public bool Rename(string id, string newId, bool style = false);
    public bool Duplicate(string id, string newId);
    public void SetVar(string name, JsonNode? value);
    public void SetImports(IEnumerable<string> paths);
    public bool SaveAsStyle(string stylePath, string name);
    public IReadOnlyList<(string Path, string Text)> Changes();
}
public sealed class RulesDocument { string Write(); ... }   // one file as JSON nodes; canonical writer
public static class Recipes, EdgePlaces, ParamJson;         // recipes, Where presets, values as JSON (RuleAuthoring.cs)
public static class DressingOps;  // rename/delete carry overrides and skip: tags; Dress with tags the selection

public interface IDressingFiles { string? ReadText(string path); long Stamp(string path); }   // DiskFiles, MemoryFiles
public static class DressingRules { string? PathFor(Document, string? levelPath, IDressingFiles); string DefaultPathFor(string levelPath); }
public static class RulesCheck { int Run(Document, string? levelPath, IDressingFiles, TextWriter, bool json); }
public static class DressingSchema { string Generate(); string Starter(string? levelName); }
```

`Session.WriteFile(path, text)` writes a file inside an undo step; undo, redo and a cancelled step put the file back
(`Session.FilesChanged` tells the dressing feature to reload at once). The editor's `DressingFeature.EditRules(label,
change, meshIds, docEdit)` wraps it: a fresh workspace, the change, every changed file written, and an optional level edit
(renames, Dress with) in the same step.

Invariants: blockout meshes pass through as the same objects with the session's revisions. Generated meshes have negative
ids, stable per (rule, source) for the dresser's life, and revisions from a counter shared by every dresser, so GPU caches
keyed on id and revision are always right. Generated meshes are `Locked` and `Collision = None`. Outputs come in blockout
order whatever was rebuilt, so exports are stable.

### Behind the seam (`src/Chip.Core/Dressing/`)

- **`JNode`**: a JSON reader that keeps line and column for every value (comments and trailing commas allowed).
- **`RuleSet`** (`Rules.cs`): imports (relative paths, cycles reported), vars, per-level patches by rule id, rule ids
  checked like tags, kinds looked up with did-you-mean, every parameter typed through **`ParamSpec`** tables
  (`Params.cs`), which also generate the schema and drive the Dressing tab (label, unit, choice labels). A rule with errors is
  left out; the rest run.
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

- `Face.Tags` and `PolyMesh.Tags`: an immutable, interned, sorted `TagSet` (`src/Chip.Core/Geometry/TagSet.cs`) with value
  equality; `default` is empty. `PolyMesh.Dressing` (`MeshDressing`) holds per-rule overrides (JSON text per rule) and
  switched-off rules (`*` for all); it is immutable with value equality, saved as the mesh's `dressing` object, and in the
  change hash and undo comparison.
- `Face.Derive(int[] verts)` copies material, mapping and tags. Every op that builds a face from a source face uses it, so
  the next surface property costs nothing. It doesn't copy explicit UVs or dominance: a bevel strip or extrude side isn't
  the dominant face. Pieces of the same face (clip, knife, connect) set those themselves.
- `MeshHash`, undo's mesh comparison, `Clone` and the serializer include tags (optional `tags` arrays; no version bump).
- `TagOps` (`src/Chip.Core/Scene/TagOps.cs`): usage counts, coverage of the selection, set/remove, select by tag. Face mode
  tags faces, object mode tags meshes.
- `TagTests.Every_op_keeps_tags_on_every_face` runs every mesh op on a tagged cube.
- Editor (`src/Chip.Editor/Features/Tagging/`): Ctrl+T popup at the mouse (type to filter or add, Enter adds or removes,
  Ctrl+Enter selects everything with the tag, suggestions from the level so typos show as new tags); inspector Tags section
  with chips (`wall 2/5` when partial; click adds to the rest, × removes, Ctrl+click selects all).

### Editor (`src/Chip.Editor/Features/Dressing/`)

- `DressingFeature` owns a `Dresser` per open level and sets `EditorContext.Dressed`: it re-dresses when the document
  changes and polls the rules files four times a second (at once after Chip writes them). Reloads say so in the status
  bar; rules with errors show there in red. It also supplies `EditorContext.ViewportTints`: proxy stripes in the blockout
  view, the hovered rule's targets, and tag colours; the renderer draws them as overlay triangles (`FaceTint`).
- `DressingPanel` (the Dressing tab: list, styles, shared values, save as style) and `RuleFields` (one rule's parameters: units,
  Where, tag chips, profile picker and preview, prop editors). Each edit goes through `EditRules`.
- `DressWithPopup` (Ctrl+Shift+T): rules and recipes for the selection; `Apply` and `ApplyRecipe` are what the tab's
  *Dress selection* and the tests use.
- `DressingSection` and `DressingOverrides`: the inspector block (view switch, Rules..., selection, per-mesh overrides with
  Use for all, why rules skip the selection, problems), and Edit > Bake dressing (`DressingBake`).
- Renderer: `BeginFrame(..., scene, tints)` draws solid faces from the dressed level when `ShowDressed`; wires, selection
  and overlays stay on the blockout. Picking stays on the blockout: clicking a railing hits its box, which is what you edit.
- Export (GLB and OBJ) writes `EditorContext.ExportDocument()`: the dressed level when there are rules, even with the
  blockout view showing. Collision stand-ins reuse `CollisionMode.Only` and the collision bake.
- `Chip --rules-check level.chip [--json]` and `Chip --rules-schema [out.json]` run without a window. Captures:
  `--shot - out.png dressing` (and `dressing-blockout`, `dressing-railing`, `dressing-rules`, `dressing-dresswith`,
  `dressing-tags`); `CHIP_SHOT_CAMERA` frames close-ups, `CHIP_SHOT_RULE=<id>` (or `vars`) picks what the Dressing tab
  shows, and `CHIP_SHOT_OVERRIDE=1` gives the `dressing-railing` railing a per-mesh override.

### Tests (through the interface)

`DressingTests` (core): mitred room of separate overlapping walls, doorway with wrapped jambs, round pipe on concave
corners, bevels and their export stand-ins, width and conflict warnings, imports/vars/overrides, every diagnostic's fix,
zero-match hints, live reload, `--rules-check` exit codes, schema and starter, a rebuild count proving one edit reruns four
jobs, and a randomised test (four seeds, 60 edits each: moves, retags, hide, delete, add) that the incremental result always
equals a fresh dress, diagnostics included. `DressingPhase2Tests`: proxies, props, overrides, Bake. `DressingEditingTests`:
the canonical writer round-trips rules unchanged, level patches over styles (minimal, reset, can't remove a selector key),
style mode, rename / delete / duplicate / save as style, recipes load clean, new profile shapes, Where presets, defaults
as JSON, undoable file writes, rename carrying overrides and skip tags, Dress with, Explain and Targets.
`DressingFeatureTests` (editor): the sample dresses and exports, the view toggle, blockout and rules-file edits redress,
rule edits write the file and undo, rename in one step with overrides, Dress with and recipes, the first rule creates the
file, taking a face out of one rule leaves the rules sharing its tag, viewport tints.

## Later

Export instancing, height bands (chair rails), more modifiers, a texture running along trims (`uv: along`), prop
thumbnails in the Dressing tab.

## Known limits

- Sweeps follow the sharp blockout corner, so a baseboard overlaps a 2 cm bevel. Invisible at trim thickness; a
  `clipToShape` option can come later.
- Contact ends join when exactly two meet within 1 mm. Overlapping walls are handled (hidden stretches are dropped, so ends
  meet at the corner); three or more ends at one point (a T-junction) stay open with caps. Ends within 5 cm that don't meet
  are reported (W520) with their position.
- Only closed meshes hide trim stretches; an open mesh (a single plane) never does.
- Convex sweeps sit in the outside quadrant of the corner (a bead), not wrapped round it.
- Bake is a snapshot: the baked meshes stop following the blockout (undo brings the rules back).
- Scatter doesn't know what stands on a face: props can land under walls or platforms sitting on a big floor face. Tag the
  open floor, or split the face. Props are merged into one mesh per source (no instancing in export yet).
- Two railing boxes meeting at a corner each get an end post there.
- Per-mesh overrides edit simple values (numbers, choices, on/off, text, pairs); selectors, profiles and props are edited
  in the rules window, for the whole rule.
- Only one Shape rule per mesh, so a mesh can't be both bevelled and a proxy.
- The window rewrites the rules file canonically: comments in a hand-written file are lost on its first edit there.
- Renaming or deleting a style's rule (Shared style) updates this level's overrides and `skip:` tags, not other levels'.
- Shared values are edited as text (lengths in cm); a value used both as a length and as something else shows plainly.
