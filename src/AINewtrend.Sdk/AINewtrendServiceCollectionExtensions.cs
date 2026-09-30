using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AINewtrend;

public static class AINewtrendServiceCollectionExtensions
{
    public static IServiceCollection AddAINewtrend(
        this IServiceCollection services,
        Action<AINewtrendOptions>? configure = null)
    {
        var options = new AINewtrendOptions();
        configure?.Invoke(options);

        // The batch processor reads this when the host builds the tracer provider.
        Environment.SetEnvironmentVariable("OTEL_BSP_SCHEDULE_DELAY", "200");
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf");

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(options.ServiceName))
            .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation())
            .UseOtlpExporter(OtlpExportProtocol.HttpProtobuf, options.Endpoint);

        return services;
    }
}
