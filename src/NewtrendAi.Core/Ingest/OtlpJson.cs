using System.Text.Json;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace NewtrendAi.Core.Ingest;

internal static class OtlpJson
{
    public static ExportTraceServiceRequest Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var request = new ExportTraceServiceRequest();
        if (!document.RootElement.TryGetProperty("resourceSpans", out var resourceSpans))
            return request;

        foreach (var resourceElement in resourceSpans.EnumerateArray())
        {
            var message = new ResourceSpans();
            if (resourceElement.TryGetProperty("resource", out var resource)
                && resource.TryGetProperty("attributes", out var resourceAttributes))
            {
                message.Resource = new Resource();
                AddAttributes(message.Resource.Attributes, resourceAttributes);
            }

            if (resourceElement.TryGetProperty("scopeSpans", out var scopes))
            {
                foreach (var scopeElement in scopes.EnumerateArray())
                {
                    var scopeSpans = new ScopeSpans();
                    if (scopeElement.TryGetProperty("spans", out var spans))
                    {
                        foreach (var spanElement in spans.EnumerateArray())
                            scopeSpans.Spans.Add(ReadSpan(spanElement));
                    }

                    message.ScopeSpans.Add(scopeSpans);
                }
            }

            request.ResourceSpans.Add(message);
        }

        return request;
    }

    private static Span ReadSpan(JsonElement element)
    {
        var span = new Span
        {
            TraceId = Hex(ReadString(element, "traceId")),
            SpanId = Hex(ReadString(element, "spanId")),
            Name = ReadString(element, "name"),
            Kind = ReadKind(element),
            StartTimeUnixNano = ReadULong(element, "startTimeUnixNano"),
            EndTimeUnixNano = ReadULong(element, "endTimeUnixNano"),
        };

        var parent = ReadString(element, "parentSpanId");
        if (parent.Length > 0)
            span.ParentSpanId = Hex(parent);

        if (element.TryGetProperty("attributes", out var attributes))
            AddAttributes(span.Attributes, attributes);

        if (element.TryGetProperty("status", out var status))
        {
            span.Status = new Status
            {
                Code = ReadStatus(status),
                Message = ReadString(status, "message"),
            };
        }

        return span;
    }

    private static void AddAttributes(IList<KeyValue> target, JsonElement attributes)
    {
        foreach (var attribute in attributes.EnumerateArray())
        {
            if (!attribute.TryGetProperty("value", out var value))
                continue;

            var any = ReadValue(value);
            if (any is null)
                continue;

            target.Add(new KeyValue
            {
                Key = ReadString(attribute, "key"),
                Value = any,
            });
        }
    }

    private static AnyValue? ReadValue(JsonElement value)
    {
        if (value.TryGetProperty("stringValue", out var text))
            return new AnyValue { StringValue = text.GetString() ?? "" };

        if (value.TryGetProperty("intValue", out var integer))
        {
            var parsed = integer.ValueKind == JsonValueKind.String
                ? long.Parse(integer.GetString() ?? "0")
                : integer.GetInt64();
            return new AnyValue { IntValue = parsed };
        }

        if (value.TryGetProperty("doubleValue", out var number))
            return new AnyValue { DoubleValue = number.GetDouble() };

        if (value.TryGetProperty("boolValue", out var flag))
            return new AnyValue { BoolValue = flag.GetBoolean() };

        return null;
    }

    private static Span.Types.SpanKind ReadKind(JsonElement element)
    {
        if (!element.TryGetProperty("kind", out var kind))
            return Span.Types.SpanKind.Internal;

        if (kind.ValueKind == JsonValueKind.Number)
            return (Span.Types.SpanKind)kind.GetInt32();

        return kind.GetString() switch
        {
            "SPAN_KIND_SERVER" => Span.Types.SpanKind.Server,
            "SPAN_KIND_CLIENT" => Span.Types.SpanKind.Client,
            "SPAN_KIND_PRODUCER" => Span.Types.SpanKind.Producer,
            "SPAN_KIND_CONSUMER" => Span.Types.SpanKind.Consumer,
            "SPAN_KIND_INTERNAL" => Span.Types.SpanKind.Internal,
            _ => Span.Types.SpanKind.Internal,
        };
    }

    private static Status.Types.StatusCode ReadStatus(JsonElement status)
    {
        if (!status.TryGetProperty("code", out var code))
            return Status.Types.StatusCode.Unset;

        if (code.ValueKind == JsonValueKind.Number)
            return (Status.Types.StatusCode)code.GetInt32();

        return code.GetString() switch
        {
            "STATUS_CODE_OK" => Status.Types.StatusCode.Ok,
            "STATUS_CODE_ERROR" => Status.Types.StatusCode.Error,
            _ => Status.Types.StatusCode.Unset,
        };
    }

    private static string ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static ulong ReadULong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;

        if (value.ValueKind == JsonValueKind.String)
            return ulong.Parse(value.GetString() ?? "0");

        if (value.ValueKind == JsonValueKind.Number)
            return value.GetUInt64();

        return 0;
    }

    private static ByteString Hex(string hex)
        => hex.Length == 0 ? ByteString.Empty : ByteString.CopyFrom(Convert.FromHexString(hex));
}
