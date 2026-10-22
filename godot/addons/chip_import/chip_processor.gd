@tool
extends RefCounted
## Turns a scene imported from a Chip .glb into a ready Godot scene. Godot 4.3+ imports glTF `extras` as the "extras"
## metadata of nodes and materials; this reads them:
##   - Entity nodes (extras.chip_class) become instances of their class's scene (extras.chip_uid, else
##     extras.chip_scene), with the level's property values applied. The instance stays linked to its scene, so editing
##     the scene updates every level. A class whose scene can't be found becomes a Marker3D carrying the data.
##   - Materials with extras.godot_path (or listed in a mesh node's extras.chip_remap) are swapped for that resource.
##   - Trigger volumes (extras.chip_tool == "trigger") become Area3D.
## Collision (-col, -colonly, -convcolonly) is left to Godot's built-in name suffix handling.
## Safe to run twice: converted nodes no longer carry the extras that trigger a conversion.
##
## Property values arrive typed (Chip coerces them with the manifest): numbers, bools, strings, [r, g, b, a] colours,
## [x, y, z] vectors, "res://" paths for resources and { "entity": id } for references to other entities, resolved to
## the instance of that entity once every entity is placed.

const ENTITY_GROUP := &"chip_entities"


static func process(scene: Node) -> void:
	var nodes: Array[Node] = []
	_collect(scene, nodes)
	var by_id := {}    # chip_id -> entity node
	var links := []    # [node, property, chip_id] references to resolve once every entity exists
	for node in nodes:
		if not is_instance_valid(node) or node == scene:
			continue
		_remap_materials(node)
		var extras := _extras(node)
		if extras.has("chip_class") and node is Node3D and not _is_mesh(node):
			var entity := _replace_entity(scene, node as Node3D, extras, links)
			if extras.has("chip_id"):
				by_id[int(extras["chip_id"])] = entity
		elif node is StaticBody3D and str(extras.get("chip_tool", "")) == "trigger":
			_make_trigger(scene, node as StaticBody3D)
	for link in links:
		_resolve(link[0], link[1], link[2], by_id)


static func _collect(node: Node, out: Array[Node]) -> void:
	out.append(node)
	for child in node.get_children():
		_collect(child, out)


static func _is_mesh(node: Node) -> bool:
	return node is MeshInstance3D or node is ImporterMeshInstance3D


static func _extras(obj: Object) -> Dictionary:
	if obj != null and obj.has_meta("extras"):
		var e = obj.get_meta("extras")
		if e is Dictionary:
			return e
	return {}


# ---- Entities ----

static func _replace_entity(scene: Node, node: Node3D, extras: Dictionary, links: Array) -> Node3D:
	var cls := str(extras["chip_class"])
	var props: Dictionary = extras["properties"] if extras.get("properties") is Dictionary else {}
	var entity := _instantiate(cls, extras)
	if entity == null:
		entity = Marker3D.new()
	else:
		for key in props:
			_apply(entity, cls, str(key), props[key], links)
	entity.transform = node.transform
	entity.set_meta("chip_class", cls)
	entity.set_meta("chip_properties", props)
	if extras.has("chip_id"):
		entity.set_meta("chip_id", extras["chip_id"])
	entity.add_to_group(ENTITY_GROUP, true)

	var parent := node.get_parent()
	var index := node.get_index()
	var new_name := String(node.name)
	node.name = "__chip_replaced"  # free the name for the replacement
	entity.name = new_name
	parent.add_child(entity, true)
	parent.move_child(entity, index)
	entity.owner = scene  # only the root: the instance's own nodes stay owned by it, so it saves as a linked instance
	parent.remove_child(node)
	node.free()
	return entity


## The class's scene, instantiated as an editable instance, or null (with a warning) when there's no usable scene.
static func _instantiate(cls: String, extras: Dictionary) -> Node3D:
	var path := ""
	for key in ["chip_uid", "chip_scene"]:
		var candidate := str(extras.get(key, ""))
		if candidate != "" and ResourceLoader.exists(candidate):
			path = candidate
			break
	if path == "":
		if extras.has("chip_scene") or extras.has("chip_uid"):
			push_warning("Chip import: the scene for entity class '%s' (%s) is gone; it becomes a Marker3D. Restore the scene or change the class in Chip." % [cls, extras.get("chip_scene", extras.get("chip_uid"))])
		return null
	var packed := load(path) as PackedScene
	if packed == null:
		push_warning("Chip import: %s (class '%s') isn't a scene; the entity becomes a Marker3D." % [path, cls])
		return null
	var node := packed.instantiate(PackedScene.GEN_EDIT_STATE_INSTANCE)
	if node is Node3D:
		return node
	push_warning("Chip import: %s (class '%s') has a %s root, not a Node3D; the entity becomes a Marker3D." % [path, cls, node.get_class()])
	node.free()
	return null


static func _apply(entity: Node, cls: String, key: String, value: Variant, links: Array) -> void:
	var type := _property_type(entity, key)
	if type == TYPE_NIL:
		push_warning("Chip import: class '%s' has no property '%s'; the value stays in the chip_properties metadata." % [cls, key])
		return
	if value is Dictionary and value.has("entity"):
		links.append([entity, key, int(value["entity"])])
		return
	var converted = _convert(value, type)
	if converted == null and value != null:
		push_warning("Chip import: '%s' on class '%s' can't take %s; it keeps the scene's value." % [key, cls, JSON.stringify(value)])
		return
	entity.set(key, converted)


static func _property_type(obj: Object, key: String) -> int:
	for p in obj.get_property_list():
		if p["name"] == key:
			return p["type"]
	return TYPE_NIL


static func _convert(value: Variant, type: int) -> Variant:
	if value == null:
		return null
	match type:
		TYPE_COLOR:
			if value is Array and value.size() >= 3:
				return Color(value[0], value[1], value[2], value[3] if value.size() > 3 else 1.0)
			if value is String and Color.html_is_valid(value):
				return Color.html(value)
			return null
		TYPE_VECTOR2, TYPE_VECTOR2I:
			return type_convert(Vector2(value[0], value[1]), type) if value is Array and value.size() >= 2 else null
		TYPE_VECTOR3, TYPE_VECTOR3I:
			return type_convert(Vector3(value[0], value[1], value[2]), type) if value is Array and value.size() >= 3 else null
		TYPE_OBJECT:
			if value is String and value != "" and ResourceLoader.exists(value):
				return load(value)
			return null
		TYPE_NODE_PATH:
			return null  # node references arrive as { "entity": id }
		TYPE_BOOL, TYPE_INT, TYPE_FLOAT, TYPE_STRING, TYPE_STRING_NAME:
			return type_convert(value, type)
	return null


static func _resolve(entity: Node, key: String, target_id: int, by_id: Dictionary) -> void:
	var target: Node = by_id.get(target_id)
	if target == null:
		push_warning("Chip import: '%s' on %s points at entity %d, which isn't in the export." % [key, entity.name, target_id])
		return
	if _property_type(entity, key) == TYPE_NODE_PATH:
		entity.set(key, entity.get_path_to(target))
	else:
		entity.set(key, target)


# ---- Triggers ----

static func _make_trigger(scene: Node, body: StaticBody3D) -> void:
	var area := Area3D.new()
	area.transform = body.transform
	area.set_meta("chip_tool", "trigger")
	var new_name := String(body.name)
	body.name = "__chip_replaced"
	area.name = new_name
	body.replace_by(area, true)
	area.owner = scene
	body.free()


# ---- Materials ----

static func _remap_materials(node: Node) -> void:
	var table: Dictionary = _extras(node).get("chip_remap", {}) if _extras(node).get("chip_remap") is Dictionary else {}
	if node is MeshInstance3D:
		var mi := node as MeshInstance3D
		if mi.mesh == null:
			return
		for i in mi.mesh.get_surface_count():
			var mat := _remapped(mi.mesh.surface_get_material(i), table)
			if mat != null:
				mi.set_surface_override_material(i, mat)
	elif node is ImporterMeshInstance3D:
		var im := node as ImporterMeshInstance3D
		if im.mesh == null:
			return
		for i in im.mesh.get_surface_count():
			var mat := _remapped(im.mesh.get_surface_material(i), table)
			if mat != null:
				im.mesh.set_surface_material(i, mat)


static func _remapped(mat: Material, table: Dictionary) -> Material:
	if mat == null:
		return null
	var path := str(_extras(mat).get("godot_path", ""))
	if path.is_empty():
		path = str(table.get(mat.resource_name, ""))
	if path.is_empty():
		return null
	if not ResourceLoader.exists(path):
		push_warning("Chip import: material '%s' is remapped to %s, which doesn't exist. Fix the path in Chip's export settings." % [mat.resource_name, path])
		return null
	var res := load(path)
	if res is Material:
		return res as Material
	push_warning("Chip import: %s is not a Material (remap for '%s')." % [path, mat.resource_name])
	return null
