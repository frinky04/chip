# Level logic: asset kinds, typed references and entity IO

Status: contract agreed, being built. This file pins the data shapes. The skill's references
(`skills/chip/reference/*.md`) take them over once each part is built.

The game's code defines the verbs (inputs), the events (outputs) and the kinds of thing a property can name. The level
only wires them. Every field a designer fills in is a pick from a list Chip can check, never free text.

## 1. Asset kinds

`chip.project.json` gains `"kinds"`: the kinds of file a property can name, where they live and how a value is written.

```jsonc
"kinds": {
  "sound":     { "root": "assets/sounds", "ext": [".wav"], "store": "stem", "doc": "A converted sound." },
  "sound_set": { "root": "assets/sounds", "folders": true },
  "model":     { "root": "assets/models", "ext": [".glb"], "store": "path" },
  "item":      { "root": "assets/data/items", "ext": [".json"], "store": "stem", "label": "name" }
}
```

| Field | Default | Meaning |
|---|---|---|
| `root` | required | Folder relative to the project root, searched recursively. |
| `ext` | any | File extensions that count, lower case with the dot. Ignored when `folders` is true. |
| `folders` | `false` | Values are folders under `root` (a set of sounds picked at random), not files. |
| `store` | `"path"` | `"path"`: project-relative path with extension, `/`-separated (`assets/models/door/main_door.glb`). `"stem"`: relative to `root`, without extension (`props/wood_break`). Folders store as a path under the project root for `path`, relative to `root` for `stem`. |
| `label` | none | For `.json` files: a top-level string field shown as the entry's display name. |
| `doc` | `""` | Tooltip. |

A property names its kind: `{ "name": "break_sound", "type": "asset", "kind": "sound" }`. The value is still a JSON
string, so loaders that ignore kinds keep working. `"kind"` is valid on `asset` properties and on `array` of `asset`.
An `asset` property without a kind behaves as now (free path, hinted with `assetPrefix`). An unknown kind is a manifest
warning. An empty string means "none".

## 2. Typed references and aliases

### References

An `entity` property (and an input parameter or output payload of type `entity`) may restrict its target:

| Field | Meaning |
|---|---|
| `class` | A class name, or an array of class names. The target must be one of them. |
| `trait` | A trait name (below). The target's class must declare it. |

With both, the target matches either. With neither, any entity matches. These are now **enforced**: the editor offers
only matching entities, and `--check-levels` reports a reference to a non-matching one.

### Traits

A class may declare `"traits": ["lockable", "toggleable"]`: names for what it can do, usually generated from the
interfaces its code implements. Traits exist for references and connection targets, and mean nothing else to Chip.

### Aliases

Classes, properties, inputs and outputs may carry `"was": ["old_name", ...]`. When Chip loads a level, a value or
connection stored under an old name moves to the new one (it never overwrites a value already under the new name).
`Chip.exe --migrate <game root | level>` applies the same renames and saves every level it changed, printing each file
and the number of renames.

## 3. Ports

A class declares the events it raises and the commands it takes:

```json
"door": {
  "traits": ["lockable"],
  "inputs": [
    { "name": "open", "doc": "Swings it open." },
    { "name": "set_locked", "params": [{ "name": "locked", "type": "bool", "default": true }] }
  ],
  "outputs": [
    { "name": "opened", "doc": "Finished opening." },
    { "name": "used_while_locked", "payload": { "type": "entity" } }
  ]
}
```

- `inputs[]`: `name`, `doc`, `was`, `params[]`. Each param has the shape of a manifest property (`name`, `type`, `default`,
  `range`, `options`, `kind`, `class`, `trait`, …).
- `outputs[]`: `name`, `doc`, `was`, `payload` (optional: a property shape without `name`, the value the event carries),
  `required` (`true`: `--check-levels` warns when an entity of this class doesn't connect it), `timeline` (`true`: the
  editor shows this output's connections as a timeline, see Sequences).
- A class may set `"logic": true`: it does nothing in the world (a relay, a counter), so the editor may draw it small.

## 4. Connections

### In the level

An entity holds its outgoing connections, in order:

```json
"outputs": [
  { "on": "used_while_locked", "target": { "entity": 12 }, "do": "play" },
  { "on": "opened", "target": { "tag": "corridor_lights" }, "do": "turn_off", "delay": 0.5, "once": true },
  { "on": "opened", "target": { "flag": "door_opened" }, "do": "set", "args": { "value": true },
    "when": { "flag": "power_on", "is": true } },
  { "on": "used_while_locked", "target": { "special": "activator" }, "do": "hurt", "args": { "amount": { "payload": true } } }
]
```

| Field | Default | Meaning |
|---|---|---|
| `on` | required | An output of the entity's class. |
| `target` | required | One of the target forms below. |
| `do` | required | An input of the target. |
| `args` | `{}` | Values for the input's params, by param name, typed like properties. A missing param uses its `default`. `{ "payload": true }` passes the output's payload instead of a constant; its type must match the param's. |
| `delay` | `0` | Seconds before the input runs. |
| `once` | `false` | The connection fires once, then never again. |
| `when` | none | A guard checked when the output fires (not when the delay ends): `{ "flag": f, "is": v }` (equal), or `{ "flag": f, "atLeast": n }` for an `int` flag. |

Target forms:

| Target | Means | Inputs offered |
|---|---|---|
| `{ "entity": 7 }` | that entity (by id, so renames never break it) | its class's |
| `{ "tag": "corridor_lights" }` | every entity carrying that tag, at the moment the input runs | the inputs every tagged entity's class has |
| `{ "special": "self" }` | the entity holding the connection | its class's |
| `{ "special": "activator" }` | whoever started the chain (usually the player) | any input name; checked only at runtime |
| `{ "special": "caller" }` | the entity whose output fired this connection (differs from `self` only in chains the game forwards) | any input name; checked only at runtime |
| `{ "flag": "power_on" }` | a flag (section 5) | `set` (param `value`), `add` (`int` flags: param `amount`), `toggle` (`bool` flags) |

### Entity tags

Entities get tags like meshes: a sorted, unique set of strings, edited the same way, stored as `"tags"` in the level
and exported as `chip_tags` on the entity node.

### In the export

An entity node's `extras` gain `chip_io`: its connections as stored, except that

- an `{ "entity": id }` target pointing at an entity that isn't exported drops that connection (Chip warns);
- every field is written out, defaults included (`args`, `delay`, `once`), so loaders need no defaults table.

The glTF `scene.extras.chip_flags` carries every declared flag:
`{ "power_on": { "type": "bool", "default": false, "scope": "game" } }`.

## 5. Flags

Named, typed state a level reads and writes, declared in `chip.project.json`:

```jsonc
"flags": {
  "power_on":         { "type": "bool", "default": false, "scope": "game", "doc": "The generator runs." },
  "generators_fixed": { "type": "int",  "default": 0,     "scope": "level" }
}
```

`type` is `bool` or `int`. `scope` is `level` (reset when a level loads) or `game` (kept across levels and saved). The
game owns their meaning at runtime. Chip only offers them as targets and in guards, and checks that both name a declared flag.

## 6. Sequences

A sequence is not a Chip feature. It is a game class (`logic_sequence`) with an input `start`, an input `cancel` and an
output `cue` marked `"timeline": true`. Its connections on `cue`, each with its own `delay`, are the timeline: flicker
at 0.0 s, sting at 0.3 s, slam at 1.2 s. `cancel` drops the cues of the current run that haven't fired yet. The editor
draws timeline outputs on a time axis; the data is ordinary connections.

## 7. Checks

`Chip.exe --check-levels <game root | level> [--json]` loads the project, its manifest and every `.chip` under the root
(skipping `bin`, `obj`, `.git`, `artifacts`, `dist` and folders starting with `.`), and reports per level:

| Severity | Problem |
|---|---|
| error | an entity of an unknown class |
| error | a reference or connection target pointing at a missing entity |
| error | a reference pointing at an entity its `class` / `trait` doesn't accept |
| error | a connection whose `on` isn't an output of the class, or whose `do` isn't an input of the target (for `entity`, `tag`, `self` and `flag` targets) |
| error | an `args` value its param's type can't read; a `payload` binding whose type doesn't match the param's; an arg for a param the input doesn't have |
| error | a flag target, or a `when`, naming an undeclared flag; `add` or `atLeast` on a `bool` flag; `toggle` on an `int` flag |
| error | an asset value with a kind, naming a file or folder that doesn't exist |
| warning | a value for a property the class doesn't declare (would show under "Unused values") |
| warning | a `tag` target no entity carries |
| warning | a `required` output with no connection |
| warning | a cycle of connections whose delays are all 0 (an infinite loop at runtime) |

Exit codes: 0 clean, 1 warnings only, 2 errors, 3 no project or no manifest. `--json` prints
`{ "levels": [ { "path": ..., "problems": [ { "severity", "entity", "message" } ] } ] }`.

## Out of scope for now

The Godot addon (it ignores the new keys), cross-level references, expressions in guards, and visual scripting. A
scene too complex for wiring gets its own game class with its own ports.
