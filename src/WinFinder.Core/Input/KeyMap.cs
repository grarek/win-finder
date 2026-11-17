namespace WinFinder.Core.Input;

[Flags]
public enum KeyModifiers { None = 0, Ctrl = 1, Shift = 2, Alt = 4 }

/// <summary>A key (Win32 virtual-key code) plus modifiers.</summary>
public readonly record struct KeyChord(int Key, KeyModifiers Modifiers);

public enum FinderCommand
{
    Open, Rename, Recycle, DeletePermanently, QuickLook,
    ViewIcons, ViewList, ViewColumns,
    Back, Forward, Parent,
    GoToFolder, GetInfo, ToggleHidden, NewFolder, AddToFavorites,
    ClearTags, Tag1, Tag2, Tag3, Tag4, Tag5, Tag6, Tag7,
    NewTab, CloseTab, NewWindow, NextTab, PreviousTab, ReopenClosedTab,
    JumpTab1, JumpTab2, JumpTab3, JumpTab4, JumpTab5, JumpTab6, JumpTab7, JumpTab8, JumpTab9,
    Undo, Copy, Cut, Paste, SelectAll, Refresh, Find,
}

/// <summary>
/// Explorer's keys are kept; Finder's extras are layered on where they don't conflict.
/// Color tags use Ctrl+Shift+0..7 because plain Ctrl+1..3 switch views.
/// </summary>
public static class KeyMap
{
    // Win32 virtual-key codes
    public const int VkBack = 0x08, VkTab = 0x09, VkEnter = 0x0D, VkEscape = 0x1B, VkSpace = 0x20,
        VkLeft = 0x25, VkUp = 0x26, VkRight = 0x27, VkDown = 0x28, VkDelete = 0x2E,
        Vk0 = 0x30, VkA = 0x41, VkC = 0x43, VkD = 0x44, VkE = 0x45, VkF = 0x46, VkG = 0x47, VkI = 0x49, VkL = 0x4C,
        VkN = 0x4E, VkR = 0x52, VkT = 0x54, VkV = 0x56, VkW = 0x57, VkX = 0x58, VkZ = 0x5A,
        VkF2 = 0x71, VkF5 = 0x74, VkOemPeriod = 0xBE, VkOemOpenBracket = 0xDB, VkOemCloseBracket = 0xDD,
        VkBrowserBack = 0xA6, VkBrowserForward = 0xA7;

    private static readonly Dictionary<KeyChord, FinderCommand> Map = Build();

    /// <summary>Commands that still apply while a text box (search, rename) has focus.</summary>
    private static readonly HashSet<FinderCommand> TextSafe =
    [
        FinderCommand.NewTab, FinderCommand.CloseTab, FinderCommand.NewWindow,
        FinderCommand.NextTab, FinderCommand.PreviousTab, FinderCommand.ReopenClosedTab,
        FinderCommand.JumpTab1, FinderCommand.JumpTab2, FinderCommand.JumpTab3, FinderCommand.JumpTab4,
        FinderCommand.JumpTab5, FinderCommand.JumpTab6, FinderCommand.JumpTab7, FinderCommand.JumpTab8, FinderCommand.JumpTab9,
        FinderCommand.GoToFolder, FinderCommand.ToggleHidden, FinderCommand.Find, FinderCommand.Refresh,
        FinderCommand.Forward,
        FinderCommand.ViewIcons, FinderCommand.ViewList, FinderCommand.ViewColumns,
    ];

    public static IReadOnlyDictionary<KeyChord, FinderCommand> All => Map;

    public static FinderCommand? Resolve(KeyChord chord) =>
        Map.TryGetValue(chord, out var cmd) ? cmd : null;

    public static FinderCommand? Resolve(int key, KeyModifiers mods) => Resolve(new KeyChord(key, mods));

    public static bool AppliesWhileEditingText(FinderCommand command) => TextSafe.Contains(command);

    private static Dictionary<KeyChord, FinderCommand> Build()
    {
        const KeyModifiers C = KeyModifiers.Ctrl, S = KeyModifiers.Shift, A = KeyModifiers.Alt, N = KeyModifiers.None;
        var m = new Dictionary<KeyChord, FinderCommand>
        {
            [new(VkEnter, N)] = FinderCommand.Open,
            [new(VkDown, C)] = FinderCommand.Open,
            [new(VkF2, N)] = FinderCommand.Rename,
            [new(VkDelete, N)] = FinderCommand.Recycle,
            [new(VkBack, C)] = FinderCommand.Recycle,
            [new(VkDelete, S)] = FinderCommand.DeletePermanently,
            [new(VkSpace, N)] = FinderCommand.QuickLook,

            [new(Vk0 + 1, C)] = FinderCommand.ViewIcons,
            [new(Vk0 + 2, C)] = FinderCommand.ViewList,
            [new(Vk0 + 3, C)] = FinderCommand.ViewColumns,

            [new(VkLeft, A)] = FinderCommand.Back,
            [new(VkRight, A)] = FinderCommand.Forward,
            [new(VkOemOpenBracket, C)] = FinderCommand.Back,
            [new(VkOemCloseBracket, C)] = FinderCommand.Forward,
            [new(VkBack, N)] = FinderCommand.Back,
            [new(VkBrowserBack, N)] = FinderCommand.Back,
            [new(VkBrowserForward, N)] = FinderCommand.Forward,
            [new(VkUp, A)] = FinderCommand.Parent,
            [new(VkUp, C)] = FinderCommand.Parent,

            [new(VkG, C | S)] = FinderCommand.GoToFolder,
            [new(VkL, C)] = FinderCommand.GoToFolder,
            [new(VkI, C)] = FinderCommand.GetInfo,
            [new(VkEnter, A)] = FinderCommand.GetInfo,
            [new(VkOemPeriod, C | S)] = FinderCommand.ToggleHidden,
            [new(VkN, C | S)] = FinderCommand.NewFolder,
            [new(VkD, C)] = FinderCommand.AddToFavorites,

            [new(Vk0, C | S)] = FinderCommand.ClearTags,

            [new(VkT, C)] = FinderCommand.NewTab,
            [new(VkW, C)] = FinderCommand.CloseTab,
            [new(VkN, C)] = FinderCommand.NewWindow,
            [new(VkTab, C)] = FinderCommand.NextTab,
            [new(VkTab, C | S)] = FinderCommand.PreviousTab,
            [new(VkT, C | S)] = FinderCommand.ReopenClosedTab,

            [new(VkZ, C)] = FinderCommand.Undo,
            [new(VkC, C)] = FinderCommand.Copy,
            [new(VkX, C)] = FinderCommand.Cut,
            [new(VkV, C)] = FinderCommand.Paste,
            [new(VkA, C)] = FinderCommand.SelectAll,
            [new(VkF5, N)] = FinderCommand.Refresh,
            [new(VkR, C)] = FinderCommand.Refresh,
            [new(VkF, C)] = FinderCommand.Find,
            [new(VkE, C)] = FinderCommand.Find,
        };

        for (int i = 1; i <= 7; i++)
            m[new(Vk0 + i, C | S)] = FinderCommand.Tag1 + (i - 1);
        for (int i = 1; i <= 9; i++)
            m[new(Vk0 + i, C | A)] = FinderCommand.JumpTab1 + (i - 1);
        return m;
    }
}
