extends SceneTree
## Tests for the Chip import addon. Run through scripts/godot-test.ps1, or:
##   godot --headless --path godot --script res://tests/test_chip_import.gd [-- --glb <level.glb>] [--mesh-glb <level.glb>]
## With --glb, also imports that file (a Chip export of tests/fixtures/entities.chip) end to end; with --mesh-glb, a
## Chip export of tests/fixtures/mesh_entities.chip (a mesh entity and a trigger).

const Manifest = preload("res://addons/chip_import/chip_manifest.gd")
const Processor = preload("res://addons/chip_import/chip_processor.gd")
const OUT := "res://tests/out"

var _failures := 0
var _checks := 0


func _initialize() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT))
	test_manifest()
	test_manifest_is_stable()
	test_builtins_match_chip()
	test_processing()
	var args := OS.get_cmdline_user_args()
	var at := args.find("--glb")
	if at >= 0 and at + 1 < args.size():
		test_glb(args[at + 1])
	at = args.find("--mesh-glb")
	if at >= 0 and at + 1 < args.size():
		test_mesh_glb(args[at + 1])
	print("%d checks, %d failed" % [_checks, _failures])
	quit(1 if _failures > 0 else 0)


func check(ok: bool, what: String) -> void:
	_checks += 1
	if not ok:
		_failures += 1
		printerr("FAIL: " + what)


func eq(actual: Variant, expected: Variant, what: String) -> void:
	check(actual == expected, "%s: expected %s, got %s" % [what, var_to_str(expected), var_to_str(actual)])


func prop(cls: Dictionary, name: String) -> Dictionary:
	for p in cls["props"]:
		if p["name"] == name:
			return p
	return {}


func test_manifest() -> void:
	var warnings := PackedStringArray()
	var classes := Manifest.build(warnings)
	eq(warnings.size(), 0, "manifest warnings " + str(warnings))
	eq(classes.keys(), ["door", "enemies/grunt", "info_player_start", "info_target", "light_point", "light_spot", "platform"], "class names (helpers under _parts skipped)")
	eq(classes["platform"].get("mesh", false), true, "a body root is a mesh class")
	check(not classes["door"].has("mesh"), "a Node3D root is a point class")

	var door: Dictionary = classes["door"]
	eq(door["uid"], "uid://dlm1osw5eeb10", "door uid")
	eq(door["label"], "Door", "door label")
	eq(door["base"], "Node3D", "door base")
	check(str(door.get("doc", "")).begins_with("A door that swings"), "door class doc")
	eq(door["props"].map(func(p): return p["name"]), ["mode", "open_angle", "locked", "tint", "keys_needed", "partner", "skin", "volume", "sound_offset", "tags", "cells", "linked", "weights", "notes"], "door property order")
	eq(prop(door, "mode")["type"], "enum", "string enum type")
	eq(prop(door, "mode")["options"], [{"label": "swing", "value": "swing"}, {"label": "slide", "value": "slide"}], "string enum options")
	eq(prop(door, "mode")["doc"], "How the door opens.", "property doc")
	eq(prop(door, "open_angle")["default"], 100.0, "default from the scene, not the script")
	eq(prop(door, "open_angle")["range"], [0.0, 180.0, 1.0], "range hint")
	eq(prop(door, "open_angle")["or_greater"], true, "or_greater")
	eq(prop(door, "tint")["default"], [0.8, 0.5, 0.2, 1.0], "colour default")
	eq(prop(door, "partner")["type"], "node", "node type")
	eq(prop(door, "partner")["class"], "Node3D", "node class")
	eq(prop(door, "skin")["type"], "resource", "resource type")
	eq(prop(door, "skin")["class"], "Material", "resource class")
	eq(prop(door, "volume")["group"], "Sound", "export group")
	eq(prop(door, "sound_offset")["default"], [0.0, 1.0, 0.0], "vector default")
	eq(prop(door, "tags")["type"], "array", "typed array")
	eq(prop(door, "tags")["of"], "string", "StringName items")
	eq(prop(door, "tags")["default"], ["wood"], "array default")
	eq(prop(door, "tags")["group"], "Lists", "array group")
	eq(prop(door, "cells")["of"], "vector2", "Vector2i items")
	eq(prop(door, "cells")["integer"], true, "Vector2i items are whole")
	eq(prop(door, "linked")["of"], "node", "node items")
	eq(prop(door, "linked")["class"], "Node3D", "node item class")
	eq(prop(door, "linked")["default"], [], "node lists start empty")
	eq(prop(door, "weights")["type"], "array", "packed array")
	eq(prop(door, "weights")["of"], "float", "packed float items")
	eq(prop(door, "weights")["default"], [0.5], "packed default")
	eq(prop(door, "notes")["type"], "unsupported", "dictionaries are unsupported")
	eq(door["bounds"], [[-0.5, 0.0, -0.05], [0.5, 2.0, 0.05]], "bounds from meshes")
	check(door.has("_preview") and door["_preview"].size() > 0, "door has a preview")

	var grunt: Dictionary = classes["enemies/grunt"]
	eq(grunt["doc"], "A basic enemy that patrols towards a target. Its mood:\n- Calm: wanders\n- Alert: searches\nand calls for help", "doc keeps list items and [br] as lines")
	eq(prop(grunt, "mood")["options"], [{"label": "Calm", "value": 0}, {"label": "Alert", "value": 5}, {"label": "Angry", "value": 6}], "int enum options")
	eq(prop(grunt, "patrol_to")["type"], "node", "NodePath is a node reference")
	eq(grunt["bounds"], [[-0.4, 0.0, -0.4], [0.4, 1.8, 0.4]], "chip_bounds metadata")
	check(not grunt.has("_preview"), "no preview without meshes")

	var light: Dictionary = classes["light_point"]
	eq(light["props"].map(func(p): return p["name"]), ["light_color", "light_energy", "shadow_enabled", "omni_range"], "exposed native properties")
	eq(prop(light, "omni_range")["default"], 5.0, "native default")


func test_builtins_match_chip() -> void:
	var embedded := ProjectSettings.globalize_path("res://").path_join("../src/Chip.Core/Scene/BuiltinEntities.json").simplify_path()
	check(FileAccess.file_exists(embedded), "Chip's embedded built-in classes exist at " + embedded)
	var text := FileAccess.get_file_as_string(embedded).replace("\r\n", "\n")
	var made_with := str(JSON.parse_string(text).get("godot", "")) if JSON.parse_string(text) is Dictionary else ""
	var here := "%d.%d" % [Engine.get_version_info()["major"], Engine.get_version_info()["minor"]]
	if made_with != here:
		print("Note: BuiltinEntities.json was made with Godot %s, this is %s; its exact match is only checked on %s" % [made_with, here, made_with])
		return
	eq(text, Manifest.builtins_json(),
		"Chip's embedded built-in classes match the addon; regenerate them with scripts/godot-test.ps1 -UpdateBuiltins")


func test_manifest_is_stable() -> void:
	var out := OUT.path_join("manifest")
	Manifest.write(out)
	var again := Manifest.write(out)
	eq(again["changed"], false, "rewriting an unchanged project changes nothing")
	var text := FileAccess.get_file_as_string(ProjectSettings.globalize_path(out).path_join("entities.json"))
	var parsed = JSON.parse_string(text)
	check(parsed is Dictionary and parsed["version"] == 1, "manifest parses")
	eq(parsed["classes"]["door"]["preview"], "previews/door.glb", "preview path")
	check(FileAccess.file_exists(ProjectSettings.globalize_path(out).path_join("previews/door.glb")), "preview written")
	check(FileAccess.file_exists(ProjectSettings.globalize_path(out).path_join(".gdignore")), ".gdignore written")


## An entity node as Godot's glTF importer leaves it: an empty Node3D with the extras as metadata.
func entity(root: Node, name: String, extras: Dictionary, at := Vector3.ZERO) -> void:
	var n := Node3D.new()
	n.name = name
	n.position = at
	n.set_meta("extras", extras)
	root.add_child(n)
	n.owner = root


func test_processing() -> void:
	var root := Node3D.new()
	root.name = "level"
	entity(root, "front_door", {"chip_class": "door", "chip_id": 1, "chip_uid": "uid://dlm1osw5eeb10", "chip_scene": "res://entities/door.tscn",
		"properties": {"mode": "slide", "open_angle": 120.0, "tint": [1.0, 0.0, 0.0, 1.0], "keys_needed": 3.0, "partner": {"entity": 2},
			"sound_offset": [0.0, 2.0, 0.0], "locked": true, "bogus": 1.0,
			"tags": ["oak", "heavy"], "cells": [[1.0, 2.0], [3.0, 4.0]], "linked": [{"entity": 2}, {"entity": 99}, null], "weights": [1.0, 2.5]}}, Vector3(1, 0, 0))
	entity(root, "back_door", {"chip_class": "door", "chip_id": 2, "chip_scene": "res://entities/door.tscn", "properties": {}})
	entity(root, "grunt_3", {"chip_class": "enemies/grunt", "chip_id": 3, "chip_uid": "uid://bvf8iabb2j2a",
		"properties": {"mood": 5.0, "target": {"entity": 1}, "patrol_to": {"entity": 2}, "health": 40.0}})
	entity(root, "ghost_4", {"chip_class": "ghost", "chip_id": 4, "chip_scene": "res://entities/ghost.tscn", "properties": {"x": 1.0}})
	entity(root, "light_point_5", {"chip_class": "light_point", "chip_id": 5, "chip_uid": "uid://bispswiantj25",
		"properties": {"light_color": [1.0, 0.5, 0.0, 1.0], "omni_range": 8.0}})
	entity(root, "info_player_start_6", {"chip_class": "info_player_start", "chip_id": 6, "chip_uid": "uid://ds5u7j0w1mcxu", "properties": {}})
	# A mesh entity: its meshes are the entity node's children (one drawn and solid, one an unseen convex collider).
	entity(root, "lift", {"chip_class": "platform", "chip_id": 7, "chip_scene": "res://entities/platform.tscn",
		"properties": {"travel": [0.0, 4.0, 0.0]}}, Vector3(5, 0, 0))
	var lift_node := root.get_node("lift")
	for spec in [["deck", {"chip_mesh": "deck", "chip_id": 8, "chip_collider": "trimesh"}], ["deck_hull", {"chip_id": 9, "chip_collider": "convex", "chip_render": false}]]:
		var mi := MeshInstance3D.new()
		mi.name = spec[0]
		mi.mesh = BoxMesh.new()
		mi.set_meta("extras", spec[1])
		lift_node.add_child(mi)
		mi.owner = root
	# A game's volume surface, as Godot imports a -colonly node.
	var water := StaticBody3D.new()
	water.name = "pool_water"
	water.set_meta("extras", {"chip_surface": "water", "chip_volume": true, "chip_id": 10})
	root.add_child(water)
	water.owner = root
	Processor.process(root)
	Processor.process(root)  # idempotent

	var lift := root.get_node("lift")
	check(lift is AnimatableBody3D, "mesh entity is an instance of its body scene")
	eq(lift.position, Vector3(5, 0, 0), "mesh entity keeps its origin")
	eq(lift.travel, Vector3(0, 4, 0), "mesh entity values applied")
	eq(lift.get_children().filter(func(c): return c is CollisionShape3D).size(), 2, "both colliders became shapes on the body")
	eq(lift.get_children().filter(func(c): return c is MeshInstance3D).map(func(c): return String(c.name)), ["deck"], "the drawn mesh moved under the instance, the unseen one didn't")
	var area := root.get_node("pool_water")
	check(area is Area3D, "a volume surface becomes an Area3D")
	eq(area.get_meta("chip_surface", ""), "water", "the area says which surface it is")

	var door := root.get_node("front_door")
	eq(door.scene_file_path, "res://entities/door.tscn", "door is an instance of its scene")
	eq(door.position, Vector3(1, 0, 0), "transform kept")
	eq(door.get_index(), 0, "order kept")
	eq(door.mode, "slide", "string enum applied")
	eq(door.open_angle, 120.0, "float applied")
	eq(door.tint, Color(1, 0, 0, 1), "colour applied")
	eq(door.keys_needed, 3, "int applied")
	check(typeof(door.keys_needed) == TYPE_INT, "int stays an int")
	eq(door.sound_offset, Vector3(0, 2, 0), "vector applied")
	eq(door.locked, true, "bool applied")
	eq(door.partner, root.get_node("back_door"), "node reference resolved")
	eq(door.tags, [&"oak", &"heavy"] as Array[StringName], "StringName list applied")
	eq(door.tags.get_typed_builtin(), TYPE_STRING_NAME, "list keeps its type")
	eq(door.cells, [Vector2i(1, 2), Vector2i(3, 4)] as Array[Vector2i], "Vector2i list applied")
	eq(door.linked, [root.get_node("back_door")] as Array[Node3D], "reference list resolved, missing and unset items dropped")
	eq(door.weights, PackedFloat32Array([1.0, 2.5]), "packed list applied")
	eq(root.get_node("back_door").tags, [&"wood"] as Array[StringName], "unset lists keep the scene's value")
	eq(door.get_meta("chip_id"), 1, "chip_id metadata")
	check(door.is_in_group(Processor.ENTITY_GROUP), "in chip_entities")
	eq(root.get_node("back_door").open_angle, 100.0, "unset properties keep the scene's value")

	var grunt := root.get_node("grunt_3")
	eq(grunt.mood, 5, "int enum applied")
	eq(grunt.health, 40, "int applied")
	eq(grunt.target, door, "Node3D reference")
	eq(grunt.patrol_to, NodePath("../back_door"), "NodePath reference")

	var ghost := root.get_node("ghost_4")
	check(ghost is Marker3D, "missing scene becomes a Marker3D")
	eq(ghost.get_meta("chip_properties"), {"x": 1.0}, "missing scene keeps its data")

	var light := root.get_node("light_point_5")
	check(light is OmniLight3D, "built-in light scene")
	eq(light.light_color, Color(1, 0.5, 0, 1), "light colour")
	eq(light.omni_range, 8.0, "light range")
	check(root.get_node("info_player_start_6").is_in_group(&"chip_spawn"), "player start carries the spawn group from its scene")

	# Saved and reloaded, entities stay linked instances and references survive.
	var packed := PackedScene.new()
	eq(packed.pack(root), OK, "pack")
	var path := OUT.path_join("processed.tscn")
	eq(ResourceSaver.save(packed, path), OK, "save")
	var text := FileAccess.get_file_as_string(path)
	check(RegEx.create_from_string("\\[node name=\"front_door\"[^\\n]*instance=ExtResource").search(text) != null, "door saved as a linked instance")
	check(not text.contains("name=\"Panel\""), "the door's own nodes aren't copied into the level")
	var loaded := (ResourceLoader.load(path, "", ResourceLoader.CACHE_MODE_IGNORE) as PackedScene).instantiate()
	var door2 := loaded.get_node("front_door")
	check(door2.has_node("Panel"), "reloaded instance has the scene's nodes")
	eq(door2.open_angle, 120.0, "override survives a reload")
	eq(door2.partner, loaded.get_node("back_door"), "reference survives a reload")
	eq(door2.tags, [&"oak", &"heavy"] as Array[StringName], "list survives a reload")
	eq(door2.linked, [loaded.get_node("back_door")] as Array[Node3D], "reference list survives a reload")
	eq(loaded.get_node("grunt_3").target, door2, "Node3D reference survives a reload")
	loaded.free()
	root.free()


## A real Chip export, loaded the way the editor imports it (extras become metadata), then processed.
func test_glb(path: String) -> void:
	var doc := GLTFDocument.new()
	var state := GLTFState.new()
	var err := doc.append_from_file(path, state)
	eq(err, OK, "load " + path)
	if err != OK:
		return
	var root := doc.generate_scene(state)
	Processor.process(root)
	var door := _find(root, func(n): return n.has_meta("chip_class") and n.get_meta("chip_class") == "door")
	check(door != null, "glb: door entity")
	if door != null:
		eq(door.scene_file_path, "res://entities/door.tscn", "glb: door instance")
		eq(door.mode, "slide", "glb: enum")
		eq(door.tint, Color(1, 0, 0, 1), "glb: colour")
		eq(door.keys_needed, 2, "glb: int")
		check(door.partner != null and door.partner.get_meta("chip_class") == "light_point", "glb: reference")
		eq(door.tags, [&"oak", &"heavy"] as Array[StringName], "glb: list")
		eq(door.linked, [door.partner] as Array[Node3D], "glb: reference list")
	var light := _find(root, func(n): return n is OmniLight3D)
	check(light != null, "glb: light")
	if light != null:
		eq(light.light_color, Color(1, 0.5, 0, 1), "glb: light colour")
		eq(light.omni_range, 8.0, "glb: light range")
	var spawn := _find(root, func(n): return n.is_in_group(&"chip_spawn"))
	check(spawn != null and spawn.position.is_equal_approx(Vector3(0, 0, 4)), "glb: player start in place")
	root.free()


## A Chip export of tests/fixtures/mesh_entities.chip: a platform tied to a mesh, and a trigger box.
func test_mesh_glb(path: String) -> void:
	var doc := GLTFDocument.new()
	var state := GLTFState.new()
	var err := doc.append_from_file(path, state)
	eq(err, OK, "load " + path)
	if err != OK:
		return
	var root := doc.generate_scene(state)
	Processor.process(root)
	var lift := _find(root, func(n): return n.has_meta("chip_class") and n.get_meta("chip_class") == "platform")
	check(lift is AnimatableBody3D, "mesh glb: the platform is its body scene")
	if lift != null:
		eq(lift.travel, Vector3(0, 4, 0), "mesh glb: values")
		check(lift.get_children().any(func(c): return c is CollisionShape3D and c.shape is ConcavePolygonShape3D), "mesh glb: its mesh collides as part of it")
		check(lift.get_children().any(func(c): return c is MeshInstance3D or c is ImporterMeshInstance3D), "mesh glb: its mesh draws under it")
	var area := _find(root, func(n): return n is Area3D)
	check(area != null and area.get_meta("chip_surface", "") == "trigger", "mesh glb: the trigger is an Area3D")
	root.free()


func _find(node: Node, pred: Callable) -> Node:
	if pred.call(node):
		return node
	for child in node.get_children():
		var hit := _find(child, pred)
		if hit != null:
			return hit
	return null
