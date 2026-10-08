using OpenTelemetry.Proto.Collector.Trace.V1;

namespace NewtrendAi.Core.Live;

public interface ILiveStore
{
    IReadOnlyList<Execution> Apply(ExportTraceServiceRequest request);
    IReadOnlyList<Execution> Snapshot();
}
