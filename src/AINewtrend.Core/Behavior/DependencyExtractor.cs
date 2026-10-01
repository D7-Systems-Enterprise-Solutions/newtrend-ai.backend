using AINewtrend.Core.Live;

namespace AINewtrend.Core.Behavior;

public sealed class FeatureDependency
{
    public required string FeatureKey { get; init; }
    public required string Type { get; init; }
    public required string Name { get; init; }
    public string? SourceLocation { get; init; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}

public sealed class MapNode
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public List<MapNode> Children { get; init; } = [];
}

public static class DependencyExtractor
{
    public static IReadOnlyList<FeatureDependency> FromExecution(Execution execution)
    {
        var feature = execution.UserAction ?? "Execution";
        var seen = DateTimeOffset.UtcNow;
        var items = new List<FeatureDependency>();

        foreach (var step in execution.Steps)
        {
            var (type, name) = Classify(step);
            items.Add(new FeatureDependency
            {
                FeatureKey = feature,
                Type = type,
                Name = name,
                SourceLocation = FormatSource(step.Source),
                FirstSeen = seen,
                LastSeen = seen,
            });
        }

        return items
            .GroupBy(item => $"{item.Type}|{item.Name}")
            .Select(group => group.First())
            .ToArray();
    }

    public static MapNode Map(string feature, IEnumerable<FeatureDependency> dependencies)
    {
        var root = new MapNode { Name = feature, Type = "Feature" };
        MapNode? api = null;
        MapNode? handler = null;

        foreach (var dependency in dependencies.Where(item => item.FeatureKey == feature))
        {
            var node = new MapNode { Name = dependency.Name, Type = dependency.Type };
            switch (dependency.Type)
            {
                case "API":
                    api = node;
                    root.Children.Add(node);
                    break;
                case "Handler":
                    handler = node;
                    (api ?? root).Children.Add(node);
                    break;
                case "Database":
                case "External API":
                    (handler ?? api ?? root).Children.Add(node);
                    break;
                default:
                    root.Children.Add(node);
                    break;
            }
        }

        return root;
    }

    private static (string Type, string Name) Classify(ExecutionStep step)
    {
        if (step.Layer == "Frontend")
            return ("Frontend", step.Name);
        if (step.Layer == "Http")
            return step.Name.Contains("external", StringComparison.OrdinalIgnoreCase)
                ? ("External API", step.Name)
                : ("API", step.Name);
        if (step.Layer == "Sql")
            return ("Database", OperationIdentity.Key(step));
        if (step.Source?.Function?.Contains("Repository", StringComparison.Ordinal) == true)
            return ("Repository", step.Source.Function);
        if (step.Layer == "Backend")
            return ("Handler", step.Source?.Function ?? step.Name);
        return ("Service", step.Name);
    }

    private static string? FormatSource(SourceLocation? source)
    {
        if (source is null)
            return null;

        var line = source.Line is int number ? $":{number}" : "";
        return $"{source.File}{line} {source.Function}".Trim();
    }
}
