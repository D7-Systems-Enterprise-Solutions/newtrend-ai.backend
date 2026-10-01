using AINewtrend.Core.Agent;
using AINewtrend.Core.Ai;
using AINewtrend.Core.Api;
using AINewtrend.Core.Ingest;
using AINewtrend.Core.Live;
using AINewtrend.Core.Recording;

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
var databasePath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "ai-newtrend",
    "sessions.db");
builder.Services.AddSingleton(new SessionStore(databasePath));
builder.Services.AddSingleton<IExecutionRecorder>(provider => provider.GetRequiredService<SessionStore>());
builder.Services.AddSingleton<LiveBroadcaster>();
var aiEndpoint = builder.Configuration["Ai:Endpoint"];
var aiModel = builder.Configuration["Ai:Model"] ?? "local";
if (string.IsNullOrWhiteSpace(aiEndpoint))
{
    builder.Services.AddSingleton<IAiProvider, EvidenceAiProvider>();
}
else
{
    builder.Services.AddHttpClient("ai", client =>
    {
        client.BaseAddress = new Uri(aiEndpoint.TrimEnd('/') + "/");
        var key = builder.Configuration["Ai:ApiKey"];
        if (!string.IsNullOrWhiteSpace(key))
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
    });
    builder.Services.AddSingleton<IAiProvider>(provider =>
        new HttpAiProvider(provider.GetRequiredService<IHttpClientFactory>().CreateClient("ai"), aiModel));
}

builder.Services.AddSingleton<ToolContext>();
builder.Services.AddSingleton<IAgentTool, GetExecutionTool>();
builder.Services.AddSingleton<IAgentTool, GetTraceTool>();
builder.Services.AddSingleton<IAgentTool, GetErrorsTool>();
builder.Services.AddSingleton<IAgentTool, FindSourceTool>();
builder.Services.AddSingleton<IAgentTool, FindSqlTool>();
builder.Services.AddSingleton<IAgentTool, CompareBehaviorTool>();
builder.Services.AddSingleton<IAgentTool, AnalyzePerformanceTool>();
builder.Services.AddSingleton<IAgentTool, GetInvestigationTool>();

var app = builder.Build();
app.UseCors();
app.UseWebSockets();

app.MapPost("/v1/traces", OtlpTraceEndpoint.HandleAsync);
app.Map("/live", (HttpContext context, LiveBroadcaster broadcaster) => broadcaster.HandleAsync(context));
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
var workspace = builder.Configuration["Workspace"] ?? Directory.GetCurrentDirectory();
ProductEndpoints.Map(app, workspace);

app.Run();
