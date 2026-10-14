using System.Text.Json;
using NewtrendAi.Core.Agent;
using NewtrendAi.Core.Ai;
using NewtrendAi.Core.Behavior;
using NewtrendAi.Core.Investigation;
using NewtrendAi.Core.Live;
using NewtrendAi.Core.Performance;
using NewtrendAi.Core.Recording;

namespace NewtrendAi.Core.Api;

public static class ProductEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapDelete("/api/session", async (ILiveStore live, LiveBroadcaster broadcaster) =>
        {
            live.Clear();
            await broadcaster.BroadcastAsync();
            return Results.NoContent();
        });

        app.MapPost("/api/session/trace", async (HttpRequest request, ILiveStore live) =>
        {
            string? traceId = null;
            if (request.ContentLength is > 0)
            {
                var body = await request.ReadFromJsonAsync<RotateBurstBody>();
                traceId = body?.TraceId;
            }

            live.StartNewTrace(traceId);
            return Results.NoContent();
        });

        app.MapPut("/api/session/burst-limit", (BurstLimitBody body, ILiveStore live) =>
        {
            if (body.MaxItems < 1 || body.MaxItems > LiveStore.MaxBurstItems)
                return Results.BadRequest(new { error = "Maximum items must be from 1 to 350." });

            live.SetMaxBurstItems(body.MaxItems);
            return Results.NoContent();
        });

        app.MapPost("/api/investigations", (CreateInvestigation body, SessionStore store) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest(new { error = "Name is required." });

            return Results.Ok(store.CreateInvestigation(body.Name.Trim(), body.Objective, body.ApplicationVersion));
        });

        app.MapGet("/api/investigations", (string? q, SessionStore store) => store.SearchInvestigations(q));

        app.MapGet("/api/investigations/{id}", (string id, SessionStore store) =>
            store.GetInvestigation(id) is { } investigation ? Results.Ok(investigation) : Results.NotFound());

        app.MapPatch("/api/investigations/{id}", (string id, PatchInvestigation body, SessionStore store) =>
            store.UpdateInvestigation(id, body.Name, body.Objective, body.Status, body.ApplicationVersion) is { } investigation
                ? Results.Ok(investigation)
                : Results.NotFound());

        app.MapDelete("/api/investigations/{id}", (string id, SessionStore store) =>
            store.DeleteInvestigation(id) ? Results.NoContent() : Results.NotFound());

        app.MapPost("/api/investigations/{id}/executions", (string id, AttachExecution body, SessionStore store, ILiveStore live) =>
        {
            if (store.GetInvestigation(id) is null)
                return Results.NotFound();

            var execution = live.Snapshot().FirstOrDefault(item => item.TraceId == body.TraceId) ?? store.GetExecution(body.TraceId);
            if (execution is null)
                return Results.NotFound(new { error = "That execution is not in the live store." });

            store.SaveExecution(execution);
            store.AttachExecution(id, execution.TraceId);
            store.SaveDependencies(DependencyExtractor.FromExecution(execution));
            return Results.Ok(store.GetInvestigation(id));
        });

        app.MapGet("/api/investigations/{id}/timeline", (string id, SessionStore store) => store.Timeline(id));

        app.MapPost("/api/investigations/{id}/notes", (string id, CreateNote body, SessionStore store) =>
        {
            if (store.GetInvestigation(id) is null)
                return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.Text))
                return Results.BadRequest(new { error = "Text is required." });

            return Results.Ok(store.AddNote(id, body.Text, body.Author, body.ExecutionId));
        });

        app.MapGet("/api/investigations/{id}/notes", (string id, SessionStore store) => store.Notes(id));

        app.MapPatch("/api/investigation-notes/{id}", (string id, CreateNote body, SessionStore store) =>
            store.UpdateNote(id, body.Text ?? "") is { } note ? Results.Ok(note) : Results.NotFound());

        app.MapDelete("/api/investigation-notes/{id}", (string id, SessionStore store) =>
            store.DeleteNote(id) ? Results.NoContent() : Results.NotFound());

        app.MapGet("/api/investigations/{id}/package", (string id, SessionStore store) =>
        {
            var investigation = store.GetInvestigation(id);
            if (investigation is null)
                return Results.NotFound();

            var bytes = InvestigationAnalysis.Package(investigation, store.Notes(id), store.Timeline(id));
            return Results.File(bytes, "application/zip", "AI-Newtrend-investigation.zip");
        });

        app.MapPost("/api/investigations/{id}/investigate", async (string id, SessionStore store, IAiProvider ai, CancellationToken cancellationToken) =>
        {
            var investigation = store.GetInvestigation(id);
            if (investigation is null)
                return Results.NotFound();

            var notes = store.Notes(id);
            var executions = store.Timeline(id);
            var answer = InvestigationAnalysis.Answer(investigation, notes, executions);
            var context = InvestigationAnalysis.Context(investigation, notes, executions, null, null);
            var phrasing = await ai.CompleteAsync(
                "Use only the evidence. Label recommendations as recommendations. Do not invent metrics.",
                context,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(phrasing))
                answer.Summary = phrasing;
            return Results.Ok(answer);
        });

        app.MapGet("/api/executions/{id}", (string id, SessionStore store, ILiveStore live) =>
        {
            var execution = store.GetExecution(id) ?? live.Snapshot().FirstOrDefault(item => item.TraceId == id);
            return execution is null ? Results.NotFound() : Results.Ok(execution);
        });

        app.MapPost("/api/baselines", (CreateBaseline body, SessionStore store) =>
        {
            var execution = store.GetExecution(body.ExecutionId);
            if (execution is null)
                return Results.NotFound(new { error = "Record the execution before saving a baseline." });

            return Results.Ok(store.SaveBaseline(body.Name, body.FeatureKey ?? execution.UserAction ?? "Feature", body.ExecutionId, body.ApplicationVersion ?? execution.ApplicationVersion));
        });

        app.MapGet("/api/baselines", (SessionStore store) => store.Baselines());

        app.MapGet("/api/baselines/{id}", (string id, SessionStore store) =>
            store.GetBaseline(id) is { } baseline ? Results.Ok(baseline) : Results.NotFound());

        app.MapDelete("/api/baselines/{id}", (string id, SessionStore store) =>
            store.DeleteBaseline(id) ? Results.NoContent() : Results.NotFound());

        app.MapPost("/api/executions/{id}/compare/{baselineId}", (string id, string baselineId, SessionStore store) =>
        {
            var current = store.GetExecution(id);
            var baseline = store.GetBaseline(baselineId);
            var expected = baseline is null ? null : store.GetExecution(baseline.ExecutionId);
            if (current is null || expected is null)
                return Results.NotFound();

            var diff = BehaviorDiffEngine.Compare(expected, current);
            return Results.Ok(new
            {
                Kind = "Compared",
                Expected = expected.Steps.Select(OperationIdentity.Key),
                Observed = current.Steps.Select(OperationIdentity.Key),
                diff.Added,
                diff.Removed,
                diff.Changed,
                diff.Reordered,
            });
        });

        app.MapGet("/api/behavior/map", (string? feature, SessionStore store) =>
        {
            var dependencies = store.Dependencies(feature);
            var key = feature ?? dependencies.FirstOrDefault()?.FeatureKey ?? "Feature";
            return Results.Ok(DependencyExtractor.Map(key, dependencies));
        });

        app.MapGet("/api/behavior/dependencies", (string? feature, SessionStore store) => store.Dependencies(feature));

        app.MapGet("/api/performance/{executionId}", (string executionId, SessionStore store) =>
        {
            var execution = store.GetExecution(executionId);
            if (execution is null)
                return Results.NotFound();

            return Results.Ok(new
            {
                Kind = "Calculated",
                Bottleneck = PerformanceAnalyzer.Bottleneck(execution),
                Repeated = PerformanceAnalyzer.RepeatedDatabaseAccess(execution),
                SlowSql = PerformanceAnalyzer.SlowestSql(execution),
            });
        });

        app.MapGet("/api/performance/history", (string operationKey, SessionStore store) =>
        {
            var samples = store.PerformanceSamples(operationKey);
            var versions = samples.GroupBy(sample => sample.ApplicationVersion).Select(group => new
            {
                Version = group.Key,
                Metrics = PercentileCalculator.Calculate(group.Select(sample => sample.DurationMs).ToArray()),
            });
            return Results.Ok(new { Kind = "Calculated", operationKey, versions });
        });

        app.MapGet("/api/performance/compare", (string operationKey, string? versionA, string? versionB, SessionStore store) =>
        {
            var comparison = PerformanceAnalyzer.Compare(operationKey, versionA, versionB, store.PerformanceSamples(operationKey));
            return comparison is null ? Results.NotFound() : Results.Ok(comparison);
        });

        app.MapPost("/api/performance/{executionId}/explain", async (string executionId, SessionStore store, IAiProvider ai, CancellationToken cancellationToken) =>
        {
            var execution = store.GetExecution(executionId);
            if (execution is null)
                return Results.NotFound();

            var bottleneck = PerformanceAnalyzer.Bottleneck(execution);
            var slow = PerformanceAnalyzer.SlowestSql(execution);
            var facts = new
            {
                MeasuredFacts = bottleneck.Select(share => $"Calculated: {share.Layer} {Math.Round(share.DurationMs)} ms ({Math.Round(share.Share * 100)}%)"),
                LikelyContributors = bottleneck.Take(1).Select(share => $"Calculated: {share.Layer} accounts for the largest share."),
                WhatChanged = Array.Empty<string>(),
                WhereToInvestigate = slow is null ? Array.Empty<string>() : new[] { $"Observed: {slow.Handler} {slow.Repository} {slow.Statement}" },
                Recommendations = new[] { "Recommended: inspect the operation with the largest measured share." },
            };
            _ = await ai.CompleteAsync("Quote only stored measurements. Label recommendations as recommendations.", JsonSerializer.Serialize(facts), cancellationToken);
            return Results.Ok(facts);
        });

        app.MapGet("/api/agent/tools", (IEnumerable<IAgentTool> tools) =>
            tools.Select(tool => new { tool.Name, tool.Description, tool.InputSchema }));

        app.MapPost("/api/agent/tools/{name}", (string name, JsonElement body, IEnumerable<IAgentTool> tools) =>
        {
            var tool = tools.FirstOrDefault(item => item.Name == name);
            if (tool is null)
                return Results.NotFound();

            try
            {
                return Results.Ok(tool.Execute(AgentToolJson.Read(body)));
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        app.MapPost("/api/agent/mcp", async (HttpContext context, IEnumerable<IAgentTool> tools) =>
        {
            var request = await context.Request.ReadFromJsonAsync<JsonElement>();
            var method = request.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;
            if (method == "tools/list")
            {
                return Results.Ok(new
                {
                    tools = tools.Select(tool => new { name = tool.Name, description = tool.Description, inputSchema = tool.InputSchema }),
                });
            }

            if (method == "tools/call")
            {
                var name = request.GetProperty("params").GetProperty("name").GetString();
                var tool = tools.FirstOrDefault(item => item.Name == name);
                if (tool is null)
                    return Results.NotFound();

                var arguments = request.GetProperty("params").TryGetProperty("arguments", out var args) ? args : default;
                return Results.Ok(new { content = tool.Execute(AgentToolJson.Read(arguments)) });
            }

            return Results.BadRequest(new { error = "Unknown MCP method." });
        });

        app.MapPost("/api/agent/investigate", (InvestigateRequest body, SessionStore store) =>
            Results.Ok(RootCauseEngine.Investigate(store, body.ExecutionId)));
    }
}

public sealed record BurstLimitBody(int MaxItems);
public sealed record RotateBurstBody(string? TraceId);
public sealed record CreateInvestigation(string Name, string? Objective, string? ApplicationVersion);
public sealed record PatchInvestigation(string? Name, string? Objective, string? Status, string? ApplicationVersion);
public sealed record AttachExecution(string TraceId);
public sealed record CreateNote(string? Text, string? Author, string? ExecutionId);
public sealed record CreateBaseline(string Name, string? FeatureKey, string ExecutionId, string? ApplicationVersion);
public sealed record InvestigateRequest(string ExecutionId, string? InvestigationId);
