using OpenTelemetry.Proto.Collector.Trace.V1;

namespace NewtrendAi.Core.Live;

public sealed record LiveCapture(long Epoch, IReadOnlyList<Execution> Executions);

public interface ILiveStore
{
    IReadOnlyList<Execution> Apply(ExportTraceServiceRequest request);
    IReadOnlyList<Execution> Snapshot();
    LiveCapture Capture();
    void Clear();
    void StartNewTrace(string? traceId = null);
    void SetMaxBurstItems(int maxItems);
}
