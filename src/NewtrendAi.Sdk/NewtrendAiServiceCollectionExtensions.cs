using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace NewtrendAi;

public static class NewtrendAiServiceCollectionExtensions
{
    public static IServiceCollection AddNewtrendAi(
        this IServiceCollection services,
        Action<NewtrendAiOptions>? configure = null)
    {
        var options = new NewtrendAiOptions();
        configure?.Invoke(options);

        // The batch processor reads this when the host builds the tracer provider.
        Environment.SetEnvironmentVariable("OTEL_BSP_SCHEDULE_DELAY", "200");
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf");
        Environment.SetEnvironmentVariable("OTEL_ATTRIBUTE_VALUE_LENGTH_LIMIT", "262144");

        services.AddHttpContextAccessor();
        services.AddSingleton<IStartupFilter, MethodSourceStartupFilter>();

        // Records the application call chain and the SQL text for every EF command.
        EfCommandChainListener.Start();

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(options.ServiceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(CallChain.SourceName)
                    .AddProcessor(new BurstTagProcessor())
                    .AddAspNetCoreInstrumentation(instrumentation =>
                    {
                        instrumentation.EnrichWithHttpRequest = BurstTagProcessor.ApplyRequest;
                        instrumentation.EnrichWithHttpResponse = MethodSource.Apply;
                    });
                AddSqlClientTracing(tracing);
            })
            .UseOtlpExporter(OtlpExportProtocol.HttpProtobuf, options.Endpoint);

        return services;
    }

    private static void AddSqlClientTracing(TracerProviderBuilder tracing)
    {
        // EF commands are already recorded by EfCommandChainListener.
        tracing.AddSqlClientInstrumentation(sql =>
        {
            sql.RecordException = true;
            sql.Filter = static _ => !EfCommandChainListener.InEfCommand;
        });
    }

    /// <summary>
    /// Kept as a compatibility no-op for applications that already call
    /// options.AddNewtrendAi(). SQL instrumentation is now registered globally
    /// by AddNewtrendAi(), so DbContext registration is no longer required.
    /// </summary>
    public static DbContextOptionsBuilder AddNewtrendAi(this DbContextOptionsBuilder builder)
        => builder;
}

internal sealed class BurstTagProcessor : BaseProcessor<Activity>
{
    internal const string Attribute = "newtrend.ai.burst";
    internal const string Header = "X-Newtrend-ai-Burst";

    public override void OnStart(Activity activity)
    {
        var burst = activity.Parent?.GetTagItem(Attribute) as string;
        if (string.IsNullOrWhiteSpace(burst))
            burst = HeaderValue(MethodSource.CurrentHttpContext?.Request);

        if (!string.IsNullOrWhiteSpace(burst))
            activity.SetTag(Attribute, burst);
    }

    internal static void ApplyRequest(Activity activity, HttpRequest request)
    {
        var burst = HeaderValue(request);
        if (!string.IsNullOrWhiteSpace(burst))
            activity.SetTag(Attribute, burst);

        activity.SetTag("url.full", $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}{request.QueryString}");

        var headers = RequestHeaders(request);
        if (headers is not null)
            activity.SetTag("newtrend.ai.http.headers", headers);

        var body = RequestBody(request);
        if (body is not null)
            activity.SetTag("newtrend.ai.http.body", body);
    }

    private static string? RequestHeaders(HttpRequest request)
    {
        var lines = new List<string>();
        foreach (var header in request.Headers)
        {
            if (!KeepHeader(header.Key))
                continue;

            var value = header.Value.ToString();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            lines.Add($"{DisplayName(header.Key)}: {value}");
        }

        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    private static bool KeepHeader(string name)
    {
        if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Accept", StringComparison.OrdinalIgnoreCase))
            return true;

        return name.StartsWith("X-", StringComparison.OrdinalIgnoreCase)
            && !name.Equals(Header, StringComparison.OrdinalIgnoreCase);
    }

    private static string DisplayName(string name)
    {
        if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            return "Authorization";
        if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            return "Content-Type";
        if (name.Equals("Accept", StringComparison.OrdinalIgnoreCase))
            return "Accept";
        return name;
    }

    private static string? RequestBody(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            return null;

        if (!IsTextBody(request.ContentType) || request.ContentLength == 0)
            return null;

        var control = request.HttpContext.Features.Get<IHttpBodyControlFeature>();
        var allowSynchronous = control?.AllowSynchronousIO ?? false;
        try
        {
            request.EnableBuffering();
            if (control is not null)
                control.AllowSynchronousIO = true;

            var buffer = new byte[64 * 1024];
            var read = request.Body.Read(buffer, 0, buffer.Length);
            if (read <= 0 || HasNull(buffer, read))
                return null;

            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (request.Body.CanSeek)
                request.Body.Position = 0;
            if (control is not null)
                control.AllowSynchronousIO = allowSynchronous;
        }
    }

    private static bool IsTextBody(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return true;

        var type = contentType.Split(';', 2)[0].Trim();
        if (type.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase)
            || type.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
            || type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            || type.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static bool HasNull(byte[] buffer, int read)
    {
        for (var i = 0; i < read; i++)
        {
            if (buffer[i] == 0)
                return true;
        }

        return false;
    }

    private static string? HeaderValue(HttpRequest? request)
    {
        if (request is null || !request.Headers.TryGetValue(Header, out var value))
            return null;

        var burst = value.ToString();
        return string.IsNullOrWhiteSpace(burst) ? null : burst;
    }
}
