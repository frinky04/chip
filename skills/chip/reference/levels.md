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
- `export` (optional) holds the export settings: `bakeCollision`, `collisionHulls`, `tessellate`, `smoothing`,
  `embedTextures`, `materialRemap` and the rest.

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
| `--export <level.chip> [out.glb]` | Exports with the project's classes and conventions; prints warnings for values left out | 0 written, 2 no output path, 4 unreadable |
| `--shot <level.chip> <out.png> [objects\|faces\|empty]` | Renders the editor with the level open, so you can look at it ("saved" in the capture is the level's state; the file is only read) | 0 written |
| `--rules-check <level.chip> [--json]` | Dresses the level headlessly and reports rule problems | 0 clean, 1 warnings, 2 errors, 3 no rules file |
| `--rules-schema [out.json]` | The JSON Schema for dressing rules files | 0 |

`--export` skips dressing; the editor's Export (`Ctrl+E`) applies it. A level whose look depends on its dressing has
to be exported from the editor.

## Dressing

Dressing rules (`<level>.dressing.json` beside the level) turn tagged blockout faces into trims, railings, stairs and
scattered props. Validate the file against `--rules-schema` while writing it, and finish when `--rules-check` exits 0.
Chip's `docs/dressing.md` covers every rule kind.
