namespace NewtrendAi;

public sealed class NewtrendAiOptions
{
    public string ServiceName { get; set; } = "newtrend-ai-app";

    /// <summary>
    /// OTLP HTTP base address. The exporter appends /v1/traces.
    /// </summary>
    public Uri Endpoint { get; set; } = new("http://127.0.0.1:4318");

    /// <summary>
    /// Kept for compatibility. SQL capture uses SqlClient instrumentation and does not read this value.
    /// </summary>
    public string? ConnectionString { get; set; }
}
