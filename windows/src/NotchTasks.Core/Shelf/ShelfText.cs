namespace NotchTasks.Core.Shelf;

/// The text a shelf tile shows for a file name (Mac: lineLimit 1, truncationMode .middle).
public static class ShelfText
{
    /// The name as it is when it fits in max characters. A longer name keeps its first (max - 1) / 2 characters and its last
    /// max - 1 - (max - 1) / 2, around an ellipsis, so the result is max characters long.
    public static string Middle(string name, int max = 14)
    {
        if (name.Length <= max)
        {
            return name;
        }
        var head = (max - 1) / 2;
        var tail = max - 1 - head;
        return name[..head] + "…" + name[^tail..];
    }
}
