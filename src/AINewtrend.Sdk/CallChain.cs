using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace AINewtrend;

internal static partial class CallChain
{
    public const string SourceName = "AINewtrend";

    public static readonly ActivitySource Source = new(SourceName);

    public static Activity[] Begin(string sql, string? endpointFunction)
    {
        var parent = Activity.Current;
        if (parent is null)
            return [];

        var frames = Frames(endpointFunction);
        var opened = new List<Activity>();
        foreach (var frame in frames)
        {
            var activity = Source.StartActivity(frame.Name, ActivityKind.Internal);
            if (activity is null)
                continue;

            activity.SetTag("ai.newtrend.layer", "backend");
            activity.SetTag("ai.newtrend.chain", true);
            activity.SetTag("code.function.name", frame.Function);
            if (frame.File is not null)
                activity.SetTag("code.file.path", frame.File);
            if (frame.Line is int line)
                activity.SetTag("code.line.number", line);
            opened.Add(activity);
        }

        var statement = Mask(sql);
        var sqlActivity = Source.StartActivity(SqlTitle(statement), ActivityKind.Client);
        if (sqlActivity is not null)
        {
            sqlActivity.SetTag("ai.newtrend.layer", "sql");
            sqlActivity.SetTag("ai.newtrend.chain", true);
            sqlActivity.SetTag("db.statement", statement);
            opened.Add(sqlActivity);
        }

        return opened.ToArray();
    }

    public static void End(Activity[] activities)
    {
        for (var i = activities.Length - 1; i >= 0; i--)
        {
            if (activities[i].Duration == TimeSpan.Zero && !activities[i].IsStopped)
                activities[i].Stop();
        }
    }

    internal static IReadOnlyList<Frame> Frames(string? endpointFunction)
    {
        var trace = EnhancedStackTrace.Current();
        var found = new List<Frame>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < trace.FrameCount; i++)
        {
            var frame = trace.GetFrame(i);
            if (frame.GetMethod() is not MethodBase method || IsFramework(method))
                continue;

            var located = MethodSource.Locate(method);
            var file = frame.GetFileName();
            if (string.IsNullOrWhiteSpace(file))
                file = located.File;
            var line = frame.GetFileLineNumber();
            if (line <= 0)
                line = located.Line ?? 0;

            if (IsFrameworkFile(file))
                continue;

            var function = located.Function;
            if (endpointFunction is not null && string.Equals(function, endpointFunction, StringComparison.Ordinal))
                continue;

            if (!seen.Add(function))
                continue;

            var name = method.DeclaringType is null ? method.Name : $"{method.DeclaringType.Name}.{method.Name}";
            found.Add(new Frame(name, function, file, line > 0 ? line : null));
        }

        found.Reverse();
        return found;
    }

    private static bool IsFramework(MethodBase method)
    {
        var name = method.DeclaringType?.FullName ?? method.Name;
        return name.StartsWith("System.", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.", StringComparison.Ordinal)
            || name.StartsWith("OpenTelemetry.", StringComparison.Ordinal)
            || name.StartsWith("AINewtrend.", StringComparison.Ordinal)
            || name.StartsWith("Dapper.", StringComparison.Ordinal)
            || name.StartsWith("EntityFramework", StringComparison.Ordinal)
            || name.Contains(".EntityFrameworkCore.", StringComparison.Ordinal)
            || name.Contains("SqlClient", StringComparison.Ordinal)
            || name.Contains("+<", StringComparison.Ordinal);
    }

    private static bool IsFrameworkFile(string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
            return false;

        return file.Contains("\\nuget\\", StringComparison.OrdinalIgnoreCase)
            || file.Contains("/nuget/", StringComparison.OrdinalIgnoreCase)
            || file.Contains("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)
            || file.Contains("\\shared\\Microsoft.NETCore", StringComparison.OrdinalIgnoreCase);
    }

    private static string SqlTitle(string sql)
    {
        var operation = SqlOperation().Match(sql);
        var from = SqlFrom().Match(sql);
        if (operation.Success && from.Success)
            return $"{operation.Groups[1].Value.ToUpperInvariant()} FROM {from.Groups[1].Value}";

        var compact = Whitespace().Replace(sql.Trim(), " ");
        return compact.Length > 80 ? compact[..77] + "..." : compact;
    }

    [GeneratedRegex(@"^\s*(SELECT|INSERT|UPDATE|DELETE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SqlOperation();

    [GeneratedRegex(@"\bFROM\s+((?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)(?:\s*\.\s*(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*))*)", RegexOptions.IgnoreCase)]
    private static partial Regex SqlFrom();

    private static string Mask(string sql)
        => SecretColumn().Replace(sql, "$1[masked]");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?i)((?:password|token|api[_-]?key|secret)\s*\)\s*values\s*\(\s*'?)[^',)\s]+")]
    private static partial Regex SecretColumn();

    internal readonly record struct Frame(string Name, string Function, string? File, int? Line);
}
