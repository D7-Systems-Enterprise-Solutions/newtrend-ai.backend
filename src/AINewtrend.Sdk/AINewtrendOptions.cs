namespace AINewtrend;

public sealed class AINewtrendOptions
{
    public string ServiceName { get; set; } = "ai-newtrend-app";

    /// <summary>
    /// OTLP HTTP base address. The exporter appends /v1/traces.
    /// </summary>
    public Uri Endpoint { get; set; } = new("http://127.0.0.1:4318");

    /// <summary>
    /// Accepted for SQL instrumentation in a later sprint. Unused in Sprint 1.
    /// </summary>
    public string? ConnectionString { get; set; }
}
