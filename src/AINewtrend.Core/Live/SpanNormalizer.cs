using System.Text.RegularExpressions;
using Google.Protobuf;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace AINewtrend.Core.Live;

public static partial class SpanNormalizer
{
    public static ExecutionStep ToStep(Span span)
    {
        var layer = Classify(span);
        var httpStatus = ReadHttpStatus(span);
        var failed = span.Status?.Code == Status.Types.StatusCode.Error
            || (httpStatus is >= 400);

        var step = new ExecutionStep
        {
            SpanId = ToHex(span.SpanId),
            ParentSpanId = IsEmpty(span.ParentSpanId) ? null : ToHex(span.ParentSpanId),
            Layer = layer,
            Name = NameFor(span, layer),
            DurationMs = DurationMs(span.StartTimeUnixNano, span.EndTimeUnixNano),
            Status = failed ? "failed" : span.EndTimeUnixNano > 0 ? "completed" : "running",
            StartUnixNano = unchecked((long)span.StartTimeUnixNano),
            EndUnixNano = unchecked((long)span.EndTimeUnixNano),
            StartedAt = span.StartTimeUnixNano == 0
                ? DateTimeOffset.UnixEpoch
                : DateTimeOffset.FromUnixTimeMilliseconds(unchecked((long)(span.StartTimeUnixNano / 1_000_000))),
            Source = ReadSource(span),
            HttpStatus = httpStatus,
            HttpUrl = Attribute(span, "url.full"),
            HttpHeaders = Attribute(span, "ai.newtrend.http.headers"),
            HttpBody = Attribute(span, "ai.newtrend.http.body"),
            SqlText = Privacy.Masker.Mask(ReadSql(span)),
        };
        step.Kind = ClassifyKind(step.Name, step.SqlText, step.Source?.Function);
        return step;
    }

    public static string ClassifyKind(string name, string? sqlText, string? function)
    {
        if (IsGenerated(name) || IsGenerated(function))
            return "infrastructure";

        if (IsInfrastructureMethod(name) || IsInfrastructureMethod(function))
            return "infrastructure";

        if (IsInfrastructureSql(sqlText) || IsInfrastructureSql(name))
            return "infrastructure";

        return "core";
    }

    public static string? UserAction(Span span)
        => Attribute(span, "ai.newtrend.user_action");

    public static string? Burst(Span span)
    {
        var attribute = Attribute(span, "ai.newtrend.burst");
        if (!string.IsNullOrWhiteSpace(attribute))
            return attribute;

        return BurstFromTraceState(span.TraceState);
    }

    private static string? BurstFromTraceState(string? traceState)
    {
        if (string.IsNullOrWhiteSpace(traceState))
            return null;

        foreach (var member in traceState.Split(','))
        {
            var piece = member.Trim();
            const string prefix = "ai-newtrend=";
            if (piece.StartsWith(prefix, StringComparison.Ordinal))
            {
                var value = piece[prefix.Length..];
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }

    public static string? ApplicationVersion(IEnumerable<KeyValue> attributes)
        => ResourceAttribute(attributes, "service.version");

    public static string? ReadSql(Span span)
        => Attribute(span, "db.query.text") ?? Attribute(span, "db.statement");

    public static string? ServiceName(IEnumerable<KeyValue> attributes)
    {
        return ResourceAttribute(attributes, "service.name");
    }

    private static string? ResourceAttribute(IEnumerable<KeyValue> attributes, string key)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.Key == key)
                return ValueAsString(attribute.Value);
        }

        return null;
    }

    public static string ToHex(ByteString bytes)
        => Convert.ToHexString(bytes.ToByteArray()).ToLowerInvariant();

    private static string Classify(Span span)
    {
        var layer = Attribute(span, "ai.newtrend.layer");
        if (layer is "frontend" or "http" or "backend" or "sql")
            return Capitalize(layer);

        if (ReadSql(span) is not null)
            return "Sql";

        if (Attribute(span, "ai.newtrend.user_action") is not null)
            return "Frontend";

        if (span.Kind == Span.Types.SpanKind.Client && HasHttp(span))
            return "Http";

        if (span.Kind == Span.Types.SpanKind.Server)
            return "Backend";

        if (HasHttp(span))
            return "Http";

        return "Backend";
    }

    private static string NameFor(Span span, string layer)
    {
        if (layer == "Frontend" && UserAction(span) is { Length: > 0 } action)
            return action;

        var method = Attribute(span, "http.request.method") ?? Attribute(span, "http.method");
        var route = Route(span);

        if (method is not null && route is not null)
            return $"{method} {route}";

        if (layer == "Sql")
            return SqlTitle(ReadSql(span) ?? span.Name);

        return string.IsNullOrWhiteSpace(span.Name) ? layer : span.Name;
    }

    private static string SqlTitle(string sql)
    {
        var operation = SqlOperation().Match(sql);
        var from = SqlFrom().Match(sql);
        if (operation.Success && from.Success)
            return $"{operation.Groups[1].Value.ToUpperInvariant()} FROM {from.Groups[1].Value}";

        return string.IsNullOrWhiteSpace(sql) ? "SQL" : sql;
    }

    [GeneratedRegex(@"^\s*(SELECT|INSERT|UPDATE|DELETE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SqlOperation();

    [GeneratedRegex(@"\bFROM\s+((?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)(?:\s*\.\s*(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*))*)", RegexOptions.IgnoreCase)]
    private static partial Regex SqlFrom();

    [GeneratedRegex(@"\b(?:FROM|JOIN|INTO|UPDATE)\s+((?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)(?:\s*\.\s*(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*))*)", RegexOptions.IgnoreCase)]
    private static partial Regex SqlObject();

    [GeneratedRegex(@"lambda_method\d+|<>c|\+<|b__", RegexOptions.IgnoreCase)]
    private static partial Regex GeneratedMethod();

    private static bool IsGenerated(string? value)
        => value is not null && GeneratedMethod().IsMatch(value);

    private static bool IsInfrastructureMethod(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains("Permission", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Authorization", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Credential", StringComparison.OrdinalIgnoreCase)
            || value.Contains("AuthSession", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInfrastructureSql(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return false;

        foreach (Match match in SqlObject().Matches(sql))
        {
            if (IsInfrastructureObject(match.Groups[1].Value))
                return true;
        }

        return false;
    }

    private static bool IsInfrastructureObject(string reference)
    {
        var parts = reference.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        var table = Unwrap(parts[^1]);
        var schema = parts.Length > 1 ? Unwrap(parts[^2]) : null;
        if (schema is "identity" or "auth" or "security")
            return true;

        if (table.Equals("Users", StringComparison.OrdinalIgnoreCase)
            || table.Equals("Roles", StringComparison.OrdinalIgnoreCase)
            || table.Equals("Claims", StringComparison.OrdinalIgnoreCase)
            || table.Equals("Permissions", StringComparison.OrdinalIgnoreCase)
            || table.Equals("UserRoles", StringComparison.OrdinalIgnoreCase)
            || table.Equals("AuthSessions", StringComparison.OrdinalIgnoreCase)
            || table.Equals("CredentialAccounts", StringComparison.OrdinalIgnoreCase)
            || table.Equals("RefreshTokens", StringComparison.OrdinalIgnoreCase))
            return true;

        if (table.StartsWith("AspNet", StringComparison.OrdinalIgnoreCase))
            return true;

        return table.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || table.Contains("permission", StringComparison.OrdinalIgnoreCase)
            || table.Contains("token", StringComparison.OrdinalIgnoreCase);
    }

    private static string Unwrap(string name)
        => name.Trim().Trim('[', ']');

    private static string? Route(Span span)
    {
        var route = Attribute(span, "http.route")
            ?? Attribute(span, "url.path")
            ?? Attribute(span, "http.target");
        if (route is not null)
            return route;

        var url = Attribute(span, "url.full") ?? Attribute(span, "http.url");
        if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            return parsed.AbsolutePath;

        return url;
    }

    private static SourceLocation? ReadSource(Span span)
    {
        var file = Attribute(span, "code.file.path");
        var function = Attribute(span, "code.function.name");
        var lineText = Attribute(span, "code.line.number");
        if (file is null && function is null && lineText is null)
            return null;

        int? line = int.TryParse(lineText, out var parsed) ? parsed : null;
        return new SourceLocation { File = file, Function = function, Line = line };
    }

    private static int? ReadHttpStatus(Span span)
    {
        var text = Attribute(span, "http.response.status_code") ?? Attribute(span, "http.status_code");
        return int.TryParse(text, out var status) ? status : null;
    }

    private static bool HasHttp(Span span)
        => Attribute(span, "http.request.method") is not null
           || Attribute(span, "http.method") is not null;

    private static string? Attribute(Span span, string key)
    {
        foreach (var attribute in span.Attributes)
        {
            if (attribute.Key == key)
                return ValueAsString(attribute.Value);
        }

        return null;
    }

    private static string? ValueAsString(AnyValue value) => value.ValueCase switch
    {
        AnyValue.ValueOneofCase.StringValue => value.StringValue,
        AnyValue.ValueOneofCase.IntValue => value.IntValue.ToString(),
        AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue.ToString(),
        AnyValue.ValueOneofCase.BoolValue => value.BoolValue ? "true" : "false",
        _ => null,
    };

    private static bool IsEmpty(ByteString bytes)
    {
        if (bytes.Length == 0)
            return true;

        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != 0)
                return false;
        }

        return true;
    }

    private static double? DurationMs(ulong start, ulong end)
    {
        if (end <= start)
            return null;

        return (end - start) / 1_000_000d;
    }

    private static string Capitalize(string layer) => layer switch
    {
        "frontend" => "Frontend",
        "http" => "Http",
        "backend" => "Backend",
        "sql" => "Sql",
        _ => layer,
    };
}
