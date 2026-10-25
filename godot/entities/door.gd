@tool
extends Node3D
## A door that swings or slides open. (Sample entity class: it exercises every property type Chip edits.)

## How the door opens.
@export_enum("swing", "slide") var mode := "swing"
## Opening angle for swinging doors.
@export_range(0, 180, 1, "or_greater", "suffix:°") var open_angle := 90.0
@export var locked := false
@export var tint := Color(0.8, 0.5, 0.2)
@export var keys_needed := 1
## Opens together with this one.
@export var partner: Node3D
@export var skin: Material

@export_group("Sound")
@export_range(0, 1) var volume := 0.8
@export var sound_offset := Vector3(0, 1, 0)

@export_group("Lists")
## Slot tags.
@export var tags: Array[StringName] = [&"wood"]
@export var cells: Array[Vector2i] = []
## Doors that open with this one.
@export var linked: Array[Node3D] = []
@export var weights := PackedFloat32Array([0.5])
## Not editable in Chip (dictionaries aren't supported).
@export var notes := {}
