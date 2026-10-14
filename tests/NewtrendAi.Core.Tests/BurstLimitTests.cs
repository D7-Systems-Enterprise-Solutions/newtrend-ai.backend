using Google.Protobuf;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;
using NewtrendAi.Core.Live;

namespace NewtrendAi.Core.Tests;

public class BurstLimitTests
{
    [Fact]
    public void A_full_burst_closes_and_the_next_span_starts_another()
    {
        var store = new LiveStore(NullLogger<LiveStore>.Instance);
        store.SetMaxBurstItems(2);

        store.Apply(Batch("burst-a", "Click save", ("span-1", "Click save"), ("span-2", "GET /api/meters")));
        var closed = store.Apply(Batch("burst-a", null, ("span-3", "SELECT meters")));

        Assert.Equal(2, closed.Count);
        var finished = closed.Single(execution => execution.TraceId == "burst-a");
        var opened = closed.Single(execution => execution.TraceId != "burst-a");
        Assert.Equal(2, finished.Steps.Count);
        Assert.Equal(["SELECT meters"], opened.Steps.Select(step => step.Name));

        var next = store.Apply(Batch("burst-a", null, ("span-4", "GET /api/states")));

        Assert.Equal(2, next.Single(execution => execution.TraceId == "burst-a").Steps.Count);
        Assert.Equal(["SELECT meters", "GET /api/states"], next.Single(execution => execution.TraceId == opened.TraceId).Steps.Select(step => step.Name));
    }

    [Fact]
    public void The_next_frontend_event_is_its_own_burst()
    {
        var store = new LiveStore(NullLogger<LiveStore>.Instance);
        store.SetMaxBurstItems(1);

        store.Apply(Batch("burst-a", "Click save", ("span-1", "Click save")));
        var snapshot = store.Apply(Batch("burst-b", "Click states", ("span-2", "Click states")));

        Assert.Equal(2, snapshot.Count);
        Assert.Contains(snapshot, execution => execution.TraceId == "burst-b" && execution.UserAction == "Click states");
        Assert.Single(snapshot.Single(execution => execution.TraceId == "burst-a").Steps);
    }

    private static ExportTraceServiceRequest Batch(string burst, string? action, params (string SpanId, string Name)[] spans)
    {
        var scope = new ScopeSpans();
        foreach (var (spanId, name) in spans)
        {
            var span = new Span
            {
                TraceId = ByteString.CopyFrom(Convert.FromHexString("aabbccddeeff00112233445566778899")),
                SpanId = ByteString.CopyFrom(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(spanId))[..8]),
                Name = name,
                StartTimeUnixNano = 1_000_000,
                EndTimeUnixNano = 2_000_000,
            };
            Add(span, "newtrend.ai.burst", burst);
            if (action is not null)
                Add(span, "newtrend.ai.user_action", action);
            scope.Spans.Add(span);
        }

        return new ExportTraceServiceRequest
        {
            ResourceSpans =
            {
                new ResourceSpans
                {
                    ScopeSpans = { scope },
                },
            },
        };
    }

    private static void Add(Span span, string key, string value)
    {
        span.Attributes.Add(new KeyValue
        {
            Key = key,
            Value = new AnyValue { StringValue = value },
        });
    }
}
