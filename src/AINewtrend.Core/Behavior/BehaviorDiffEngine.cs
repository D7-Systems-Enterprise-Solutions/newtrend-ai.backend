using AINewtrend.Core.Live;

namespace AINewtrend.Core.Behavior;

public sealed class BehaviorDiff
{
    public List<string> Added { get; init; } = [];
    public List<string> Removed { get; init; } = [];
    public List<string> Changed { get; init; } = [];
    public List<string> Reordered { get; init; } = [];
}

public static class BehaviorDiffEngine
{
    public static BehaviorDiff Compare(Execution baseline, Execution current)
    {
        var baseSteps = baseline.Steps;
        var currentSteps = current.Steps;
        var baseKeys = baseSteps.Select(OperationIdentity.Key).ToList();
        var currentKeys = currentSteps.Select(OperationIdentity.Key).ToList();

        var added = Subtract(currentKeys, baseKeys);
        var removed = Subtract(baseKeys, currentKeys);
        var changed = new List<string>();

        var baseStatus = baseSteps.GroupBy(OperationIdentity.Key).ToDictionary(group => group.Key, group => group.First().Status);
        foreach (var step in currentSteps)
        {
            var key = OperationIdentity.Key(step);
            if (baseStatus.TryGetValue(key, out var status) && status != step.Status && !changed.Contains(key))
                changed.Add(key);
        }

        var sharedBase = baseKeys.Where(currentKeys.Contains).ToList();
        var sharedCurrent = currentKeys.Where(baseKeys.Contains).ToList();
        var reordered = sharedBase.SequenceEqual(sharedCurrent)
            ? []
            : new List<string> { string.Join(" -> ", sharedCurrent) };

        return new BehaviorDiff
        {
            Added = added,
            Removed = removed,
            Changed = changed,
            Reordered = reordered,
        };
    }

    private static List<string> Subtract(IReadOnlyList<string> source, IReadOnlyList<string> other)
    {
        var remaining = other.ToList();
        var extra = new List<string>();
        foreach (var key in source)
        {
            var index = remaining.IndexOf(key);
            if (index >= 0)
                remaining.RemoveAt(index);
            else
                extra.Add(key);
        }

        return extra;
    }
}
