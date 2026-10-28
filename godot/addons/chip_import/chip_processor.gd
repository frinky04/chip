@tool
extends RefCounted
## Turns a scene imported from a Chip .glb into a ready Godot scene. Godot 4.3+ imports glTF `extras` as the "extras"
## metadata of nodes and materials; this reads them:
##   - Entity nodes (extras.chip_class) become instances of their class's scene (extras.chip_uid, else
##     extras.chip_scene), with the level's property values applied. The instance stays linked to its scene, so editing
##     the scene updates every level. A class whose scene can't be found becomes a Marker3D carrying the data.
##   - Materials with extras.chip_path (or listed in a mesh node's extras.chip_remap) are swapped for that resource.
##   - Mesh entities (an entity node with mesh children) keep their meshes under the instance; colliders become
##     CollisionShape3D on its root, which should be a body or an area (AnimatableBody3D, Area3D).
##   - Volumes (extras.chip_volume, Chip's trigger and the game's own volume surfaces) become Area3D carrying
##     chip_surface metadata.
## Collision (-col, -colonly, -convcolonly) is left to Godot's built-in name suffix handling.
## Safe to run twice: converted nodes no longer carry the extras that trigger a conversion.
##
## Property values arrive typed (Chip coerces them with the manifest): numbers, bools, strings, [r, g, b, a] colours,
## [x, y, z] vectors, "res://" paths for resources and { "entity": id } for references to other entities, resolved to
## the instance of that entity once every entity is placed. Lists of those fill the property's typed Array (or packed
## array) in its own type.

const Manifest = preload("chip_manifest.gd")
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
		elif node is StaticBody3D and (extras.get("chip_volume", false) or str(extras.get("chip_tool", "")) == "trigger"):
			_make_trigger(scene, node as StaticBody3D, str(extras.get("chip_surface", "trigger")))
		elif _is_mesh(node) and extras.get("chip_volume", false) and node.get_parent() != null:
			_mesh_to_area(scene, node as Node3D, str(extras.get("chip_surface", "trigger")))
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
	_adopt_geometry(scene, node, entity, cls)
	parent.remove_child(node)
	node.free()
	return entity


## A mesh entity's geometry (its node's mesh children, in its space): meshes that draw move under the instance, and
## colliders and volumes become CollisionShape3D on it when its root is a body or an area.
static func _adopt_geometry(scene: Node, node: Node3D, entity: Node3D, cls: String) -> void:
	for child in node.get_children():
		if not _is_mesh(child):
			continue
		var extras := _extras(child)
		var volume: bool = extras.get("chip_volume", false)
		var kind := str(extras.get("chip_collider", "convex" if volume else ""))
		if kind != "":
			if entity is CollisionObject3D:
				var shape := _shape(child, kind == "convex")
				if shape != null:
					var cs := CollisionShape3D.new()
					cs.name = String(child.name) + "_shape"
					cs.shape = shape
					cs.transform = (child as Node3D).transform
					entity.add_child(cs, true)
					cs.owner = scene
			else:
				push_warning("Chip import: '%s' (class '%s') has collision, but its scene's root is a %s, not a body or area; the collision is dropped." % [node.name, cls, entity.get_class()])
		if extras.get("chip_render", true) and not volume:
			node.remove_child(child)
			entity.add_child(child, true)
			child.owner = scene


static func _shape(mesh_node: Node, convex: bool) -> Shape3D:
	var mesh: Mesh = null
	if mesh_node is MeshInstance3D:
		mesh = (mesh_node as MeshInstance3D).mesh
	elif mesh_node is ImporterMeshInstance3D and (mesh_node as ImporterMeshInstance3D).mesh != null:
		mesh = (mesh_node as ImporterMeshInstance3D).mesh.get_mesh()
	if mesh == null:
		return null
	return mesh.create_convex_shape() if convex else mesh.create_trimesh_shape()


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
	if value is Array and value.any(func(x): return x is Dictionary and x.has("entity")):
		links.append([entity, key, value])
		return
	var converted = _convert_list(value, entity.get(key), type) if value is Array and _is_list(type) else _convert(value, type)
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


static func _is_list(type: int) -> bool:
	return type == TYPE_ARRAY or type in Manifest.PACKED_ITEMS


## The list in the property's own type: a typed Array (the scene's value, emptied, keeps the type) or a packed array.
## Null when an item doesn't fit.
static func _convert_list(value: Array, current: Variant, type: int) -> Variant:
	var out: Array = current.duplicate() if current is Array else []
	out.clear()
	var item_type: int = Manifest.PACKED_ITEMS.get(type, out.get_typed_builtin())
	for x in value:
		var c = x if item_type == TYPE_NIL else _convert(x, item_type)
		if c == null and x != null:
			return null
		out.append(c)
	return out if type == TYPE_ARRAY else type_convert(out, type)


## Sets a reference (target is an id) or a list of them (target is the list, its items { "entity": id }).
static func _resolve(entity: Node, key: String, target: Variant, by_id: Dictionary) -> void:
	var paths := _property_type(entity, key) == TYPE_NODE_PATH
	if not target is Array:
		var node: Node = by_id.get(target)
		if node == null:
			push_warning("Chip import: '%s' on %s points at entity %d, which isn't in the export." % [key, entity.name, target])
		else:
			entity.set(key, entity.get_path_to(node) if paths else node)
		return
	var current = entity.get(key)
	var out: Array = current.duplicate() if current is Array else []
	out.clear()
	paths = out.get_typed_builtin() == TYPE_NODE_PATH
	for item in target:
		if not (item is Dictionary and item.has("entity")):
			continue  # a row not picked yet
		var node: Node = by_id.get(int(item["entity"]))
		if node == null:
			push_warning("Chip import: '%s' on %s lists entity %d, which isn't in the export." % [key, entity.name, int(item["entity"])])
		else:
			out.append(entity.get_path_to(node) if paths else node)
	entity.set(key, out)


# ---- Triggers ----

## A volume that arrived as a mesh (no -colonly suffix: loaded at runtime, or a project without suffixes).
static func _mesh_to_area(scene: Node, node: Node3D, surface: String) -> void:
	var area := Area3D.new()
	area.transform = node.transform
	area.set_meta("chip_tool", "trigger")
	area.set_meta("chip_surface", surface)
	var shape := _shape(node, true)
	var new_name := String(node.name)
	node.name = "__chip_replaced"
	area.name = new_name
	node.replace_by(area, true)
	area.owner = scene
	if shape != null:
		var cs := CollisionShape3D.new()
		cs.name = "shape"
		cs.shape = shape
		area.add_child(cs, true)
		cs.owner = scene
	node.free()


static func _make_trigger(scene: Node, body: StaticBody3D, surface: String) -> void:
	var area := Area3D.new()
	area.transform = body.transform
	area.set_meta("chip_tool", "trigger")
	area.set_meta("chip_surface", surface)
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
	# godot_path: files exported before Chip used the engine-neutral chip_path.
	var path := str(_extras(mat).get("chip_path", _extras(mat).get("godot_path", "")))
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
