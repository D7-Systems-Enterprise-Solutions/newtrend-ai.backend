using System.Diagnostics;
using System.Text;
using Xunit;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;
using AINewtrend.Core.Live;
using AINewtrend.Core.Privacy;
using AINewtrend;

namespace AINewtrend.Core.Tests;

public class HttpCurlTests
{
    private static readonly ActivitySource Source = new("ai-newtrend-http-curl-tests");

    static HttpCurlTests()
    {
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = source => source.Name == Source.Name,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        });
    }

    [Fact]
    public void Http_span_keeps_the_url_headers_and_body()
    {
        var span = new Span
        {
            SpanId = ByteString.CopyFrom(Convert.FromHexString("0102030405060708")),
            Name = "GET",
            Kind = Span.Types.SpanKind.Client,
            StartTimeUnixNano = 1_000_000,
            EndTimeUnixNano = 2_000_000,
        };
        Add(span, "ai.newtrend.layer", "http");
        Add(span, "http.method", "GET");
        Add(span, "http.route", "/api/communications/channels/options");
        Add(span, "url.full", "https://host/api/communications/channels/options?active=true");
        Add(span, "ai.newtrend.http.headers", "Authorization: Bearer eyJ.token\nAccept: application/json");
        Add(span, "ai.newtrend.http.body", "{\"name\":\"Acme\"}");

        var step = SpanNormalizer.ToStep(span);

        Assert.Equal("GET /api/communications/channels/options", step.Name);
        Assert.Equal("https://host/api/communications/channels/options?active=true", step.HttpUrl);
        Assert.Equal("Authorization: Bearer eyJ.token\nAccept: application/json", step.HttpHeaders);
        Assert.Equal("{\"name\":\"Acme\"}", step.HttpBody);
    }

    [Fact]
    public void Mask_keeps_the_bearer_on_the_http_step()
    {
        var execution = new Execution
        {
            TraceId = "trace",
            StartedAt = DateTimeOffset.UnixEpoch,
            Steps =
            [
                new ExecutionStep
                {
                    SpanId = "span",
                    Layer = "Http",
                    Name = "GET /api/communications/channels/options",
                    HttpUrl = "https://host/api/communications/channels/options",
                    HttpHeaders = "Authorization: Bearer eyJ.token",
                },
            ],
        };

        Masker.MaskExecution(execution);

        Assert.Contains("eyJ.token", execution.Steps[0].HttpHeaders);
    }

    [Fact]
    public void Request_records_the_url_authorization_and_body()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("host");
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/communications/channels/options";
        context.Request.QueryString = new QueryString("?active=true");
        context.Request.Headers.Authorization = "Bearer eyJ.token";
        context.Request.Headers.Accept = "application/json";
        context.Request.ContentType = "application/json";
        context.Request.Headers["traceparent"] = "00-abc";
        context.Request.Headers["X-AI-Newtrend-Burst"] = "burst-1";
        context.Request.Headers["X-Correlation-Id"] = "corr";
        var bytes = Encoding.UTF8.GetBytes("{\"name\":\"Acme\"}");
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;

        using var activity = Source.StartActivity("request");
        Assert.NotNull(activity);
        BurstTagProcessor.ApplyRequest(activity, context.Request);

        Assert.Equal("https://host/api/communications/channels/options?active=true", activity.GetTagItem("url.full"));
        var headers = Assert.IsType<string>(activity.GetTagItem("ai.newtrend.http.headers"));
        Assert.Contains("Authorization: Bearer eyJ.token", headers);
        Assert.Contains("Accept: application/json", headers);
        Assert.Contains("X-Correlation-Id: corr", headers);
        Assert.DoesNotContain("traceparent", headers);
        Assert.DoesNotContain("burst-1", headers);
        Assert.Equal("{\"name\":\"Acme\"}", activity.GetTagItem("ai.newtrend.http.body"));
        Assert.Equal(0, context.Request.Body.Position);
    }

    [Fact]
    public void Get_request_omits_the_body()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("host");
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/communications/channels/options";
        var bytes = Encoding.UTF8.GetBytes("{\"name\":\"Acme\"}");
        context.Request.Body = new MemoryStream(bytes);
        context.Request.Body.Position = 4;
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;

        using var activity = Source.StartActivity("request");
        Assert.NotNull(activity);
        BurstTagProcessor.ApplyRequest(activity, context.Request);

        Assert.Null(activity.GetTagItem("ai.newtrend.http.body"));
        Assert.Equal(4, context.Request.Body.Position);
    }

    [Fact]
    public void Multipart_body_is_not_captured()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("host");
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/upload";
        context.Request.ContentType = "multipart/form-data; boundary=abc";
        var bytes = Encoding.UTF8.GetBytes("file-bytes");
        context.Request.Body = new MemoryStream(bytes);
        context.Request.Body.Position = 3;
        context.Request.ContentLength = bytes.Length;

        using var activity = Source.StartActivity("request");
        Assert.NotNull(activity);
        BurstTagProcessor.ApplyRequest(activity, context.Request);

        Assert.Null(activity.GetTagItem("ai.newtrend.http.body"));
        Assert.Equal(3, context.Request.Body.Position);
    }

    private static void Add(Span span, string key, string value)
    {
        span.Attributes.Add(new KeyValue
        {
            Key = key,
            Value = new AnyValue { StringValue = value },
        });
    }
}
