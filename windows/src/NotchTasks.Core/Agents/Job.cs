namespace NotchTasks.Core.Agents;

/// One row under an item on the pill: a running or waiting task (Mac: Job).
public sealed record Job(string Id, string Title, string Detail, bool Working, string? Url = null, double? Progress = null, string? Symbol = null, string? Tool = null);

/// One watch item with its jobs. Jobs are ordered working first. Equality compares the job lists by value.
public sealed record ItemState(WatchItem Item, IReadOnlyList<Job> Jobs)
{
    public bool Equals(ItemState? other) =>
        other is not null && Item.Equals(other.Item) && Jobs.SequenceEqual(other.Jobs);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Item);
        foreach (var job in Jobs)
        {
            hash.Add(job);
        }
        return hash.ToHashCode();
    }
}
