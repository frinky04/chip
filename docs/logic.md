# Level logic: asset kinds, typed references and entity IO

Status: built. Asset kinds, enforced references and renames, and entity IO (inputs, outputs, connections, tags, flags,
sequences), with `--check-levels` and `--migrate`. The formats are in the skill's references:
[`manifest.md`](../skills/chip/reference/manifest.md) (kinds, ports in the manifest, renames),
[`logic.md`](../skills/chip/reference/logic.md) (connections, flags, sequences, runtime rules, checks) and
[`export.md`](../skills/chip/reference/export.md) (`chip_io`, `chip_tags`, `chip_flags`).

The game's code defines the verbs (inputs), the events (outputs) and the kinds of file a property can name. The level
only wires them, and every field a designer fills in is a pick from a list Chip can check, never free text.

## Why not Hammer's IO

Source's entity IO is the right idea, wiring instead of scripting, done with strings:

| | Hammer | Chip |
|---|---|---|
| Inputs and outputs | Declared in the FGD, again, beside the code | Generated from the class's code (methods, events) |
| Target | A targetname, wildcards; a rename breaks it silently | An entity id, a tag, self, the activator or a flag |
| Input | Typed by hand; a typo fails at runtime | Picked from what the target has |
| Parameter | One string, parsed by the input | Typed arguments with the property widgets, or the output's own value |
| State | `logic_branch`, `env_global`, counters | Flags in the project file, tested by a connection's guard |
| Timed scenes | Relays chained with delays | A sequence: beats on a time axis |
| Finding mistakes | Playtesting | `--check-levels`, in the inspector and in the game's tests |
| Renames | Every map edited by hand | `was` in the code; levels carry over on load or with `--migrate` |

Kept from Source: connections live on the entity that fires them, delays and fire-once, and `!activator`.

## In the editor

- **Outputs** (inspector, an entity with outputs): each output of its class with its connections. **+** adds one,
  asking for the target first. A connection's row picks the target (with the eyedropper to click one), then what it
  does, its delay and Once; argument fields follow the input's types, and **←** passes the output's value instead.
  **+ Only when…** adds a flag condition. Problems with the entity's connections show in red under the list.
- **Timeline outputs** (a sequence's `cue`) also show their connections as beats on a time axis; drag one to move it.
- **Links** (inspector) lists what the entity points at and what points at it, through references and connections;
  click a row to select that entity. In the viewport, selected entities draw lines to what they point at (cyan) and from
  what points at them (violet).
- **References** list only the entities their class or trait accepts, with search, the eyedropper and a button that
  selects the target.
- **Asset fields** with a kind are a searchable list of the project's files, which follows the folder as files come and
  go. A value naming a missing file shows red.
- **Tags** (`Ctrl+T`) in object mode tag entities with meshes; a connection can target every entity with a tag.
- Opening a level whose classes were renamed moves its values to the new names as one undo step.

## Out of scope for now

The Godot addon (it ignores the new keys), references across levels, expressions in guards, and visual scripting. A
scene too complex for wiring gets its own game class with its own ports.
