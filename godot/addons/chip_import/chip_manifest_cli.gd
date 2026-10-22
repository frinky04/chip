extends SceneTree
## Writes the entity manifest without opening the editor (CI, or a project nobody has opened yet):
##   godot --headless --path <project> --script res://addons/chip_import/chip_manifest_cli.gd [-- --out <dir>]
## On a fresh checkout, run `godot --headless --path <project> --import` first so scene UIDs are known.
## `-- --builtins <file>` writes only the addon's own classes (no previews): Chip's embedded fallback catalog.

const Manifest = preload("chip_manifest.gd")


func _initialize() -> void:
	var args := OS.get_cmdline_user_args()
	var builtins := args.find("--builtins")
	if builtins >= 0 and builtins + 1 < args.size():
		var f := FileAccess.open(args[builtins + 1], FileAccess.WRITE)
		if f == null:
			printerr("Chip entities: can't write %s" % args[builtins + 1])
			quit(1)
			return
		f.store_string(Manifest.builtins_json())
		f.close()
		print("Chip entities: built-in classes written to %s" % args[builtins + 1])
		quit(0)
		return
	var out := Manifest.OUT_DIR
	var at := args.find("--out")
	if at >= 0 and at + 1 < args.size():
		out = args[at + 1]
	var result := Manifest.write(out)
	for w in result["warnings"]:
		printerr(w)
	print("Chip entities: %d classes, %s (%s)" % [result["classes"], "written" if result["changed"] else "unchanged", result["path"]])
	quit(0)
