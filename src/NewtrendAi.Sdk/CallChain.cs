using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using OpenTelemetry;

namespace NewtrendAi;

internal static partial class CallChain
{
    public const string SourceName = "NewtrendAi";

    public static readonly ActivitySource Source = new(SourceName);

    static CallChain()
    {
        // OpenTelemetry's parent-based sampler returns no activity when Activity.Current
        // is not recorded. A local listener forces these spans to be created and recorded
        // so the official SqlClient instrumentation sees a recorded parent.
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = source => source.Name == SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        });
    }

    public static Activity[] BeginBackendChain(string? endpointFunction)
    {
        var parent = Activity.Current?.Context;
        var opened = new List<Activity>();
        var currentParent = parent;

        try
        {
            foreach (var frame in Frames(endpointFunction))
            {
                var activity = Start(frame.Name, ActivityKind.Internal, currentParent);
                if (activity is null)
                    continue;

                activity.SetTag("newtrend.ai.layer", "backend");
                activity.SetTag("newtrend.ai.chain", true);
                activity.SetTag("code.function.name", frame.Function);
                if (frame.File is not null)
                    activity.SetTag("code.file.path", frame.File);
                if (frame.Line is int line)
                    activity.SetTag("code.line.number", line);

                opened.Add(activity);

                // Make the next application function a child of the previous one.
                // SqlClient instrumentation then parents the SQL span to the innermost function.
                currentParent = activity.Context;
            }
        }
        catch (Exception)
        {
            // A bad frame must not prevent SQL tracing.
        }

        return opened.ToArray();
    }

    public static string InlineParameters(string sql, IEnumerable<(string Name, object? Value)> parameters)
    {
        var named = new List<(string Name, object? Value)>();
        foreach (var (parameterName, parameterValue) in parameters)
        {
            if (string.IsNullOrWhiteSpace(parameterName))
                continue;

            var name = parameterName.StartsWith('@')
                ? parameterName
                : "@" + parameterName;
            var value = parameterValue is null or DBNull ? null : parameterValue;
            named.Add((name, value));
        }

        foreach (var (name, value) in named.OrderByDescending(parameter => parameter.Name.Length))
        {
            var pattern = $@"(?<![A-Za-z0-9_]){Regex.Escape(name)}(?![A-Za-z0-9_])";
            sql = Regex.Replace(sql, pattern, FormatParameter(value));
        }

        return sql;
    }

    private static string FormatParameter(object? value)
    {
        switch (value)
        {
            case null:
                return "NULL";
            case bool bit:
                return bit ? "1" : "0";
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL";
            case DateTime date:
                return $"'{date:yyyy-MM-dd HH:mm:ss.fffffff}'";
            case DateTimeOffset date:
                return $"'{date:yyyy-MM-dd HH:mm:ss.fffffff zzz}'";
            case DateOnly date:
                return $"'{date:yyyy-MM-dd}'";
            case TimeOnly time:
                return $"'{time:HH:mm:ss.fffffff}'";
            case Guid guid:
                return $"'{guid}'";
            case byte[] bytes:
                return "0x" + Convert.ToHexString(bytes);
            case string text:
                return $"'{text.Replace("'", "''")}'";
            case IEnumerable items:
                return $"'{string.Join(", ", items.Cast<object?>().Select(item => FormatParameter(item).Trim('\''))).Replace("'", "''")}'";
            default:
                return $"'{value.ToString()?.Replace("'", "''")}'";
        }
    }

    public static Activity? BeginSql(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return null;

        if (sql.Length > 100_000)
            sql = sql[..100_000] + "...";

        var activity = Start(SqlTitle(sql), ActivityKind.Client, Activity.Current?.Context);
        if (activity is null)
            return null;

        activity.SetTag("newtrend.ai.layer", "sql");
        activity.SetTag("newtrend.ai.chain", true);
        activity.SetTag("db.query.text", sql);
        activity.SetTag("db.system.name", "mssql");
        return activity;
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

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private static Activity? Start(string name, ActivityKind kind, ActivityContext? parent)
    {
        using (SuppressInstrumentationScope.Begin(false))
        {
            return parent is { } context
                ? Source.StartActivity(name, kind, context)
                : Source.StartActivity(name, kind);
        }
    }

    public static void End(Activity[] activities)
    {
        for (var i = activities.Length - 1; i >= 0; i--)
        {
            if (!activities[i].IsStopped)
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
            try
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
            catch (Exception)
            {
                // One dynamic frame must not hide the command.
            }
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
            || name.StartsWith("NewtrendAi.", StringComparison.Ordinal)
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

    internal readonly record struct Frame(string Name, string Function, string? File, int? Line);
}
