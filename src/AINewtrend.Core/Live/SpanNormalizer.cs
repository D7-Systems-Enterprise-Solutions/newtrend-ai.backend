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

        return new ExecutionStep
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
            SqlText = Privacy.Masker.Mask(ReadSql(span)),
        };
    }

    public static string? UserAction(Span span)
        => Attribute(span, "ai.newtrend.user_action");

    public static string? ApplicationVersion(IEnumerable<KeyValue> attributes)
        => ResourceAttribute(attributes, "service.version");

    public static string? ReadSql(Span span)
        => Attribute(span, "db.statement") ?? Attribute(span, "db.query.text");

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
