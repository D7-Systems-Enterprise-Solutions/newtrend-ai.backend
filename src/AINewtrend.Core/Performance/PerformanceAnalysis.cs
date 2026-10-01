using AINewtrend.Core.Behavior;
using AINewtrend.Core.Live;

namespace AINewtrend.Core.Performance;

public sealed class PerformanceSample
{
    public required string Id { get; init; }
    public required string OperationKey { get; init; }
    public string? ApplicationVersion { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
    public double DurationMs { get; init; }
    public required string Status { get; init; }
}

public sealed class DurationMetrics
{
    public double Average { get; init; }
    public double P50 { get; init; }
    public double P95 { get; init; }
    public double P99 { get; init; }
    public double Maximum { get; init; }
}

public sealed class PerformanceComparison
{
    public required string OperationKey { get; init; }
    public string? VersionA { get; init; }
    public string? VersionB { get; init; }
    public string Metric { get; init; } = "p95";
    public double ValueA { get; init; }
    public double ValueB { get; init; }
    public double ChangePercent { get; init; }
}

public sealed class LayerShare
{
    public required string Layer { get; init; }
    public double DurationMs { get; init; }
    public double Share { get; init; }
}

public sealed class RepeatedSql
{
    public required string Statement { get; init; }
    public int Occurrences { get; init; }
    public bool RepeatedReadInLoop { get; init; }
}

public sealed class SlowSql
{
    public string? UserAction { get; init; }
    public string? Endpoint { get; init; }
    public string? Handler { get; init; }
    public string? Repository { get; init; }
    public string? Statement { get; init; }
    public double? DurationMs { get; init; }
    public string? File { get; init; }
    public int? Line { get; init; }
    public required string TraceId { get; init; }
}

public static class PercentileCalculator
{
    public static DurationMetrics Calculate(IReadOnlyList<double> durations)
    {
        if (durations.Count == 0)
            return new DurationMetrics();

        var ordered = durations.OrderBy(value => value).ToArray();
        return new DurationMetrics
        {
            Average = ordered.Average(),
            P50 = At(ordered, 0.50),
            P95 = At(ordered, 0.95),
            P99 = At(ordered, 0.99),
            Maximum = ordered[^1],
        };
    }

    private static double At(double[] ordered, double percentile)
    {
        var index = (int)Math.Ceiling(percentile * ordered.Length) - 1;
        if (index < 0)
            index = 0;
        if (index >= ordered.Length)
            index = ordered.Length - 1;
        return ordered[index];
    }
}

public static class PerformanceAnalyzer
{
    public static IReadOnlyList<PerformanceSample> Samples(Execution execution)
    {
        var captured = DateTimeOffset.UtcNow;
        return execution.Steps
            .Where(step => step.DurationMs is not null)
            .Select(step => new PerformanceSample
            {
                Id = Guid.NewGuid().ToString("n"),
                OperationKey = OperationIdentity.Key(step),
                ApplicationVersion = execution.ApplicationVersion,
                CapturedAt = captured,
                DurationMs = step.DurationMs ?? 0,
                Status = step.Status,
            })
            .ToArray();
    }

    public static PerformanceComparison? Compare(string operationKey, string? versionA, string? versionB, IReadOnlyList<PerformanceSample> samples)
    {
        var a = PercentileCalculator.Calculate(Values(samples, operationKey, versionA));
        var b = PercentileCalculator.Calculate(Values(samples, operationKey, versionB));
        if (a.P95 == 0 && b.P95 == 0)
            return null;

        var change = a.P95 == 0 ? 0 : (b.P95 - a.P95) / a.P95 * 100;
        return new PerformanceComparison
        {
            OperationKey = operationKey,
            VersionA = versionA,
            VersionB = versionB,
            ValueA = a.P95,
            ValueB = b.P95,
            ChangePercent = Math.Round(change, 1),
        };
    }

    public static IReadOnlyList<LayerShare> Bottleneck(Execution execution)
    {
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var step in execution.Steps)
        {
            if (step.DurationMs is not double duration)
                continue;

            var layer = Bucket(step);
            totals[layer] = totals.GetValueOrDefault(layer) + duration;
        }

        var sum = totals.Values.Sum();
        return totals
            .OrderByDescending(pair => pair.Value)
            .Select(pair => new LayerShare
            {
                Layer = pair.Key,
                DurationMs = pair.Value,
                Share = sum == 0 ? 0 : pair.Value / sum,
            })
            .ToArray();
    }

    public static IReadOnlyList<RepeatedSql> RepeatedDatabaseAccess(Execution execution)
    {
        var sql = execution.Steps.Where(step => step.Layer == "Sql" && !string.IsNullOrWhiteSpace(step.SqlText)).ToList();
        return sql
            .GroupBy(step => OperationIdentity.NormalizeSql(step.SqlText))
            .Where(group => group.Count() > 1)
            .Select(group => new RepeatedSql
            {
                Statement = group.Key,
                Occurrences = group.Count(),
                RepeatedReadInLoop = group.Key.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                    && group.Select(step => step.ParentSpanId).Distinct().Count() == 1
                    && group.First().ParentSpanId is not null,
            })
            .ToArray();
    }

    public static SlowSql? SlowestSql(Execution execution)
    {
        var sql = execution.Steps
            .Where(step => step.Layer == "Sql")
            .OrderByDescending(step => step.DurationMs ?? 0)
            .FirstOrDefault();
        if (sql is null)
            return null;

        var endpoint = execution.Steps.FirstOrDefault(step => step.Layer == "Http");
        var handler = execution.Steps.FirstOrDefault(step => step.Layer == "Backend" && step.Source?.Function?.Contains("Repository", StringComparison.Ordinal) != true);
        var repository = execution.Steps.FirstOrDefault(step => step.Source?.Function?.Contains("Repository", StringComparison.Ordinal) == true) ?? sql;

        return new SlowSql
        {
            UserAction = execution.UserAction,
            Endpoint = endpoint?.Name,
            Handler = handler?.Source?.Function ?? handler?.Name,
            Repository = repository.Source?.Function ?? repository.Name,
            Statement = sql.SqlText,
            DurationMs = sql.DurationMs,
            File = sql.Source?.File ?? repository.Source?.File,
            Line = sql.Source?.Line ?? repository.Source?.Line,
            TraceId = execution.TraceId,
        };
    }

    private static List<double> Values(IReadOnlyList<PerformanceSample> samples, string operationKey, string? version)
        => samples
            .Where(sample => sample.OperationKey == operationKey && sample.ApplicationVersion == version)
            .Select(sample => sample.DurationMs)
            .ToList();

    private static string Bucket(ExecutionStep step)
    {
        if (step.Name.Contains("serial", StringComparison.OrdinalIgnoreCase))
            return "Serialization";
        if (step.Layer == "Http" && step.Name.Contains("external", StringComparison.OrdinalIgnoreCase))
            return "External API";
        return step.Layer switch
        {
            "Sql" => "SQL",
            "Http" => "HTTP",
            "Backend" => "Backend",
            _ => "Other",
        };
    }
}
