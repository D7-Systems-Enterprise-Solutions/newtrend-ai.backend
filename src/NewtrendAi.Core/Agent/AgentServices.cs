using System.Text.Json;
using NewtrendAi.Core.Behavior;
using NewtrendAi.Core.Live;
using NewtrendAi.Core.Performance;
using NewtrendAi.Core.Recording;

namespace NewtrendAi.Core.Agent;

public interface IAgentTool
{
    string Name { get; }
    string Description { get; }
    object InputSchema { get; }
    object Execute(IReadOnlyDictionary<string, string?> input);
}

public sealed class ToolContext
{
    public required SessionStore Store { get; init; }
}

public sealed class GetExecutionTool(ToolContext context) : IAgentTool
{
    public string Name => "get_execution";
    public string Description => "Read one stored execution.";
    public object InputSchema => AgentToolJson.Schema("executionId");
    public object Execute(IReadOnlyDictionary<string, string?> input)
    {
        var execution = context.Store.GetExecution(AgentToolJson.Required(input, "executionId"));
        return execution ?? (object)new { found = false };
    }
}

public sealed class GetTraceTool(ToolContext context) : IAgentTool
{
    public string Name => "get_trace";
    public string Description => "Read the execution stored for a trace id.";
    public object InputSchema => AgentToolJson.Schema("traceId");
    public object Execute(IReadOnlyDictionary<string, string?> input)
    {
        var execution = context.Store.GetExecution(AgentToolJson.Required(input, "traceId"));
        return execution ?? (object)new { found = false };
    }
}

public sealed class GetErrorsTool(ToolContext context) : IAgentTool
{
    public string Name => "get_errors";
    public string Description => "Read the error on one execution.";
    public object InputSchema => AgentToolJson.Schema("executionId");
    public object Execute(IReadOnlyDictionary<string, string?> input)
    {
        var execution = context.Store.GetExecution(AgentToolJson.Required(input, "executionId"));
        return execution?.Error ?? new ErrorInfo();
    }
}

public sealed class FindSourceTool(ToolContext context) : IAgentTool
{
    public string Name => "find_source";
    public string Description => "Find steps whose source matches a file or function.";
    public object InputSchema => AgentToolJson.Schema("file", "function");
    public object Execute(IReadOnlyDictionary<string, string?> input)
    {
        var file = input.GetValueOrDefault("file");
        var function = input.GetValueOrDefault("function");
        return context.Store.SearchInvestigations(file ?? function)
            .SelectMany(investigation => context.Store.Timeline(investigation.Id))
            .SelectMany(execution => execution.Steps)
            .Where(step => (file is null || step.Source?.File?.Contains(file, StringComparison.OrdinalIgnoreCase) == true)
                && (function is null || step.Source?.Function?.Contains(function, StringComparison.OrdinalIgnoreCase) == true))
            .Select(step => step.Source)
            .Distinct()
            .ToArray();
    }
}

public sealed class FindSqlTool(ToolContext context) : IAgentTool
{
    public string Name => "find_sql";
    public string Description => "List SQL steps on one execution.";
    public object InputSchema => AgentToolJson.Schema("executionId");
    public object Execute(IReadOnlyDictionary<string, string?> input)
    {
        var execution = context.Store.GetExecution(AgentToolJson.Required(input, "executionId"));
        return execution?.Steps.Where(step => step.Layer == "Sql").Select(step => step.SqlText).ToArray() ?? [];
    }
}

public sealed class CompareBehaviorTool(ToolContext context) : IAgentTool
{
    public string Name => "compare_behavior";
    public string Description => "Compare an execution with a baseline.";
    public object InputSchema => AgentToolJson.Schema("executionId", "baselineId");
    public object Execute(IReadOnlyDictionary<string, string?> input)
    {
        var execution = context.Store.GetExecution(AgentToolJson.Required(input, "executionId"));
        var baseline = context.Store.GetBaseline(AgentToolJson.Required(input, "baselineId"));
        var expected = baseline is null ? null : context.Store.GetExecution(baseline.ExecutionId);
        if (execution is null || expected is null)
            return new { found = false };

        return BehaviorDiffEngine.Compare(expected, execution);
    }
}

public sealed class AnalyzePerformanceTool(ToolContext context) : IAgentTool
{
    public string Name => "analyze_performance";
    public string Description => "Return calculated bottleneck, repeated SQL, and the slowest SQL step.";
    public object InputSchema => AgentToolJson.Schema("executionId");
    public object Execute(IReadOnlyDictionary<string, string?> input)
    {
        var execution = context.Store.GetExecution(AgentToolJson.Required(input, "executionId"));
        if (execution is null)
            return new { found = false };

        return new
        {
            Kind = "Calculated",
            Bottleneck = PerformanceAnalyzer.Bottleneck(execution),
            Repeated = PerformanceAnalyzer.RepeatedDatabaseAccess(execution),
            SlowSql = PerformanceAnalyzer.SlowestSql(execution),
        };
    }
}

public sealed class GetInvestigationTool(ToolContext context) : IAgentTool
{
    public string Name => "get_investigation";
    public string Description => "Read one investigation, its notes, and its timeline.";
    public object InputSchema => AgentToolJson.Schema("sessionId");
    public object Execute(IReadOnlyDictionary<string, string?> input)
    {
        var id = AgentToolJson.Required(input, "sessionId");
        return new
        {
            Investigation = context.Store.GetInvestigation(id),
            Notes = context.Store.Notes(id),
            Timeline = context.Store.Timeline(id),
        };
    }
}

public static class RootCauseEngine
{
    public static object Investigate(SessionStore store, string executionId)
    {
        var execution = store.GetExecution(executionId);
        if (execution is null)
            return new { found = false };

        var failing = execution.Steps.FirstOrDefault(step => step.Status == "failed");
        var evidence = new List<string>();
        if (execution.Error is not null)
            evidence.Add($"Observed: {execution.Error.Layer} {execution.Error.Operation} {execution.Error.Message}");
        if (failing?.Source is not null)
            evidence.Add($"Observed: {failing.Source.File}:{failing.Source.Line} {failing.Source.Function}");

        foreach (var step in execution.Steps.Where(step => step.Layer is "Http" or "Sql" or "Backend"))
            evidence.Add($"Observed: {step.Layer} {step.Name}");

        var hypothesis = failing is null
            ? "Inferred: no failing step was recorded."
            : $"Inferred: the failure is associated with {failing.Name}, which is a hypothesis until the source confirms it.";

        return new
        {
            Evidence = evidence,
            Hypothesis = hypothesis,
            Confidence = failing?.Source is null ? "Low" : "Medium",
            RelatedFiles = execution.Steps.Select(step => step.Source?.File).Where(file => file is not null).Distinct().ToArray(),
            Kind = "Inferred",
        };
    }
}

public static class FixProposalEngine
{
    public static PatchAction Propose(SessionStore store, string? investigationId, string executionId, string workspace)
    {
        var cause = RootCauseEngine.Investigate(store, executionId);
        var execution = store.GetExecution(executionId);
        var file = execution?.Steps.Select(step => step.Source?.File).FirstOrDefault(path => path is not null && File.Exists(path));
        var relative = file is null ? "unknown.cs" : Path.GetRelativePath(workspace, file);
        var diff = file is null
            ? $"--- a/{relative}\n+++ b/{relative}\n@@ -1 +1 @@\n-// failing step\n+// inspect the failing step\n"
            : BuildDiff(file, workspace);

        var action = new PatchAction
        {
            Id = Guid.NewGuid().ToString("n"),
            InvestigationId = investigationId,
            Timestamp = DateTimeOffset.UtcNow,
            Files = [relative.Replace('\\', '/')],
            Diff = diff,
            Problem = execution?.Error is null ? "No recorded error." : $"{execution.Error.Operation} failed.",
            Evidence = execution?.Steps.Where(step => step.Status == "failed").Select(step => $"{step.Layer} {step.Name}").ToList() ?? [],
            ProposedChange = "Add a marker at the recorded source line so a person can inspect that step.",
            ExpectedImpact = "No behavior change. The marker is a comment.",
            Approved = false,
            Applied = false,
        };
        _ = cause;
        store.SavePatch(action);
        return action;
    }

    public static PatchAction Apply(SessionStore store, string patchId, string? user, string workspace, string? submittedDiff)
    {
        var action = store.GetPatch(patchId) ?? throw new InvalidOperationException("The patch does not exist.");
        if (action.Applied)
            return action;
        if (submittedDiff is not null && submittedDiff != action.Diff)
            throw new InvalidOperationException("The confirmation does not match the proposal.");

        foreach (var relative in action.Files)
        {
            var full = Path.GetFullPath(Path.Combine(workspace, relative));
            var root = Path.GetFullPath(workspace);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The patch leaves the workspace.");
            if (!File.Exists(full))
                throw new InvalidOperationException("The affected file is not in the workspace.");

            ApplyHunk(full, action.Diff);
        }

        store.MarkPatch(patchId, user, approved: true, applied: true);
        action.Approved = true;
        action.Applied = true;
        action.User = user;
        return action;
    }

    private static string BuildDiff(string file, string workspace)
    {
        var relative = Path.GetRelativePath(workspace, file).Replace('\\', '/');
        var lines = File.ReadAllLines(file);
        var original = lines.Length == 0 ? "" : lines[0];
        var updated = original + " // AI-Newtrend: inspect this failure";
        return $"--- a/{relative}\n+++ b/{relative}\n@@ -1,1 +1,1 @@\n-{original}\n+{updated}\n";
    }

    private static void ApplyHunk(string file, string diff)
    {
        var removed = diff.Split('\n').FirstOrDefault(line => line.StartsWith('-') && !line.StartsWith("---"));
        var added = diff.Split('\n').FirstOrDefault(line => line.StartsWith('+') && !line.StartsWith("+++"));
        if (removed is null || added is null)
            throw new InvalidOperationException("The diff has no hunk.");

        var oldLine = removed[1..];
        var newLine = added[1..];
        var text = File.ReadAllText(file);
        var replaced = text.Replace(oldLine, newLine);
        if (replaced == text)
            throw new InvalidOperationException("The file no longer matches the proposal.");

        File.WriteAllText(file, replaced);
    }
}

public static class AgentToolJson
{
    public static object Schema(params string[] names)
        => new
        {
            type = "object",
            properties = names.ToDictionary(name => name, _ => new { type = "string" }),
        };

    public static string Required(IReadOnlyDictionary<string, string?> input, string name)
        => input.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Missing {name}.");

    public static IReadOnlyDictionary<string, string?> Read(JsonElement element)
    {
        var values = new Dictionary<string, string?>();
        if (element.ValueKind != JsonValueKind.Object)
            return values;

        foreach (var property in element.EnumerateObject())
            values[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
        return values;
    }
}
