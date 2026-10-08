using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace NewtrendAi.Core.Live;

public sealed class LiveStore : ILiveStore
{
    private const int MaxExecutions = 50;
    private readonly object _gate = new();
    private readonly Dictionary<string, MutableExecution> _byTrace = new();
    private readonly Dictionary<string, string> _burstByTrace = new();

    public IReadOnlyList<Execution> Apply(ExportTraceServiceRequest request)
    {
        lock (_gate)
        {
            foreach (var resourceSpans in request.ResourceSpans)
            {
                var serviceName = resourceSpans.Resource is null
                    ? null
                    : SpanNormalizer.ServiceName(resourceSpans.Resource.Attributes);
                var version = resourceSpans.Resource is null
                    ? null
                    : SpanNormalizer.ApplicationVersion(resourceSpans.Resource.Attributes);

                foreach (var scope in resourceSpans.ScopeSpans)
                {
                    foreach (var span in scope.Spans)
                    {
                        if (span.TraceId.IsEmpty || span.SpanId.IsEmpty)
                            continue;

                        var traceId = SpanNormalizer.ToHex(span.TraceId);
                        var burst = SpanNormalizer.Burst(span);
                        if (!string.IsNullOrWhiteSpace(burst))
                            _burstByTrace[traceId] = burst;
                        else if (_burstByTrace.TryGetValue(traceId, out var known))
                            burst = known;

                        var key = string.IsNullOrWhiteSpace(burst) ? traceId : burst;
                        if (!string.IsNullOrWhiteSpace(burst)
                            && key != traceId
                            && _byTrace.TryGetValue(traceId, out var earlier))
                        {
                            if (!_byTrace.TryGetValue(key, out var burstExecution))
                            {
                                burstExecution = new MutableExecution(key);
                                _byTrace[key] = burstExecution;
                            }

                            burstExecution.Absorb(earlier);
                            _byTrace.Remove(traceId);
                        }

                        if (!_byTrace.TryGetValue(key, out var execution))
                        {
                            execution = new MutableExecution(key);
                            _byTrace[key] = execution;
                        }

                        if (string.IsNullOrWhiteSpace(execution.ServiceName))
                            execution.ServiceName = serviceName;

                        if (string.IsNullOrWhiteSpace(execution.ApplicationVersion))
                            execution.ApplicationVersion = version;

                        execution.Upsert(span);
                    }
                }
            }

            foreach (var empty in _byTrace.Where(pair => pair.Value.StepCount == 0).Select(pair => pair.Key).ToArray())
                _byTrace.Remove(empty);

            Trim();
            return SnapshotUnsafe();
        }
    }

    public IReadOnlyList<Execution> Snapshot()
    {
        lock (_gate)
            return SnapshotUnsafe();
    }

    private void Trim()
    {
        if (_byTrace.Count <= MaxExecutions)
            return;

        foreach (var stale in _byTrace.Values
                     .OrderBy(execution => execution.StartedUnixNano)
                     .Take(_byTrace.Count - MaxExecutions)
                     .Select(execution => execution.TraceId)
                     .ToArray())
        {
            _byTrace.Remove(stale);
        }
    }

    private IReadOnlyList<Execution> SnapshotUnsafe()
        => _byTrace.Values
            .OrderByDescending(execution => execution.StartedUnixNano)
            .Select(execution => execution.ToExecution())
            .ToArray();

    private sealed class MutableExecution
    {
        private readonly Dictionary<string, ExecutionStep> _steps = new();

        public MutableExecution(string traceId) => TraceId = traceId;

        public string TraceId { get; }
        public string? UserAction { get; private set; }
        public string? ServiceName { get; set; }
        public string? ApplicationVersion { get; set; }
        public ulong StartedUnixNano { get; private set; } = ulong.MaxValue;
        public int StepCount => _steps.Count;

        public void Absorb(MutableExecution other)
        {
            foreach (var step in other._steps.Values)
                _steps[step.SpanId] = step;

            if (UserAction is null && other.UserAction is not null)
                UserAction = other.UserAction;

            if (string.IsNullOrWhiteSpace(ServiceName))
                ServiceName = other.ServiceName;

            if (string.IsNullOrWhiteSpace(ApplicationVersion))
                ApplicationVersion = other.ApplicationVersion;

            if (other.StartedUnixNano < StartedUnixNano)
                StartedUnixNano = other.StartedUnixNano;
        }

        public void Upsert(Span span)
        {
            var step = SpanNormalizer.ToStep(span);
            if (step.Name.StartsWith("OPTIONS ", StringComparison.OrdinalIgnoreCase))
                return;

            _steps[step.SpanId] = step;

            if (SpanNormalizer.UserAction(span) is { Length: > 0 } action)
                UserAction = action;

            if (span.StartTimeUnixNano > 0 && span.StartTimeUnixNano < StartedUnixNano)
                StartedUnixNano = span.StartTimeUnixNano;
        }

        public Execution ToExecution()
        {
            var steps = _steps.Values
                .OrderBy(step => step.StartUnixNano)
                .ThenBy(step => LayerRank(step.Layer))
                .ToList();

            var failed = steps.FirstOrDefault(step => step.Status == "failed");
            var status = failed is not null
                ? "failed"
                : steps.Count > 0 && steps.All(step => step.Status == "completed")
                    ? "completed"
                    : "running";

            var started = StartedUnixNano == ulong.MaxValue ? 0 : StartedUnixNano;
            var ended = steps.Count == 0 ? 0 : steps.Max(step => (ulong)step.EndUnixNano);

            return new Execution
            {
                TraceId = TraceId,
                UserAction = UserAction,
                Status = status,
                DurationMs = started > 0 && ended > started ? (ended - started) / 1_000_000d : null,
                StartedAt = started == 0
                    ? DateTimeOffset.UnixEpoch
                    : DateTimeOffset.FromUnixTimeMilliseconds((long)(started / 1_000_000)),
                ServiceName = ServiceName,
                ApplicationVersion = ApplicationVersion,
                Steps = steps,
                Error = failed is null
                    ? null
                    : new ErrorInfo
                    {
                        Layer = failed.Layer,
                        Operation = failed.Name,
                        Message = Privacy.Masker.Mask(failed.HttpStatus is int code
                            ? $"HTTP {code}"
                            : "The step failed."),
                    },
            };
        }

        private static int LayerRank(string layer) => layer switch
        {
            "Frontend" => 0,
            "Http" => 1,
            "Backend" => 2,
            "Sql" => 3,
            _ => 4,
        };
    }
}
