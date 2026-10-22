@tool
extends EditorPlugin
## Registers a scene post-import plugin, so every imported scene that carries Chip data is processed without setting an
## import script per file, and keeps res://.chip/entities.json (the entity classes Chip offers) in step with the
## project: it is rewritten shortly after scenes or scripts are saved or files change.

const Manifest = preload("chip_manifest.gd")
## Batches bursts of saves and file changes into one rewrite.
const DELAY := 0.5

var _importer: EditorScenePostImportPlugin
var _queued := false


func _enter_tree() -> void:
	Manifest.register_setting()
	_importer = preload("chip_import_plugin.gd").new()
	add_scene_post_import_plugin(_importer)
	EditorInterface.get_resource_filesystem().filesystem_changed.connect(_queue)
	resource_saved.connect(_queue.unbind(1))
	scene_saved.connect(_queue.unbind(1))
	ProjectSettings.settings_changed.connect(_queue)
	_queue()


func _exit_tree() -> void:
	EditorInterface.get_resource_filesystem().filesystem_changed.disconnect(_queue)
	ProjectSettings.settings_changed.disconnect(_queue)
	if _importer:
		remove_scene_post_import_plugin(_importer)
		_importer = null


func _queue() -> void:
	if _queued or not is_inside_tree():
		return
	_queued = true
	get_tree().create_timer(DELAY).timeout.connect(_write)


func _write() -> void:
	_queued = false
	var result := Manifest.write()
	for w in result["warnings"]:
		push_warning(w)
	if result["changed"]:
		print("Chip: %d entity classes written to %s" % [result["classes"], Manifest.MANIFEST])
