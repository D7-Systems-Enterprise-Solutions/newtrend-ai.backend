using AINewtrend.Core.Ingest;
using AINewtrend.Core.Live;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:4318", "http://127.0.0.1:4319");
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.SetIsOriginAllowed(_ => true)
            .AllowAnyHeader()
            .AllowAnyMethod());
});
builder.Services.AddSingleton<ILiveStore, LiveStore>();
builder.Services.AddSingleton<IExecutionRecorder, NoOpExecutionRecorder>();
builder.Services.AddSingleton<LiveBroadcaster>();

var app = builder.Build();
app.UseCors();
app.UseWebSockets();

app.MapPost("/v1/traces", OtlpTraceEndpoint.HandleAsync);
app.Map("/live", (HttpContext context, LiveBroadcaster broadcaster) => broadcaster.HandleAsync(context));
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
