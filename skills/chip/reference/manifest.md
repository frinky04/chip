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
| `kinds` | none | The kinds of file an asset property can name (Asset kinds, below). |
| `flags` | none | Named level and game state that connections set and test ([`logic.md`](logic.md), Flags). |

An unknown field makes the whole file unreadable, and Chip then falls back to its built-in classes. Comments and trailing commas are allowed.

### Asset kinds

An asset property with a `kind` is a dropdown of the project's files of that kind instead of a text field, and
`--check-levels` reports a value naming a file that isn't there. Declare each kind once, matching the form the game's
loader already reads:

```jsonc
"kinds": {
  "sound":     { "root": "assets/sounds", "ext": [".ogg"], "store": "stem" },     // "props/wood_break"
  "sound_set": { "root": "assets/sounds", "folders": true, "store": "stem" },    // "props/glass_break"
  "model":     { "root": "assets/models", "ext": [".glb"], "store": "path" },    // "assets/models/door/main_door.glb"
  "item":      { "root": "assets/data/items", "ext": [".json"], "store": "stem", "label": "name" }
}
```

| Field | Default | Meaning |
|---|---|---|
| `root` | required | Folder relative to the project file, searched recursively. |
| `ext` | any | File extensions that count, with the dot. Ignored for `folders`. |
| `folders` | `false` | Values are folders under `root` (a set of files picked at random), not files. |
| `store` | `"path"` | `path`: relative to the project, with extension, `/`-separated. `stem`: relative to `root`, without extension. |
| `label` | none | For `.json` files: a top-level string field shown as the entry's name. |
| `doc` | `""` | Tooltip. |

The value stays a string, and `""` means none. Changing `store` or `root` changes what every stored value means; keep
them matching what levels already hold.
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
export writes as `chip_class`. To rename a class, property, input or output, keep its old name in `was`: levels then
carry over (Renames, below).

### Class fields

| Field | Meaning |
|---|---|
| `label` | Display name. Default: the name, humanised. |
| `doc` | First line under the class in Chip; the whole text as a tooltip. |
| `props` | The properties, in display order. |
| `bounds` | `[[minX, minY, minZ], [maxX, maxY, maxZ]]` in metres around the origin: the box Chip draws and clicks. Default: a 0.4 m cube. |
| `preview` | A `.glb` path relative to the manifest, drawn in place of the box. |
| `icon` | The class's glyph in the viewport, the outliner and the class lists: a [Tabler](https://tabler.io/icons) icon name (`"door"`, `"device-desktop"`). Default: a box (the built-in classes' names keep their own). |
| `color` | The colour the class is drawn in: `"#rrggbb"` or `[r, g, b]` in 0..1. Avoid orange, Chip's selection colour. |
| `mesh` | `true`: a mesh class, made of level geometry tied to it (a door, a platform) instead of placed as a point ([`surfaces.md`](surfaces.md)). |
| `scene`, `uid` | The engine asset that makes the class and its stable id. Exported as `chip_scene` / `chip_uid`. |
| `base` | The engine type, informational. |
| `inputs`, `outputs` | The commands it takes and the events it raises, which connections wire ([`logic.md`](logic.md), Ports). |
| `traits` | Names for what it can do (`["lockable"]`), usually its code's interfaces. References and connections can ask for a trait instead of a class. |
| `logic` | `true`: it does nothing in the world by itself (a relay, a counter). |
| `was` | Old names of the class (Renames). |

### Property fields

| Field | Meaning |
|---|---|
| `name` | Key in levels and in the export. Renaming it without `was` leaves old values under "Unused values". |
| `was` | Old names of the property (Renames). |
| `type` | See the type table. |
| `of` | Item type of an `array` (any type but `array` and `unsupported`). |
| `default` | The value an entity has until the designer changes it. Must be readable as the type, or it becomes "none". |
| `doc` | Tooltip. |
| `group` | Sub-heading in the inspector. |
| `options` | `enum` only: `[{ "label": "Shown", "value": <stored> }]`; values are strings or numbers. |
| `range` | `[min, max, step]` for numbers; the field clamps unless `or_greater` / `or_less` is `true`. Step 0 is free. |
| `suffix` | Unit shown after the number (`" m"`, `"°"`). |
| `integer` | `vector2` / `vector3` with whole-number components. |
| `class` | `entity`: the class, or array of classes, it may point at. |
| `trait` | `entity`: a trait its target's class must have. With `class` as well, either matches; with neither, any entity does. Chip lists only matching entities and `--check-levels` reports a mismatch. |
| `kind` | `asset`: a kind from the project file. Without one the field is free text. |
| `engine` | `unsupported`: the engine's type name, shown as "Set in <engine> (Dictionary)". |

For an `array`, the hints (`options`, `range`, `integer`, `class`, `trait`, `kind`, `suffix`) describe its items.

### Types

| `type` | Inspector | Value in levels and the export |
|---|---|---|
| `bool` | checkbox | `true` |
| `int`, `float` | drag field | `3`, `0.8` |
| `string` | text | `"abc"` |
| `enum` | dropdown | the option's `value` |
| `color` | colour picker | `[r, g, b, a]`, 0..1 |
| `vector2`, `vector3` | X/Y(/Z) fields | `[x, y]`, `[x, y, z]` |
| `entity` (Godot: `node`) | search of the entities it accepts, a viewport picker | `{ "entity": 7 }`, the target's id |
| `asset` (Godot: `resource`) | with a `kind`: the kind's files; else text hinted with `assetPrefix` | `"props/wood_break"`, as the kind stores it |
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
| A reference to another entity (often nullable) | `entity`, no `default` (a new entity references nothing); `class` from its declared type, `trait` when that type is an interface |
| A list | `array` with `of`: the item type from the field's declaration. When the language doesn't type its lists, give the item type in the entity definition (an annotation, attribute or override table) |
| An enumeration | `enum`, one option per member; `value` is what the game parses back |
| A path to a game asset | `asset` with `kind`. Give each kind its own small type in the game (a sound handle, an item id), so the type says the kind and the code can't mix them up |
| A whole-number vector | `vector2`/`vector3` with `"integer": true` |
| Anything Chip can't edit (maps, nested lists) | `unsupported` with `engine` set to the type's name |
| An interface the class implements, meaning a capability | a `traits` entry |
| A method the level may call / an event it raises | an `inputs` / `outputs` entry ([`logic.md`](logic.md), Ports) |
| A former name of a class, field, method or event (an attribute argument) | its `was` |

Commit the generated file, and regenerate it whenever entity definitions change.

## Renames

A `was` list names what something used to be called. When Chip loads a level (the editor, `--export`,
`--check-levels`), a class, property value, connection output or input, or argument stored under an old name moves to
the new one; a value already under the new name wins. The editor makes this one undo step and saves it with the level.
`Chip.exe --migrate <game root | level>` writes it into every level at once. Keep `was` entries until every level has
been migrated; they cost nothing.

## Check

```
Chip.exe --entities-check <game root | level | manifest.json> [--json]
```

Reads the project file and the manifest the way the editor does, then lists every class with its property types, ports
and traits. It also reports what the editor would quietly drop: unknown types and keys, an enum without options, a
default its type can't read, a property declared twice, a `kind` the project doesn't declare, a `was` that collides
with a current name. Exit codes: 0 clean, 1 warnings, 2 errors (the project file or the manifest can't be
read), 3 no project or no manifest.
