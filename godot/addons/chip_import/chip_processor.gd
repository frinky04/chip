@tool
extends RefCounted
## Turns a scene imported from a Chip .glb into a ready Godot scene. Godot 4.3+ imports glTF `extras` as
## the "extras" metadata of nodes and materials; this reads them:
##   - Entity nodes (extras.chip_class) become Marker3D / OmniLight3D / SpotLight3D.
##   - Materials with extras.godot_path (or listed in a mesh node's extras.chip_remap) are swapped for that
##     project resource.
##   - Trigger volumes (extras.chip_tool == "trigger", or StaticBody3D named *_trigger) become Area3D.
## Collision (-col, -colonly, -convcolonly) is left to Godot's built-in name suffix handling.
## Safe to run twice: converted nodes no longer carry the extras that trigger a conversion.

const SPAWN_GROUP := &"chip_spawn"
const ENTITY_GROUP := &"chip_entities"


static func process(scene: Node) -> void:
	var nodes: Array[Node] = []
	_collect(scene, nodes)
	var spawns := 0
	for node in nodes:
		if not is_instance_valid(node) or node == scene:
			continue
		_remap_materials(node)
		var extras := _extras(node)
		if extras.has("chip_class") and node is Node3D and not _is_mesh(node):
			if str(extras["chip_class"]) == "info_player_start":
				spawns += 1
			_replace_entity(scene, node as Node3D, extras, spawns)
		elif node is StaticBody3D and (str(extras.get("chip_tool", "")) == "trigger" or String(node.name).ends_with("_trigger")):
			_make_trigger(scene, node as StaticBody3D)


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

static func _replace_entity(scene: Node, node: Node3D, extras: Dictionary, spawn_index: int) -> void:
	var cls := str(extras["chip_class"])
	var props: Dictionary = extras["properties"] if extras.get("properties") is Dictionary else {}
	var replacement: Node3D
	var new_name := String(node.name)
	match cls:
		"info_player_start":
			replacement = Marker3D.new()
			new_name = "spawn" if spawn_index == 1 else "spawn_%d" % spawn_index
			replacement.add_to_group(SPAWN_GROUP, true)
		"light_point":
			var omni := OmniLight3D.new()
			_apply_light(omni, props)
			if _has_any(props, ["range", "radius"]):
				omni.omni_range = _float(props, ["range", "radius"], omni.omni_range)
			replacement = omni
		"light_spot":
			# SpotLight3D shines along its -Z axis, the same forward Chip entities use.
			var spot := SpotLight3D.new()
			_apply_light(spot, props)
			if _has_any(props, ["range", "radius"]):
				spot.spot_range = _float(props, ["range", "radius"], spot.spot_range)
			if _has_any(props, ["spot_angle", "angle", "cone"]):
				spot.spot_angle = _float(props, ["spot_angle", "angle", "cone"], spot.spot_angle)
			replacement = spot
		_:
			replacement = Marker3D.new()
	replacement.transform = node.transform
	replacement.set_meta("chip_class", cls)
	replacement.set_meta("chip_properties", props)
	if extras.has("chip_id"):
		replacement.set_meta("chip_id", extras["chip_id"])
	replacement.add_to_group(ENTITY_GROUP, true)
	node.name = "__chip_replaced"  # free the name for the replacement
	replacement.name = new_name
	node.replace_by(replacement, true)
	replacement.owner = scene
	node.free()


static func _apply_light(light: Light3D, props: Dictionary) -> void:
	if props.has("color"):
		light.light_color = _color(str(props["color"]))
	elif props.has("_light"):  # Hammer style: "r g b brightness"
		var parts := str(props["_light"]).split_floats(" ", false)
		light.light_color = _color(str(props["_light"]))
		if parts.size() >= 4 and not _has_any(props, ["energy", "brightness"]):
			light.light_energy = parts[3] / 200.0
	if _has_any(props, ["energy", "brightness"]):
		light.light_energy = _float(props, ["energy", "brightness"], light.light_energy)
	if props.has("shadows"):
		light.shadow_enabled = str(props["shadows"]).strip_edges().to_lower() in ["1", "true", "yes", "on"]


## "#ff8800", "ff8800", "255 136 0" or "1 0.5 0".
static func _color(value: String) -> Color:
	var s := value.strip_edges()
	if s.begins_with("#") or ((s.length() == 6 or s.length() == 8) and s.is_valid_hex_number()):
		return Color.html(s)
	var parts := s.split_floats(" ", false)
	if parts.size() >= 3:
		if parts[0] > 1.0 or parts[1] > 1.0 or parts[2] > 1.0:
			return Color(parts[0] / 255.0, parts[1] / 255.0, parts[2] / 255.0)
		return Color(parts[0], parts[1], parts[2])
	return Color.WHITE


static func _has_any(props: Dictionary, keys: Array) -> bool:
	for k in keys:
		if props.has(k):
			return true
	return false


static func _float(props: Dictionary, keys: Array, fallback: float) -> float:
	for k in keys:
		if props.has(k) and str(props[k]).is_valid_float():
			return str(props[k]).to_float()
	return fallback


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
