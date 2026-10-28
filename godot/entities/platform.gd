@tool
extends AnimatableBody3D
## A platform that moves back and forth. Its body is the level geometry tied to it in Chip.

## How far it travels from where it starts.
@export var travel := Vector3(0, 2, 0)
## Seconds for one way.
@export_range(0.1, 30, 0.1, "or_greater", "suffix:s") var duration := 3.0
