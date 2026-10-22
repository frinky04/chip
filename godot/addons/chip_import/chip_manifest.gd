@tool
extends RefCounted
## Writes the entity manifest Chip reads: res://.chip/entities.json, plus a preview .glb per class that has meshes.
##
## Every scene under the entity folders is a class (Project Settings > chip/entities/folders, default res://entities).
## Its name is the path relative to the folder, without extension ("enemies/grunt"). Files and folders starting with "_"
## or "." are skipped, so helper scenes can live beside the classes. The addon's own classes (player start, lights,
## target) come first; a project scene with the same name replaces one.
##
## A class's properties are the root script's exported variables, plus any native properties named in the root's
## "chip_expose" metadata (a PackedStringArray, e.g. ["light_color", "omni_range"]). Defaults come from the instantiated
## scene, so values set in the scene override the script's. "##" doc comments become tooltips. The root's optional
## "chip_bounds" metadata (an AABB) sets the box Chip draws when the scene has no meshes.
##
## The manifest is generated: never edit it. Chip watches it and reloads on change. It is only rewritten when its
## content changes.

const VERSION := 1
const OUT_DIR := "res://.chip"
const MANIFEST := "res://.chip/entities.json"
const PREVIEWS := "previews"
const BUILTIN_DIR := "res://addons/chip_import/entities"
const SETTING := "chip/entities/folders"
const DEFAULT_FOLDERS := ["res://entities"]


## Adds the folders setting to Project Settings (editor only).
static func register_setting() -> void:
	if not ProjectSettings.has_setting(SETTING):
		ProjectSettings.set_setting(SETTING, PackedStringArray(DEFAULT_FOLDERS))
	ProjectSettings.set_initial_value(SETTING, PackedStringArray(DEFAULT_FOLDERS))
	ProjectSettings.add_property_info({
		"name": SETTING, "type": TYPE_PACKED_STRING_ARRAY, "hint": PROPERTY_HINT_TYPE_STRING,
		"hint_string": "%d/%d:" % [TYPE_STRING, PROPERTY_HINT_DIR],
	})


static func folders() -> PackedStringArray:
	return PackedStringArray(ProjectSettings.get_setting(SETTING, PackedStringArray(DEFAULT_FOLDERS)))


## Builds the manifest and writes it (and the previews) when they changed.
## Returns { "classes": int, "changed": bool, "warnings": PackedStringArray, "path": String }.
static func write(out_dir: String = OUT_DIR) -> Dictionary:
	var warnings := PackedStringArray()
	var classes := build(warnings)
	var previews := {}  # relative preview path -> glb bytes
	for name in classes:
		var c: Dictionary = classes[name]
		if c.has("_preview"):
			var rel := "%s/%s.glb" % [PREVIEWS, name]
			previews[rel] = c["_preview"]
			c.erase("_preview")
			c["preview"] = rel
	var text := JSON.stringify({"version": VERSION, "generator": "chip_import", "classes": classes}, "  ", false) + "\n"

	var root := ProjectSettings.globalize_path(out_dir)
	DirAccess.make_dir_recursive_absolute(root)
	var changed := _write_if_changed(root.path_join(".gdignore"), PackedByteArray())
	for rel in previews:
		DirAccess.make_dir_recursive_absolute(root.path_join(rel).get_base_dir())
		changed = _write_if_changed(root.path_join(rel), previews[rel]) or changed
	changed = _remove_stale(root.path_join(PREVIEWS), root, previews) or changed
	# The manifest goes last, so a reader that sees it changed also sees the previews it names.
	changed = _write_if_changed(root.path_join("entities.json"), text.to_utf8_buffer()) or changed
	return {"classes": classes.size(), "changed": changed, "warnings": warnings, "path": root.path_join("entities.json")}


## The addon's own classes as manifest JSON, without previews: Chip embeds this as the classes it offers when a level
## isn't in a Godot project (src/Chip.Core/Scene/BuiltinEntities.json).
## Native properties' hints vary a little between Godot versions, so it records the one that wrote it.
static func builtins_json() -> String:
	var classes := build(PackedStringArray(), false)
	for name in classes:
		classes[name].erase("_preview")
	var godot := "%d.%d" % [Engine.get_version_info()["major"], Engine.get_version_info()["minor"]]
	return JSON.stringify({"version": VERSION, "generator": "chip_import", "godot": godot, "classes": classes}, "  ", false) + "\n"


## Class name -> description. Previews are returned as bytes under "_preview".
static func build(warnings: PackedStringArray, project := true) -> Dictionary:
	var classes := {}
	_scan(BUILTIN_DIR, BUILTIN_DIR, classes, warnings)
	for folder in (folders() if project else PackedStringArray()):
		if folder.simplify_path() == BUILTIN_DIR:
			continue
		if not DirAccess.dir_exists_absolute(folder):
			continue
		_scan(folder, folder, classes, warnings)
	var sorted := {}
	var names := classes.keys()
	names.sort()
	for n in names:
		sorted[n] = classes[n]
	return sorted


static func _scan(dir: String, root: String, classes: Dictionary, warnings: PackedStringArray) -> void:
	var d := DirAccess.open(dir)
	if d == null:
		return
	for sub in d.get_directories():
		if not _skipped(sub):
			_scan(dir.path_join(sub), root, classes, warnings)
	for file in d.get_files():
		if _skipped(file) or not file.get_extension() in ["tscn", "scn"]:
			continue
		var path := dir.path_join(file)
		var name := path.trim_prefix(root.trim_suffix("/") + "/").get_basename()
		var c := describe(path, name, warnings)
		if not c.is_empty():
			classes[name] = c


static func _skipped(file: String) -> bool:
	return file.begins_with("_") or file.begins_with(".")


## One class: { scene, uid?, label, base, doc?, bounds?, props: [...] } (+ "_preview" bytes).
static func describe(path: String, name: String, warnings: PackedStringArray) -> Dictionary:
	var packed := load(path) as PackedScene
	if packed == null:
		warnings.append("Chip entities: %s isn't a scene Godot can load; skipped" % path)
		return {}
	var node := packed.instantiate()
	if node == null:
		warnings.append("Chip entities: %s can't be instantiated; skipped" % path)
		return {}
	if not node is Node3D:
		warnings.append("Chip entities: %s has a %s root; entity scenes need a Node3D root. Skipped" % [path, node.get_class()])
		node.free()
		return {}

	var c := {"scene": path, "label": _label(name), "base": node.get_class()}
	var uid := ResourceLoader.get_resource_uid(path)
	if uid != ResourceUID.INVALID_ID:
		c["uid"] = ResourceUID.id_to_text(uid)
	var script := node.get_script() as Script
	var docs := _docs(script)
	if docs.get("", "") != "":
		c["doc"] = docs[""]

	var expose := PackedStringArray(node.get_meta("chip_expose", PackedStringArray()))
	var props := []
	var group := ""
	for p in node.get_property_list():
		var usage: int = p["usage"]
		if usage & PROPERTY_USAGE_CATEGORY:
			group = ""
			continue
		if usage & PROPERTY_USAGE_GROUP:
			group = str(p["name"])
			continue
		var exported := (usage & PROPERTY_USAGE_SCRIPT_VARIABLE) and (usage & PROPERTY_USAGE_EDITOR)
		if not exported and not str(p["name"]) in expose:
			continue
		var spec := _prop(p, node.get(p["name"]))
		if docs.has(p["name"]):
			spec["doc"] = docs[p["name"]]
		if group != "":
			spec["group"] = group
		props.append(spec)
	for e in expose:
		if not props.any(func(s): return s["name"] == e):
			warnings.append("Chip entities: %s exposes %s, which a %s doesn't have" % [path, e, node.get_class()])
	c["props"] = props

	var box = node.get_meta("chip_bounds") if node.has_meta("chip_bounds") else _visual_bounds(node, Transform3D.IDENTITY)
	if box is AABB and box.size != Vector3.ZERO:
		c["bounds"] = [_vec(box.position), _vec(box.end)]
	if _has_mesh(node):
		var doc := GLTFDocument.new()
		var state := GLTFState.new()
		if doc.append_from_scene(node, state) == OK:
			var bytes := doc.generate_buffer(state)
			if not bytes.is_empty():
				c["_preview"] = bytes
	node.free()
	return c


## A property's spec: { name, type, default, ... }. Types: bool, int, float, string, enum, color, vector2, vector3, node,
## resource, unsupported.
static func _prop(p: Dictionary, value: Variant) -> Dictionary:
	var spec := {"name": str(p["name"])}
	var hint: int = p["hint"]
	var hs: String = p["hint_string"]
	match p["type"]:
		TYPE_BOOL:
			spec["type"] = "bool"
			spec["default"] = bool(value)
		TYPE_INT:
			if hint == PROPERTY_HINT_ENUM:
				spec["type"] = "enum"
				spec["options"] = _enum_options(hs, true)
			else:
				spec["type"] = "int"
				_range(spec, hint, hs)
			spec["default"] = int(value)
		TYPE_FLOAT:
			spec["type"] = "float"
			_range(spec, hint, hs)
			spec["default"] = _num(value)
		TYPE_STRING, TYPE_STRING_NAME:
			if hint == PROPERTY_HINT_ENUM or hint == PROPERTY_HINT_ENUM_SUGGESTION:
				spec["type"] = "enum"
				spec["options"] = _enum_options(hs, false)
			else:
				spec["type"] = "string"
				if hint == PROPERTY_HINT_FILE or hint == PROPERTY_HINT_GLOBAL_FILE:
					spec["file"] = hs
				elif hint == PROPERTY_HINT_MULTILINE_TEXT:
					spec["multiline"] = true
			spec["default"] = str(value)
		TYPE_COLOR:
			spec["type"] = "color"
			var col: Color = value
			spec["default"] = [_num(col.r), _num(col.g), _num(col.b), _num(col.a)]
		TYPE_VECTOR2, TYPE_VECTOR2I:
			spec["type"] = "vector2"
			spec["default"] = [_num(value.x), _num(value.y)]
		TYPE_VECTOR3, TYPE_VECTOR3I:
			spec["type"] = "vector3"
			spec["default"] = _vec(Vector3(value))
		TYPE_NODE_PATH:
			spec["type"] = "node"
			spec["default"] = null
		TYPE_OBJECT:
			if hint == PROPERTY_HINT_NODE_TYPE:
				spec["type"] = "node"
				if hs != "":
					spec["class"] = hs
				spec["default"] = null
			elif hint == PROPERTY_HINT_RESOURCE_TYPE:
				spec["type"] = "resource"
				spec["class"] = hs
				spec["default"] = value.resource_path if value is Resource and value.resource_path != "" else null
			else:
				spec["type"] = "unsupported"
				spec["godot"] = "Object"
		var other:
			spec["type"] = "unsupported"
			spec["godot"] = type_string(other)
	if p["type"] in [TYPE_VECTOR2I, TYPE_VECTOR3I]:
		spec["integer"] = true
	return spec


## "Swing,Slide:5,Fly" -> [{label, value}]; ints count up from the last explicit value, strings are their own value.
static func _enum_options(hint_string: String, ints: bool) -> Array:
	var out := []
	var next := 0
	for part in hint_string.split(","):
		var label := part.strip_edges()
		if label.is_empty():
			continue
		if ints:
			var colon := label.rfind(":")
			if colon >= 0 and label.substr(colon + 1).is_valid_int():
				next = label.substr(colon + 1).to_int()
				label = label.substr(0, colon)
			out.append({"label": label, "value": next})
			next += 1
		else:
			out.append({"label": label, "value": label})
	return out


## PROPERTY_HINT_RANGE "0,180,1,or_greater,suffix:m" -> range [min, max, step], or_greater, or_less, suffix.
static func _range(spec: Dictionary, hint: int, hint_string: String) -> void:
	if hint != PROPERTY_HINT_RANGE:
		return
	var parts := hint_string.split(",")
	if parts.size() < 2 or not parts[0].strip_edges().is_valid_float() or not parts[1].strip_edges().is_valid_float():
		return
	var step := 0.0
	if parts.size() > 2 and parts[2].strip_edges().is_valid_float():
		step = parts[2].strip_edges().to_float()
	spec["range"] = [_num(parts[0].strip_edges().to_float()), _num(parts[1].strip_edges().to_float()), _num(step)]
	for extra in parts.slice(2):
		var e := extra.strip_edges()
		if e == "or_greater":
			spec["or_greater"] = true
		elif e == "or_less":
			spec["or_less"] = true
		elif e.begins_with("suffix:"):
			spec["suffix"] = e.trim_prefix("suffix:")


## "##" comments: "" -> the class doc (the first block, ended by a blank line or the first member), name -> the comment
## block right above that variable.
static func _docs(script: Script) -> Dictionary:
	var out := {}
	if script == null or script.source_code.is_empty():
		return out
	var var_re := RegEx.create_from_string("(?:^|\\s)var\\s+([A-Za-z_]\\w*)")
	var pending := PackedStringArray()
	var members := false
	for raw in script.source_code.split("\n"):
		var line := raw.strip_edges()
		if line.begins_with("##"):
			pending.append(line.substr(2).strip_edges())
			continue
		if line.is_empty():
			if not members and not pending.is_empty() and not out.has(""):
				out[""] = _join(pending)
			pending.clear()
			continue
		if line.begins_with("#") or line.begins_with("extends") or line.begins_with("class_name") or line.begins_with("@tool") or line.begins_with("@icon"):
			continue
		var m := var_re.search(line)
		if m != null:
			members = true
			if not pending.is_empty():
				out[m.get_string(1)] = _join(pending)
			pending.clear()
		elif line.begins_with("@") and not line.begins_with("@export_group") and not line.begins_with("@export_subgroup") and not line.begins_with("@export_category"):
			continue  # an annotation on its own line belongs to the var below
		else:
			if line.begins_with("func") or line.begins_with("const") or line.begins_with("signal") or line.begins_with("enum"):
				members = true
			pending.clear()
	return out


## "enemies/big_grunt" -> "Big grunt" (sentence case, like Chip's labels).
static func _label(name: String) -> String:
	var words := name.get_file().capitalize().to_lower()
	return words.substr(0, 1).to_upper() + words.substr(1)


## Comment lines joined into prose, keeping the structure Chip shows: a blank "##" line starts a paragraph, list items
## ("- ", "* ", "1. ") and Godot's [br] start a line.
static func _join(lines: PackedStringArray) -> String:
	var out := ""
	var list_item := RegEx.create_from_string("^([-*•]|\\d+[.)])\\s")
	for line in lines:
		if line.is_empty():
			out = out.strip_edges(false, true) + "\n"
		elif out.is_empty() or out.ends_with("\n"):
			out += line
		elif list_item.search(line) != null:
			out += "\n" + line
		else:
			out += " " + line
	return out.replace("[br]", "\n").strip_edges()


static func _visual_bounds(node: Node, xform: Transform3D) -> Variant:
	var box = null
	if node is GeometryInstance3D:
		var b: AABB = xform * (node as GeometryInstance3D).get_aabb()
		if b.size != Vector3.ZERO:
			box = b
	for child in node.get_children():
		var child_box = _visual_bounds(child, xform * child.transform if child is Node3D else xform)
		if child_box != null:
			box = child_box if box == null else box.merge(child_box)
	return box


static func _has_mesh(node: Node) -> bool:
	if node is MeshInstance3D and node.mesh != null:
		return true
	for child in node.get_children():
		if _has_mesh(child):
			return true
	return false


static func _vec(v: Vector3) -> Array:
	return [_num(v.x), _num(v.y), _num(v.z)]


## Floats rounded to 6 decimals, so float32 noise (0.800000011920929) reads as 0.8 and doesn't churn the file.
static func _num(x: float) -> float:
	return ("%.6f" % x).to_float()


static func _write_if_changed(path: String, bytes: PackedByteArray) -> bool:
	if FileAccess.file_exists(path) and FileAccess.get_file_as_bytes(path) == bytes:
		return false
	var f := FileAccess.open(path, FileAccess.WRITE)
	if f == null:
		push_error("Chip entities: can't write %s (%s)" % [path, error_string(FileAccess.get_open_error())])
		return false
	f.store_buffer(bytes)
	f.close()
	return true


static func _remove_stale(dir: String, root: String, keep: Dictionary) -> bool:
	var d := DirAccess.open(dir)
	if d == null:
		return false
	var removed := false
	for sub in d.get_directories():
		removed = _remove_stale(dir.path_join(sub), root, keep) or removed
	for file in d.get_files():
		var rel := dir.path_join(file).trim_prefix(root.trim_suffix("/") + "/")
		if not keep.has(rel):
			DirAccess.remove_absolute(dir.path_join(file))
			removed = true
	return removed
