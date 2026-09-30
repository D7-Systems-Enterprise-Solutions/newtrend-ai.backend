namespace AINewtrend.Core.Live;

/// <summary>
/// Port for Record-on-demand persistence. Sprint 1 registers a recorder that does nothing.
/// SQLite sessions attach here in a later sprint.
/// </summary>
public interface IExecutionRecorder
{
    ValueTask RecordAsync(Execution execution, CancellationToken cancellationToken = default);
}

public sealed class NoOpExecutionRecorder : IExecutionRecorder
{
    public ValueTask RecordAsync(Execution execution, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
