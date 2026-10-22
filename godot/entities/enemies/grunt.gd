extends CharacterBody3D
## A basic enemy that patrols towards a target. Its mood:
## - Calm: wanders
## - Alert: searches[br]and calls for help

enum Mood { CALM, ALERT = 5, ANGRY }

@export var mood := Mood.CALM
@export var health := 100
@export var patrol_to: NodePath
@export var target: Node3D
