using System.IO.Compression;
using NewtrendAi.Core.Live;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace NewtrendAi.Core.Ingest;

public static class OtlpTraceEndpoint
{
    public static async Task HandleAsync(HttpContext context, ILiveStore store, LiveBroadcaster broadcaster, ILogger<LiveStore> logger)
    {
        ExportTraceServiceRequest request;
        try
        {
            await using var body = OpenBody(context.Request);
            using var buffer = new MemoryStream();
            await body.CopyToAsync(buffer, context.RequestAborted);
            var bytes = buffer.ToArray();
            var contentType = context.Request.ContentType ?? "";
            logger.LogInformation(
                "OTLP {Method} {Path} {ContentType} {ByteCount} bytes.",
                context.Request.Method,
                context.Request.Path,
                contentType,
                bytes.Length);

            request = contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                ? OtlpJson.Parse(System.Text.Encoding.UTF8.GetString(bytes))
                : ExportTraceServiceRequest.Parser.ParseFrom(bytes);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Rejected OTLP payload.");
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var spanCount = request.ResourceSpans.Sum(resource => resource.ScopeSpans.Sum(scope => scope.Spans.Count));
        var executions = store.Apply(request);
        logger.LogInformation("Ingested {SpanCount} spans. Live executions: {ExecutionCount}.", spanCount, executions.Count);
        await broadcaster.BroadcastAsync();

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/x-protobuf";
    }

    private static Stream OpenBody(HttpRequest request)
    {
        var encoding = request.Headers.ContentEncoding.ToString();
        if (encoding.Contains("gzip", StringComparison.OrdinalIgnoreCase))
            return new GZipStream(request.Body, CompressionMode.Decompress, leaveOpen: true);

        return request.Body;
    }
}
