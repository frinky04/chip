namespace Chip.Editor.Framework;

/// <summary>
/// One user action. Menus, hotkeys, the shortcuts sheet and the command palette are all generated from these,
/// so a feature adds a command once and it shows up everywhere.
/// </summary>
public sealed class Command
{
    /// <summary>Stable dotted id, e.g. "mesh.bridge". Used by scripts, remapping and tests.</summary>
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required Action Execute { get; init; }

    /// <summary>Top-level menu (see <see cref="CommandRegistry.MenuOrder"/>), or null for hotkey/palette-only commands.</summary>
    public string? Menu { get; init; }
    /// <summary>Items with the same group sit together; groups are separated by a line. Groups sort by name.</summary>
    public string Group { get; init; } = "";
    public int Order { get; init; }
    /// <summary>Shortcuts sheet heading; defaults to <see cref="Menu"/>.</summary>
    public string? Category { get; init; }
    public string? Description { get; init; }

    public Hotkey[] Hotkeys { get; init; } = [];
    public Func<bool>? CanExecute { get; init; }
    public Func<bool>? IsChecked { get; init; }
    /// <summary>Still dispatched while a tool is mid-operation (undo, redo, cancel). Others wait until it ends.</summary>
    public bool DuringInteraction { get; init; }

    /// <summary>Optional label computed at display time, e.g. "Undo Create Block".</summary>
    public Func<string>? DisplayLabel { get; init; }

    public bool Enabled => CanExecute?.Invoke() ?? true;
    public string CurrentLabel => DisplayLabel?.Invoke() ?? Label;
    public string? HotkeyText => Hotkeys.Length > 0 ? Hotkeys[0].ToString() : null;
}

/// <summary>A binding the sheet should list that isn't a keyboard command (mouse gestures, tool keys).</summary>
public readonly record struct ShortcutReference(string Category, string Keys, string Action);

public sealed class CommandRegistry
{
    /// <summary>Menu bar order. Features may use other names; those appear after these, alphabetically.</summary>
    public static readonly string[] MenuOrder = ["File", "Edit", "Select", "Mesh", "Texture", "Tools", "View", "Help"];

    readonly Dictionary<string, Command> _byId = new();
    readonly List<Command> _all = new();
    readonly List<ShortcutReference> _references = new();

    public IReadOnlyList<Command> All => _all;
    public IReadOnlyList<ShortcutReference> References => _references;

    public Command Add(Command command)
    {
        if (!_byId.TryAdd(command.Id, command)) throw new InvalidOperationException($"Duplicate command id '{command.Id}'");
        _all.Add(command);
        return command;
    }

    /// <summary>Shorthand for the common case.</summary>
    public Command Add(string id, string label, Action execute, string? menu = null, string? hotkey = null,
        Func<bool>? canExecute = null, string group = "", int order = 0, Func<bool>? isChecked = null) =>
        Add(new Command
        {
            Id = id, Label = label, Execute = execute, Menu = menu, Group = group, Order = order,
            Hotkeys = hotkey == null ? [] : [Hotkey.Parse(hotkey)], CanExecute = canExecute, IsChecked = isChecked,
        });

    public void AddReference(string category, string keys, string action) => _references.Add(new(category, keys, action));

    public Command? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Runs a command by id if it exists and is enabled. Returns whether it ran.</summary>
    public bool Execute(string id)
    {
        if (Find(id) is not { } c || !c.Enabled) return false;
        c.Execute();
        return true;
    }

    /// <summary>The first enabled command with a pressed hotkey, honouring <see cref="Command.DuringInteraction"/>.</summary>
    public Command? Match(Func<Hotkey, bool> isPressed, bool interacting)
    {
        foreach (var c in _all)
        {
            if (interacting && !c.DuringInteraction) continue;
            foreach (var h in c.Hotkeys)
                if (isPressed(h)) return c.Enabled ? c : null;
        }
        return null;
    }

    /// <summary>Hotkeys bound to more than one command. Must be empty; enforced by tests.</summary>
    public IEnumerable<(Hotkey Hotkey, string[] Commands)> Conflicts() =>
        _all.SelectMany(c => c.Hotkeys.Select(h => (h, c.Id)))
            .GroupBy(x => x.h)
            .Where(g => g.Count() > 1)
            .Select(g => (g.Key, g.Select(x => x.Id).ToArray()));

    /// <summary>Menu names in display order.</summary>
    public IEnumerable<string> Menus() =>
        _all.Where(c => c.Menu != null).Select(c => c.Menu!).Distinct()
            .OrderBy(m => Array.IndexOf(MenuOrder, m) is var i && i >= 0 ? i : MenuOrder.Length)
            .ThenBy(m => m);

    /// <summary>A menu's commands grouped (groups in name order), each group in <see cref="Command.Order"/>.</summary>
    public IEnumerable<IGrouping<string, Command>> MenuGroups(string menu) =>
        _all.Where(c => c.Menu == menu)
            .OrderBy(c => c.Group, StringComparer.Ordinal).ThenBy(c => c.Order)
            .GroupBy(c => c.Group);
}
