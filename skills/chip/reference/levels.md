# Levels and the command line

## The `.chip` file

A level is JSON (camelCase), version 3. Chip writes it whole on every save, so close the level in the editor before
editing the file, or the next save overwrites your edit.

```json
{
  "version": 3, "exportScale": 1, "exportCollision": true, "exportPath": "demo.glb", "nextId": 13,
  "meshes": [ ... ],
  "entities": [
    { "id": 9, "className": "door", "name": "left_door", "position": [4.5, 0, 0], "rotation": [0, 90, 0],
      "hidden": false, "properties": { "partner": { "entity": 10 }, "keys": ["key_red"] } }
  ]
}
```

- `world` (optional) stores level values as `"world": { "properties": { "sky": "overcast", "brightness": 0.7 } }`. Only changed values are stored, using the manifest world class's types. Missing `world` means no values. This is an additive version 3 field. `--migrate` applies world property `was` renames; `--check-levels` checks types, unused properties and values without a world class.
- `exportPath` is relative to the level. `--export` writes there when no output path is given.
- Ids are unique across meshes, entities and outliner groups (`groups`, optional). Give a new entity `nextId` and raise
  `nextId` past it; Chip repairs a `nextId` that is too low when it loads.
- An entity needs only `id` and `className`. `name` defaults to empty (exported as `<class>_<id>`), `position` and
  `rotation` to zero.
- `rotation` is degrees: pitch X, yaw Y, roll Z. Yaw 0 faces −Z.
- `properties` is sparse: write only the values that differ from the class default, typed as in the manifest's type
  table. A reference is `{ "entity": <id> }`.
- A mesh entity lists its meshes: `"meshes": [13]`. Its `position` is its origin (a hinge), not the meshes' position.
  A mesh belongs to one entity at most ([`surfaces.md`](surfaces.md)).
- `tags` (optional) is the entity's tags, `["corridor_lights"]`, and `outputs` (optional) its connections, in the
  shape [`logic.md`](logic.md) gives, leaving out defaults.
- `export` (optional) holds the export settings: `bakeCollision`, `collisionHulls`, `tessellate`, `smoothing`,
  `embedTextures`, `materialRemap` and the rest.
- A mesh or group may have its own `origin`: `{ "position": [x, y, z], "rotation": [x, y, z] }` (degrees, as an
  entity's; rotation left out when zero). It is the pivot and where export places the mesh's node. Vertices stay in
  world space.
- Linked copies: `definitions` (optional) lists what copies share, `{ "id", "name", "parts": [{ "key", "mesh" }],
  "prefab"? }`, each part a mesh in the prop's own space. A copy is a group with an `origin` and `"instance": <definition
  id>`; its meshes are ordinary meshes in world space with `"part": <key>`. Chip keeps copies the same, so a script
  editing one copy's meshes should edit the definition's parts and every copy alike, or leave linked props alone.

### Prefabs

A prefab is an ordinary level file named `name.prefab.chip` with `"prefab": { "guid": "…", "name": "crate" }`. Its
meshes are the prop's parts, around the file's origin (put the prop's base at 0, 0, 0). A level's definition that comes
from one has `"prefab": { "path": "props/crate.prefab.chip", "guid": "…" }`, the path relative to the project root (the
folder of `chip.project.json` or `project.godot`, else the level's folder). The level keeps the parts as well; when
the level opens, and on `--export`, Chip reads the file again, finding it by `guid` if it moved.

### Meshes

Geometry is the designer's work in the editor. Test levels only need boxes; this one is a 1 m cube at the origin:

```json
{ "id": 1, "name": "floor", "hidden": false, "collision": "default",
  "positions": [[0,0,0], [1,0,0], [0,1,0], [1,1,0], [0,0,1], [1,0,1], [0,1,1], [1,1,1]],
  "faces": [
    { "verts": [0, 4, 6, 2], "material": "dev_measurewall01a", "u": [0, 0, 1], "v": [0, -1, 0], "shift": [0, 0], "scale": [0.00390625, 0.00390625], "rotation": 0 },
    { "verts": [1, 3, 7, 5], "material": "dev_measurewall01a", "u": [0, 0, -1], "v": [0, -1, 0], "shift": [0, 0], "scale": [0.00390625, 0.00390625], "rotation": 0 },
    { "verts": [0, 1, 5, 4], "material": "dev_measurewall01a", "u": [-1, 0, 0], "v": [0, 0, 1], "shift": [0, 0], "scale": [0.00390625, 0.00390625], "rotation": 0 },
    { "verts": [2, 6, 7, 3], "material": "dev_measurewall01a", "u": [1, 0, 0], "v": [0, 0, 1], "shift": [0, 0], "scale": [0.00390625, 0.00390625], "rotation": 0 },
    { "verts": [0, 2, 3, 1], "material": "dev_measurewall01a", "u": [-1, 0, 0], "v": [0, -1, 0], "shift": [0, 0], "scale": [0.00390625, 0.00390625], "rotation": 0 },
    { "verts": [4, 5, 7, 6], "material": "dev_measurewall01a", "u": [1, 0, 0], "v": [0, -1, 0], "shift": [0, 0], "scale": [0.00390625, 0.00390625], "rotation": 0 }
  ] }
```

For another box, replace the 0s and 1s in `positions` with its min and max corners; the faces stay the same. `verts`
index `positions`, counter-clockwise seen from outside. `u`/`v` are the texture's world axes, and `scale` is metres per
texel (1/256 matches the default 256 px per metre). `collision` is `default`, `none`, `only` or `convexOnly`.

`material` is the file name, without extension, of an image in Chip's texture folders (Chip ships
`dev_measurewall01a`, `dev_measurewall01d` and `dev_measurecrate02`), or a surface: `trigger` for a trigger volume,
`clip` for player clip, `nodraw` for invisible collision, `skip` to leave the face out, or one of the game's own
([`surfaces.md`](surfaces.md)). Any other name exports under that name and draws magenta in Chip. That suits a game that
looks materials up by name.

## Commands

`Chip.exe` runs these without opening a window:

| Command | Does | Exit |
|---|---|---|
| `--entities-check <path> [--json]` (any path inside the project, or the manifest itself) | Reads the project file and the manifest, lints the manifest, lists the classes | 0 clean, 1 warnings, 2 errors, 3 not found |
| `--check-levels <game root \| level> [--json]` | Checks every `.chip` under the root (skipping `bin`, `obj`, `artifacts`, `dist` and dot folders) against the classes: unknown classes, dangling or wrongly typed references and connections, missing assets, undeclared flags, zero-delay cycles ([`logic.md`](logic.md), Checks) | 0 clean, 1 warnings, 2 errors, 3 no project or manifest |
| `--migrate <game root \| level>` | Moves values and connections stored under old names (`was`) to the current ones and saves each level it changed | 0 done, 2 unreadable, 3 no project or manifest |
| `--export <level.chip> [out.glb]` | Exports with the project's classes and conventions; prints warnings for values left out | 0 written, 2 no output path, 4 unreadable |
| `--shot <level.chip> <out.png> [objects\|faces\|entity\|empty]` | Renders the editor with the level open, so you can look at it ("saved" in the capture is the level's state; the file is only read). `entity` selects one entity to show its inspector: the first, or `CHIP_SHOT_ENTITY=<name or id>`; `CHIP_SHOT_FOLD=selection,entity` folds sections above to bring lower ones into view; `CHIP_SHOT_CONNECTION=<index>` opens that connection under Entity IO; `CHIP_SHOT_KEYS=Ctrl+Shift+I` opens the Wiring window | 0 written |
| `--rules-check <level.chip> [--json]` | Dresses the level headlessly and reports rule problems | 0 clean, 1 warnings, 2 errors, 3 no rules file |
| `--rules-schema [out.json]` | The JSON Schema for dressing rules files | 0 |

`--export` skips dressing; the editor's Export (`Ctrl+E`) applies it. A level whose look depends on its dressing has
to be exported from the editor. `--export`, `--check-levels` and the editor apply the `was` renames as they load a
level; only `--migrate` (and saving in the editor) writes them back.

## Dressing

Dressing rules (`<level>.dressing.json` beside the level) turn tagged blockout faces into trims, railings, stairs and
scattered props. Validate the file against `--rules-schema` while writing it, and finish when `--rules-check` exits 0.
Chip's `docs/dressing.md` covers every rule kind.
