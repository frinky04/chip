@tool
extends EditorScenePostImport
## Per-file import script: select a Chip .glb, open the Import dock, set Import Script to this file and
## click Reimport. Not needed when the Chip import plugin is enabled (it does the same for every scene);
## running both is harmless because the processor only acts on data it hasn't converted yet.

const Processor = preload("chip_processor.gd")


func _post_import(scene: Node) -> Object:
	Processor.process(scene)
	return scene
