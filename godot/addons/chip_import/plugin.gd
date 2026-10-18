@tool
extends EditorPlugin
## Registers a scene post-import plugin, so every imported scene that carries Chip data is processed
## without setting an import script per file.

var _importer: EditorScenePostImportPlugin


func _enter_tree() -> void:
	_importer = preload("chip_import_plugin.gd").new()
	add_scene_post_import_plugin(_importer)


func _exit_tree() -> void:
	if _importer:
		remove_scene_post_import_plugin(_importer)
		_importer = null
