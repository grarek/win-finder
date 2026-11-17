namespace WinFinder.Core.Tags;

/// <summary>Higher-level tagging operations (toggle like Finder, clear, bulk apply).</summary>
public sealed class TagService
{
    public TagService(TagStore store) => Store = store;

    public TagStore Store { get; }

    /// <summary>
    /// Finder behaviour: if every selected item already has the tag, remove it from all;
    /// otherwise add it to all.
    /// </summary>
    public void Toggle(IReadOnlyList<string> paths, int tagId)
    {
        if (paths.Count == 0) return;
        var current = paths.ToDictionary(p => p, p => Store.GetTagIds(p).ToList(), StringComparer.OrdinalIgnoreCase);
        bool allHave = current.Values.All(ids => ids.Contains(tagId));
        foreach (var (path, ids) in current)
        {
            if (allHave) ids.Remove(tagId);
            else if (!ids.Contains(tagId)) ids.Add(tagId);
            Store.SetTags(path, ids);
        }
    }

    /// <summary>Toggles the Nth tag in sidebar order (1-based), as used by Ctrl+Shift+1..7.</summary>
    public void ToggleByIndex(IReadOnlyList<string> paths, int index)
    {
        var tags = Store.GetTags();
        if (index < 1 || index > tags.Count) return;
        Toggle(paths, tags[index - 1].Id);
    }

    public void Clear(IReadOnlyList<string> paths)
    {
        foreach (var p in paths) Store.SetTags(p, Array.Empty<int>());
    }

    public void OnItemsRelocated(IReadOnlyList<(string OldPath, string NewPath)> moves)
    {
        foreach (var (oldPath, newPath) in moves) Store.UpdatePath(oldPath, newPath);
    }
}
