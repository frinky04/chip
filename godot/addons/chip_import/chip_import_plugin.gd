@tool
extends EditorScenePostImportPlugin
## Runs the Chip processor on every imported scene. Scenes without Chip extras are left untouched.

const Processor = preload("chip_processor.gd")


func _post_process(scene: Node) -> void:
	Processor.process(scene)
