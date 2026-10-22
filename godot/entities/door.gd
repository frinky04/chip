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

## Not editable in Chip (arrays aren't supported yet).
@export var tags: Array[String] = []
