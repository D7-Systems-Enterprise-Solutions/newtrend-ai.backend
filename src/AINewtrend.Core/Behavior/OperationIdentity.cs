using System.Text.RegularExpressions;
using AINewtrend.Core.Live;

namespace AINewtrend.Core.Behavior;

public static partial class OperationIdentity
{
    public static string Key(ExecutionStep step)
    {
        return step.Layer switch
        {
            "Http" => HttpKey(step.Name),
            "Sql" => SqlKey(step.SqlText ?? step.Name),
            "Backend" => $"METHOD:{step.Source?.Function ?? step.Name}",
            "Frontend" => $"UI:{step.Name}",
            _ => $"{step.Layer}:{step.Name}",
        };
    }

    public static string NormalizeSql(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return "";

        var compact = Whitespace().Replace(sql.Trim(), " ");
        compact = StringLiteral().Replace(compact, "?");
        compact = Number().Replace(compact, "?");
        return compact;
    }

    private static string HttpKey(string name)
    {
        var parts = name.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
            return $"HTTP:{parts[0].ToUpperInvariant()}:{PathOnly(parts[1])}";

        return $"HTTP:{name}";
    }

    private static string PathOnly(string route)
    {
        var query = route.IndexOf('?');
        return query >= 0 ? route[..query] : route;
    }

    private static string SqlKey(string sql)
    {
        var normalized = NormalizeSql(sql);
        var match = SqlShape().Match(normalized);
        if (!match.Success)
            return $"SQL:{normalized}";

        return $"SQL:{match.Groups["op"].Value.ToUpperInvariant()}:{match.Groups["table"].Value}";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"'[^']*'")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"\b\d+(\.\d+)?\b")]
    private static partial Regex Number();

    [GeneratedRegex(@"(?i)^(?<op>SELECT|INSERT|UPDATE|DELETE)(?:\s+INTO|\s+FROM)?\s+(?<table>[A-Za-z0-9_\.]+)")]
    private static partial Regex SqlShape();
}
