# Entity classes

Status: phases 1 (Godot scenes as classes, typed properties, instancing on import) and 2 (previews, references between
entities) built. Mesh entities (doors and triggers made from a mesh) are phase 3.

**Your Godot scenes are the entity definitions.** You never write a definition file. The Chip import addon reads the
scenes in your entity folders and writes a manifest; Chip reads the manifest and reloads it when it changes; on import,
the addon turns every entity back into an instance of its scene with the level's values applied.

## Why not an FGD

Hammer, TrenchBroom and FuncGodot describe entities in a second file (an FGD, or FuncGodot's FGD resources) beside the
scene or script that already defines them, sync it to the editor by hand, store every value as a string, and link
entities by name (`target` / `targetname`). Each of those is a place for the two sides to drift. Here:

| | FGD workflow | Chip |
|---|---|---|
| Definition | Written again, beside the scene | The scene: its root script's `@export` vars |
| Sync | Export FGD + game config, often restart the editor | The addon rewrites the manifest when you save; Chip reloads it |
| Values | Strings, converted at build time | Typed JSON, checked as you edit |
| Defaults | Usually written into every entity | Only changed values are stored; a new default in Godot reaches every level |
| References | Names that break on rename | Chip ids, resolved to the node on import |
| Point vs solid | Two class kinds | One kind (mesh entities in phase 3) |
| Units | Map units and a scale factor | Metres on both sides |

## Workflow

1. Copy `godot/addons/chip_import` into the project and enable **Chip import** (Project Settings > Plugins).
2. Make a scene per entity class under `res://entities/` (Project Settings > `chip/entities/folders` lists the folders).
   The class name is the path without extension: `res://entities/enemies/grunt.tscn` is `enemies/grunt`. Files and
   folders starting with `_` or `.` are skipped, so helper scenes can live beside classes.
3. Save the level inside the project folder, or export into it. Chip finds `project.godot` by walking up from the export
   path, then from the level, and reads `.chip/entities.json`. The status bar says how many classes it loaded.
4. Place entities with the entity tool (`Shift+E`). Other entities are surfaces too: click a crate to put a pickup on
   it (on its preview model, else its box; snapping stays on the crate). Set the tool's **On** to *Meshes* to click
   through entities to the level. The inspector shows the class's properties; long class docs show their first line,
   with the rest on hover.
5. Export into the project. Godot imports the `.glb`; each entity becomes a linked instance of its scene.

Without a Godot project (or before the plugin has run) Chip offers the addon's built-in classes: `info_player_start`,
`info_target`, `light_point`, `light_spot`.

## Defining a class

```gdscript
# res://entities/door.tscn, root script:
@tool
extends Node3D
## A door that swings or slides open.          <- the class doc, shown under the class in Chip

## How the door opens.                         <- the tooltip on the field
@export_enum("swing", "slide") var mode := "swing"
@export_range(0, 180, 1, "or_greater", "suffix:°") var open_angle := 90.0
@export var locked := false
@export var tint := Color(0.8, 0.5, 0.2)
@export var partner: Node3D                    <- pick another entity in Chip
@export var skin: Material                     <- a res:// path

@export_group("Sound")                         <- a sub-heading in Chip
@export_range(0, 1) var volume := 0.8
```

| Godot export | In Chip | Stored as |
|---|---|---|
| `bool` | checkbox | `true` |
| `int`, `float` (with `@export_range`: limits, step, `or_greater`, `suffix`) | drag field | `3`, `0.8` |
| `String`, `StringName` | text | `"abc"` |
| `@export_enum` on `int` or `String` | dropdown | the option's value (`5` or `"slide"`) |
| `Color` | colour picker | `[r, g, b, a]` in 0..1 |
| `Vector2(i)`, `Vector3(i)` | X/Y/Z fields | `[x, y, z]` |
| `Node` types, `NodePath` | list of the level's entities | `{ "entity": 7 }` |
| `Resource` types | text field | `"res://materials/brick.tres"` |
| Typed arrays of the above (`Array[StringName]`, `Array[Vector2i]`, `Array[Node3D]`, `Array[Material]`, an `Array` of an enum) and packed arrays (`PackedStringArray`, `PackedFloat32Array`, `PackedVector3Array`…) | a field per item, with Add item and a remove button each | `["oak", "heavy"]`, `[{ "entity": 7 }, { "entity": 9 }]` |
| anything else (untyped arrays, arrays of arrays, dictionaries) | "Set in Godot" | not editable |

A list is one value: setting it replaces the whole list, and × puts back the scene's list. Add item copies the last item
(a run of similar cells is quick), except in a list of references, which adds an empty one to pick. A list reads
tolerantly, like the other types: a single value is a list of one, and text in a list of text or choices splits at commas,
so a class that turns `tags: String` into `tags: Array[StringName]` keeps the levels' `"oak, heavy"` as `["oak", "heavy"]`.

Defaults come from the instantiated scene, so a value set on the scene's root overrides the script's. Two pieces of root
metadata cover what a script can't say:

- `chip_expose` (`PackedStringArray`): native properties to edit as well (`["light_color", "omni_range"]`). The built-in
  light scenes are nothing but this, with no script.
- `chip_bounds` (`AABB`): the box Chip draws and picks when the scene has no meshes (the player start's 0.6 × 1.8 m).

A scene with meshes gets a preview: the addon exports its visuals to `.chip/previews/<class>.glb` and Chip draws that, as
clay, instead of a box. Bounds come from the meshes.

## The manifest

`res://.chip/entities.json`, written by `chip_manifest.gd` (with a `.gdignore`, so Godot doesn't import the folder). It's
generated: never edit it. It is rewritten half a second after scenes or scripts are saved or files change, and only when
its content changes. Headless (CI, or a project nobody has opened):
`godot --headless --path <project> --script res://addons/chip_import/chip_manifest_cli.gd`.

```json
{
  "version": 1,
  "classes": {
    "door": {
      "scene": "res://entities/door.tscn", "uid": "uid://dlm1osw5eeb10", "label": "Door", "base": "Node3D",
      "doc": "A door that swings or slides open.",
      "props": [
        { "name": "mode", "type": "enum", "options": [{ "label": "swing", "value": "swing" }, { "label": "slide", "value": "slide" }],
          "default": "swing", "doc": "How the door opens." },
        { "name": "open_angle", "type": "float", "range": [0, 180, 1], "or_greater": true, "suffix": "°", "default": 90 },
        { "name": "partner", "type": "node", "class": "Node3D", "default": null },
        { "name": "volume", "type": "float", "range": [0, 1, 0], "default": 0.8, "group": "Sound" },
        { "name": "tags", "type": "array", "of": "string", "default": ["wood"] },
        { "name": "cells", "type": "array", "of": "vector2", "integer": true, "default": [] },
        { "name": "linked", "type": "array", "of": "node", "class": "Node3D", "default": [] }
      ],
      "bounds": [[-0.5, 0, -0.05], [0.5, 2, 0.05]],
      "preview": "previews/door.glb"
    }
  }
}
```

A list is `"type": "array"` with its item type in `"of"` (any type above but `array` and `unsupported`). The item's hints
(`options`, `class`, `range`, `integer`, `suffix`) sit on the list itself. The addon reads them from the array's
`type/hint:hint_string` export hint, so an `Array[Node3D]` lists Node3D entities and an `Array[Vector2i]` has whole
numbers. A node list's default is always empty: the nodes in a scene's own list mean nothing in a level.

The format doesn't depend on Godot: other engines write it themselves, from a `chip.project.json` that says where it is
([engines.md](engines.md)). They can also use the neutral type names `entity` (for `node`) and `asset` (for
`resource`). [`skills/chip/reference/manifest.md`](../skills/chip/reference/manifest.md) lists every field.

## Levels and export

A `.chip` (v3) entity stores only the values it changes, typed:

```json
{ "id": 12, "className": "door", "position": [4, 0, 2], "properties": { "open_angle": 110, "partner": { "entity": 7 } } }
```

Setting a value back to the default removes it (× in the inspector does the same). Changing class keeps every value, so
changing back loses nothing. v2 levels load as they are: their string values read through the class's types, and the
built-in lights' old keys (`color`, `energy`, `range`, `angle`, `shadows` and the old addon's aliases) move to Godot's
names (`light_color`, `light_energy`, `omni_range` / `spot_range`, `spot_angle`, `shadow_enabled`).

Each entity exports as an empty glTF node with
`extras = { chip_class, chip_id, chip_uid?, chip_scene?, chip_name?, properties }`. Values are typed by the class on the
way out (a v2 `"10"` goes out as `10`). The addon finds the scene by `chip_uid` (survives moving or renaming the scene),
then `chip_scene`; instantiates it as a linked instance (editing the scene updates every level); applies the values
(a list fills the property's own typed or packed array, so `Array[StringName]` stays one);
then points node properties, and node lists, at the instances of the entities they reference. Every entity keeps `chip_class`,
`chip_properties` and `chip_id` as metadata and is in the `chip_entities` group. Groups saved on the scene (the player
start's `chip_spawn`) come with it.

Duplicating entities that reference each other keeps them a pair (the copies reference the copies). Importing a Chip
`.glb` back keeps references between the imported entities.

## When something doesn't match

Nothing here stops a level from loading or exporting.

| Situation | Chip | Godot import |
|---|---|---|
| Class not in the project (scene deleted, level from elsewhere) | Red note in the inspector; values kept as free key-values | `Marker3D` carrying the values as metadata, with a warning |
| A value the class no longer declares | Listed under "Unused values", removable | Warning; the value stays in `chip_properties` |
| A value its type can't read | Red label, the default is used, export leaves it out with a status-bar warning | Scene's value |
| Reference to an entity that isn't exported | Export leaves it out with a warning (from a list: just that item) | Warning; the item is dropped from a list |
| No manifest yet | Built-in classes; the entity tool says to enable the plugin | — |
| Manifest can't be read (mid-write, hand-edited) | Keeps the last good classes and says why | — |

## Code

- `src/Chip.Core/Scene/EntityCatalog.cs`: `EntityClass`, `PropSpec`, manifest parsing, the built-in catalog (embedded
  `BuiltinEntities.json`, generated from the addon).
- `src/Chip.Core/Scene/EntityValues.cs`: typed values, tolerant readers (v2 strings, colour forms), `Coerce` against a spec.
- `src/Chip.Core/Scene/Entities.cs`: `EntityClasses` (the classes in use) and `EntityClassTracker` (finds the project,
  reloads the manifest).
- `src/Chip.Core/Scene/GameProject.cs`: finding the project (`chip.project.json` or `project.godot`) and its engine
  conventions. `EntityManifestCheck.cs`: `--entities-check`.
- `src/Chip.Editor/Features/Scene/EntitySection.cs` (inspector), `ClassPicker.cs`, `ClassTracker.cs` (polls twice a second).
- `godot/addons/chip_import/`: `chip_manifest.gd` (+ `_cli.gd`), `chip_processor.gd`, `entities/` (built-in classes).
- `godot/` is the addon's dev project: sample classes in `godot/entities/`, a demo level in `godot/levels/`.

## Tests

- `tests/Chip.Core.Tests/EntityCatalogTests.cs`: manifest parsing, value coercion, project tracking, v2 migration,
  references through import and duplicate, export.
- `scripts/godot-test.ps1 [-Godot <console exe>]`: runs `godot/tests/test_chip_import.gd` headless: the manifest for the
  sample classes, processing (types, references, missing scenes, linked instances that survive save and reload), and a
  real `Chip --export` of `tests/fixtures/entities.chip` imported end to end. Passes on Godot 4.5, 4.6 and 4.7.
- After changing the built-in scenes: `scripts/godot-test.ps1 -UpdateBuiltins` regenerates `BuiltinEntities.json` (the
  test fails until you do, on the Godot version that wrote the file).

## Phases

1. Done. Scenes as classes, manifest, typed inspector, sparse typed storage, instancing on import, built-ins as scenes.
2. Done. Previews, node references, lists (typed and packed arrays).
3. Mesh entities: a scene whose root is a `CollisionObject3D` (`Area3D`, `AnimatableBody3D`) can be given to a mesh,
   whose geometry becomes its visual and collision shape. Needs a per-object origin (a door's hinge); triggers become an
   ordinary `trigger.tscn`.
4. Later: signal connections (Hammer's I/O, as Godot signals), the same manifest approach for materials.

## Known limits

- Untyped arrays, arrays of arrays, dictionaries and other types can't be edited in Chip (shown as "Set in Godot").
- List items can't be reordered in place yet (remove and add again).
- Resource fields are typed paths; there's no browser of project resources yet.
- A node property's class (`Node3D`) isn't checked against the referenced entity's scene root.
- `Chip --export` doesn't apply dressing rules (the editor's export does).
