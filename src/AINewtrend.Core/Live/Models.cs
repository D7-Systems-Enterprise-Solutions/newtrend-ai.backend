namespace AINewtrend.Core.Live;

public sealed class Execution
{
    public required string TraceId { get; init; }
    public string? UserAction { get; set; }
    public string Status { get; set; } = "running";
    public double? DurationMs { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public string? ServiceName { get; set; }
    public List<ExecutionStep> Steps { get; set; } = [];
    public ErrorInfo? Error { get; set; }
}

public sealed class ExecutionStep
{
    public required string SpanId { get; init; }
    public string? ParentSpanId { get; init; }
    public required string Layer { get; set; }
    public required string Name { get; set; }
    public double? DurationMs { get; set; }
    public string Status { get; set; } = "running";
    public long StartUnixNano { get; set; }
    public long EndUnixNano { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public SourceLocation? Source { get; set; }
    public int? HttpStatus { get; set; }
}

public sealed class SourceLocation
{
    public string? File { get; set; }
    public string? Function { get; set; }
    public int? Line { get; set; }
}

public sealed class ErrorInfo
{
    public string? Message { get; set; }
    public string? Layer { get; set; }
}
