# Entity manifest

The file that tells Chip which entity classes a game has. Chip reads it from the path in `chip.project.json`
(`"entities"`, default `.chip/entities.json`) and reloads it within half a second of a change.

## Project file

`chip.project.json` at the game's root marks the project. Chip finds it by walking up from the level and from the
level's export path. Every field is optional:

| Field | Default | Meaning |
|---|---|---|
| `version` | `1` | Format version. |
| `engine` | `"the engine"` | The name in Chip's messages ("Set in Toy"). |
| `entities` | `".chip/entities.json"` | The manifest, relative to the project file. |
| `collisionSuffixes` | `false` (`true` beside `project.godot`) | Also append Godot's `-col`/`-colonly`/`-convcolonly` to collider node names. |
| `assetPrefix` | `""` (`"res://"` for Godot) | What asset paths start with: the hint in asset fields and material remaps, and what Chip checks remap paths against. |
| `surfaces` | none | The game's own surfaces, such as a monster clip or a video screen ([`surfaces.md`](surfaces.md)). |

An unknown field makes the whole file unreadable, and Chip then falls back to its built-in classes. Comments and trailing commas are allowed.
A Godot project needs no project file: `project.godot` alone means Godot's conventions, and the Chip import addon
writes the manifest.

## Format

```json
{
  "version": 1,
  "classes": {
    "door": {
      "label": "Door",
      "doc": "A door that opens when the player comes near.\nShown in full on hover.",
      "props": [
        { "name": "mode", "type": "enum", "options": [{ "label": "Swing", "value": "swing" }, { "label": "Slide", "value": "slide" }], "default": "swing" },
        { "name": "open_angle", "type": "float", "range": [0, 180, 1], "suffix": "°", "default": 90 },
        { "name": "partner", "type": "entity", "class": "door", "doc": "Opens together with this door." },
        { "name": "keys", "type": "array", "of": "string", "group": "Lock", "default": [] }
      ],
      "bounds": [[-0.5, 0, -0.05], [0.5, 2, 0.05]]
    }
  }
}
```

The class name is the key (`door`, or `enemies/grunt`, which the picker shows as "Grunt"). It is what levels store and what the
export writes as `chip_class`, so renaming it orphans every placed entity of that class.

### Class fields

| Field | Meaning |
|---|---|
| `label` | Display name. Default: the name, humanised. |
| `doc` | First line under the class in Chip; the whole text as a tooltip. |
| `props` | The properties, in display order. |
| `bounds` | `[[minX, minY, minZ], [maxX, maxY, maxZ]]` in metres around the origin: the box Chip draws and clicks. Default: a 0.4 m cube. |
| `preview` | A `.glb` path relative to the manifest, drawn in place of the box. |
| `mesh` | `true`: a mesh class, made of level geometry tied to it (a door, a platform) instead of placed as a point ([`surfaces.md`](surfaces.md)). |
| `scene`, `uid` | The engine asset that makes the class and its stable id. Exported as `chip_scene` / `chip_uid`. |
| `base` | The engine type, informational. |

### Property fields

| Field | Meaning |
|---|---|
| `name` | Key in levels and in the export. Renaming it leaves old values under "Unused values". |
| `type` | See the type table. |
| `of` | Item type of an `array` (any type but `array` and `unsupported`). |
| `default` | The value an entity has until the designer changes it. Must be readable as the type, or it becomes "none". |
| `doc` | Tooltip. |
| `group` | Sub-heading in the inspector. |
| `options` | `enum` only: `[{ "label": "Shown", "value": <stored> }]`; values are strings or numbers. |
| `range` | `[min, max, step]` for numbers; the field clamps unless `or_greater` / `or_less` is `true`. Step 0 is free. |
| `suffix` | Unit shown after the number (`" m"`, `"°"`). |
| `integer` | `vector2` / `vector3` with whole-number components. |
| `class` | `entity`: the class it should point at (shown, not enforced). |
| `engine` | `unsupported`: the engine's type name, shown as "Set in <engine> (Dictionary)". |

For an `array`, the hints (`options`, `range`, `integer`, `class`, `suffix`) describe its items.

### Types

| `type` | Inspector | Value in levels and the export |
|---|---|---|
| `bool` | checkbox | `true` |
| `int`, `float` | drag field | `3`, `0.8` |
| `string` | text | `"abc"` |
| `enum` | dropdown | the option's `value` |
| `color` | colour picker | `[r, g, b, a]`, 0..1 |
| `vector2`, `vector3` | X/Y(/Z) fields | `[x, y]`, `[x, y, z]` |
| `entity` (Godot: `node`) | list of the level's entities | `{ "entity": 7 }`, the target's id |
| `asset` (Godot: `resource`) | text, hinted with `assetPrefix` | `"assets/sounds/door.wav"` |
| `array` | one row per item, add and remove | `[...]` of the item values |
| `unsupported` | "Set in <engine>" | never set by Chip |

Numbers inside a `float` field are metres or whatever the game means by them: the export scale never touches property
values.

## Defaults are the game's

A level stores only the values the designer changed. Every other property is absent from the `.chip` file and from the
export, so the game applies its own default. Keep the manifest's `default` equal to the game's real default, which is
automatic when the manifest is generated from the game's code.

## Generate it from the game

Generate the manifest from wherever the game defines its entity types, so the two can't disagree:

- **Code**: reflect over the entity types and their fields, as `samples/toy-game/game/Manifest.cs` does (attributes on
  C# classes, field `OpenAngle` → property `open_angle`, defaults from a new instance).
- **Data**: when entities are defined in data files (prefabs, JSON, a database), a build script translates those.
- **Hand-written**: fine for a handful of classes. It is the source of truth then, and the game's loader must agree with it.

Map the game's field types the same way every time:

| Game field | Manifest |
|---|---|
| A reference to another entity (often nullable) | `entity`, no `default` (a new entity references nothing) |
| A list | `array` with `of`: the item type from the field's declaration. When the language doesn't type its lists, give the item type in the entity definition (an annotation, attribute or override table) |
| An enumeration | `enum`, one option per member; `value` is what the game parses back |
| A path to a game asset | `asset` |
| A whole-number vector | `vector2`/`vector3` with `"integer": true` |
| Anything Chip can't edit (maps, nested lists) | `unsupported` with `engine` set to the type's name |

Commit the generated file, and regenerate it whenever entity definitions change.

## Check

```
Chip.exe --entities-check <game root | level | manifest.json> [--json]
```

Reads the project file and the manifest the way the editor does, then lists every class with its property types. It also
reports what the editor would quietly drop: unknown types and keys, an enum without options, a default its type can't
read, a property declared twice. Exit codes: 0 clean, 1 warnings, 2 errors (the project file or the manifest can't be
read), 3 no project or no manifest.
