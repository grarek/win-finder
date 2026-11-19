using WinFinder.Core.Navigation;

namespace WinFinder.Core.Shell;

/// <summary>What a drop does (mirrors DROPEFFECT / DataPackageOperation).</summary>
[Flags]
public enum DropEffect { None = 0, Copy = 1, Move = 2, Link = 4 }

/// <summary>Explorer's rules for what dropping files into a folder does.</summary>
public static class DropRules
{
    /// <summary>
    /// Same drive moves, different drive copies; Ctrl forces copy, Shift forces move. The result is
    /// limited to what the drag source <paramref name="allowed"/>: browsers, for example, only offer
    /// Copy for a downloaded file. A source that reports nothing is treated as allowing Copy and Move.
    /// </summary>
    public static DropEffect Decide(IReadOnlyList<string> sources, string destination, DropEffect allowed, bool ctrl, bool shift)
    {
        if (allowed == DropEffect.None) allowed = DropEffect.Copy | DropEffect.Move;

        DropEffect wanted;
        if (ctrl) wanted = DropEffect.Copy;
        else if (shift) wanted = DropEffect.Move;
        else
        {
            string? destRoot = Path.GetPathRoot(destination);
            bool sameDrive = sources.Count > 0 &&
                sources.All(s => string.Equals(Path.GetPathRoot(s), destRoot, StringComparison.OrdinalIgnoreCase));
            wanted = sameDrive ? DropEffect.Move : DropEffect.Copy;
        }

        if (allowed.HasFlag(wanted)) return wanted;
        if (allowed.HasFlag(DropEffect.Copy)) return DropEffect.Copy;
        if (allowed.HasFlag(DropEffect.Move)) return DropEffect.Move;
        return DropEffect.None; // Link only: we don't create shortcuts on drop
    }

    /// <summary>True when the drop would be a no-op or invalid (into itself, or into its current folder for a move).</summary>
    public static bool IsInvalidTarget(IReadOnlyList<string> sources, string destination, DropEffect effect)
    {
        if (sources.Count == 0 || effect == DropEffect.None) return true;
        if (sources.Any(s => PathUtil.IsSameOrUnder(destination, s))) return true;
        if (effect == DropEffect.Move &&
            sources.All(s => string.Equals(PathUtil.Parent(s), PathUtil.Normalize(destination), StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }
}
