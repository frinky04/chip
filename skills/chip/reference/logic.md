# Level logic

Entity IO: the game's code declares what each class can do (**inputs**) and what it reports (**outputs**), and the level
wires outputs to inputs with **connections**. The game owns the verbs; the level only wires them. A designer picks every
part of a connection from a list Chip checks, so a connection never names an input its target lacks.

## Ports

In the manifest, beside `props`:

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

| Field | On | Meaning |
|---|---|---|
| `name`, `doc`, `was` | both | As for properties: key, tooltip, old names. |
| `params` | input | The arguments, each shaped like a property (`name`, `type`, `default`, `range`, `kind`, `class`, …). |
| `payload` | output | The value the event carries (the entity that tried, the damage), a property shape without `name`. |
| `required` | output | `true`: `--check-levels` warns about an entity of the class that doesn't connect it. |
| `timeline` | output | `true`: Chip lays this output's connections out on a time axis by delay (Sequences). |

Generate ports from the code like properties: a method the level may call is an input (its parameters become
`params`), an event field is an output (its type argument becomes `payload`), and an interface the class implements is
a trait.

## Connections

An entity's `outputs` in the level, and its `chip_io` in the export:

```json
[
  { "on": "used_while_locked", "target": { "special": "activator" }, "do": "hurt", "args": { "amount": 10 } },
  { "on": "opened", "target": { "tag": "corridor_lights" }, "do": "turn_off", "delay": 0.5, "once": true },
  { "on": "opened", "target": { "flag": "door_opened" }, "do": "set", "args": { "value": true },
    "when": { "flag": "power_on", "is": true } },
  { "on": "used_while_locked", "target": { "entity": 12 }, "do": "look_at", "args": { "who": { "payload": true } } }
]
```

| Field | Default | Meaning |
|---|---|---|
| `on` | required | An output of the entity's class. |
| `target` | required | One target form, below. |
| `do` | required | An input of the target. |
| `args` | `{}` | Values for the input's params by name, typed like properties; a missing one takes the param's `default`. `{ "payload": true }` passes the output's payload instead; its type matches the param's. |
| `delay` | `0` | Seconds before the input runs. |
| `once` | `false` | Fires the first time, then never again. |
| `when` | none | A guard: `{ "flag": f, "is": v }`, or `{ "flag": f, "atLeast": n }` for an `int` flag. |

| Target | Means | Inputs |
|---|---|---|
| `{ "entity": 7 }` | that entity, by id | its class's |
| `{ "tag": "corridor_lights" }` | every entity carrying the tag (`chip_tags`) when the input runs | those all of them have |
| `{ "special": "self" }` | the entity holding the connection | its class's |
| `{ "special": "activator" }` | whoever started the chain, usually the player | any; known only at runtime |
| `{ "special": "caller" }` | the entity whose output fired this connection | any; known only at runtime |
| `{ "flag": "power_on" }` | a flag | `set` (`value`), `add` (`amount`, `int` flags), `toggle` (`bool` flags) |

### Running them

A game's dispatcher follows these rules, so levels behave the same in every game:

- An output fires its connections in their stored order.
- The `when` guard is checked when the output fires, not when the delay ends.
- A delayed connection runs after `delay` seconds of game time. Zero-delay connections run in the same frame, in the
  order they were queued: all of an output's connections, then whatever those fire.
- A `once` connection that fired never fires again, including after a save and load.
- A `tag` target resolves when the input runs, so entities that gained or lost the tag in the meantime count.
- The activator travels down the chain: an input that fires outputs passes on the activator it was given.
- An unknown input or target is logged once and skipped. It never throws: an old level must not stop the game.
- Cap the zero-delay work per frame, so a loop of zero-delay connections stalls one chain, not the game.

## Flags

Named state in `chip.project.json`, exported as `chip_flags`:

```jsonc
"flags": {
  "power_on":         { "type": "bool", "default": false, "scope": "game", "doc": "The generator runs." },
  "generators_fixed": { "type": "int",  "default": 0,     "scope": "level" }
}
```

`type` is `bool` or `int`; `scope` is `level` (reset when a level loads) or `game` (kept across levels and in saves).
Connections set flags through flag targets and test them in `when`. Put state in flags, not in chains of relays: a
flag survives a save, says what it means, and every connection that tests it shows up in `--check-levels`.

## Sequences

A scripted moment (lights flicker, a sting plays, a door slams, something appears) is a game class with a `start`
input, a `cancel` input and a `timeline` output (`cue`). Each connection on `cue` is one beat, at its own `delay` from
the start. `cancel` drops the run's beats that haven't fired. In Chip the beats sit on a time axis, where dragging one
changes its delay. Use a sequence wherever a chain of relays would only add delays.

## Checks

`Chip.exe --check-levels <game root | level> [--json]` reports, per level:

| Severity | Problem |
|---|---|
| error | an entity of an unknown class |
| error | a reference or connection pointing at a missing entity, or at one its `class` / `trait` doesn't accept |
| error | an `on` the class doesn't output; a `do` the target doesn't take (entity, tag, self and flag targets) |
| error | an argument its param can't read, a payload of the wrong type, an argument the input doesn't have |
| error | an undeclared flag in a target or `when`; `add` or `atLeast` on a `bool` flag, `toggle` on an `int` one |
| error | an asset value with a kind, naming no file of that kind |
| warning | a value for a property the class doesn't declare |
| warning | a `tag` target no entity carries |
| warning | a `required` output with no connection |
| warning | a loop of connections whose delays are all 0 |

`--json` prints `{ "levels": [ { "path", "problems": [ { "severity", "entity", "message" } ] } ] }`. Run it in the game's
tests, so a renamed input breaks the build instead of a playtest.
