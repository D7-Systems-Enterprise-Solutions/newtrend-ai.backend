using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NewtrendAi.Core.Agent;
using NewtrendAi.Core.Live;
using NewtrendAi.Core.Performance;
using NewtrendAi.Core.Privacy;
using NewtrendAi.Core.Recording;

namespace NewtrendAi.Core.Investigation;

public sealed class InvestigationAnswer
{
    public string Summary { get; set; } = "";
    public List<Labeled> WhatHappened { get; init; } = [];
    public List<Labeled> FailureLocation { get; init; } = [];
    public List<Labeled> SuspiciousOperations { get; init; } = [];
    public List<Labeled> RecommendedInspection { get; init; } = [];
    public List<Labeled> MissingInformation { get; init; } = [];
}

public sealed class Labeled
{
    public required string Kind { get; init; }
    public required string Text { get; init; }
}

public static class InvestigationAnalysis
{
    public static InvestigationAnswer Answer(Recording.Investigation investigation, IReadOnlyList<InvestigationNote> notes, IReadOnlyList<Execution> executions)
    {
        var happened = executions.Select(execution => new Labeled
        {
            Kind = "Observed",
            Text = $"{execution.UserAction ?? execution.TraceId} {execution.Status}" + (execution.DurationMs is double duration ? $" in {Math.Round(duration)} ms" : ""),
        }).ToList();

        var failures = executions
            .Where(execution => execution.Error is not null)
            .Select(execution => new Labeled
            {
                Kind = "Observed",
                Text = $"{execution.Error!.Layer} {execution.Error.Operation}: {execution.Error.Message}",
            })
            .ToList();

        var suspicious = executions
            .SelectMany(execution => execution.Steps.Where(step => step.Status == "failed" || step.Layer == "Sql"))
            .Select(step => new Labeled
            {
                Kind = "Observed",
                Text = step.SqlText is { Length: > 0 } sql ? $"{step.Name} {sql}" : step.Name,
            })
            .ToList();

        var inspect = executions
            .SelectMany(execution => execution.Steps)
            .Where(step => step.Source?.File is not null)
            .Select(step => new Labeled
            {
                Kind = "Recommended",
                Text = $"{step.Source!.File}:{step.Source.Line} {step.Source.Function}",
            })
            .DistinctBy(item => item.Text)
            .ToList();

        var missing = new List<Labeled>();
        if (executions.Count == 0)
            missing.Add(new Labeled { Kind = "Observed", Text = "No executions are attached." });
        if (executions.All(execution => execution.Steps.All(step => step.SqlText is null)) && executions.Count > 0)
            missing.Add(new Labeled { Kind = "Observed", Text = "No SQL text was captured." });
        if (notes.Count == 0)
            missing.Add(new Labeled { Kind = "Observed", Text = "The investigation has no notes." });

        var failed = executions.FirstOrDefault(execution => execution.Status == "failed");
        var summary = failed is null
            ? $"{investigation.Name} has {executions.Count} executions and none failed."
            : $"{investigation.Name} failed at {failed.Error?.Operation ?? "a step"}.";

        return new InvestigationAnswer
        {
            Summary = summary,
            WhatHappened = happened,
            FailureLocation = failures,
            SuspiciousOperations = suspicious,
            RecommendedInspection = inspect,
            MissingInformation = missing,
        };
    }

    public static string Context(Recording.Investigation investigation, IReadOnlyList<InvestigationNote> notes, IReadOnlyList<Execution> executions, string? behavior, string? performance)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Investigation: {investigation.Name}");
        builder.AppendLine($"Objective: {investigation.Objective}");
        builder.AppendLine($"Version: {investigation.ApplicationVersion}");
        foreach (var note in notes)
            builder.AppendLine($"Note: {note.Text}");

        foreach (var execution in executions)
        {
            builder.AppendLine($"Action: {execution.UserAction}");
            builder.AppendLine($"Trace: {execution.TraceId}");
            builder.AppendLine($"Status: {execution.Status}");
            if (execution.Error is not null)
                builder.AppendLine($"Error: {execution.Error.Layer} {execution.Error.Operation} {execution.Error.Message}");
            foreach (var step in execution.Steps)
            {
                builder.AppendLine($"Step: {step.Layer} {step.Name} {step.DurationMs} ms");
                if (step.Source?.File is not null)
                    builder.AppendLine($"Source: {step.Source.File}:{step.Source.Line} {step.Source.Function}");
                if (step.SqlText is not null)
                    builder.AppendLine($"SQL: {step.SqlText}");
            }
        }

        if (behavior is not null)
            builder.AppendLine(behavior);
        if (performance is not null)
            builder.AppendLine(performance);
        builder.AppendLine("Environment: localhost");
        return Masker.Mask(builder.ToString()) ?? "";
    }

    public static byte[] Package(Recording.Investigation investigation, IReadOnlyList<InvestigationNote> notes, IReadOnlyList<Execution> executions)
    {
        var answer = Answer(investigation, notes, executions);
        var performance = executions.Select(execution => new
        {
            execution.TraceId,
            Bottleneck = PerformanceAnalyzer.Bottleneck(execution),
            SlowSql = PerformanceAnalyzer.SlowestSql(execution),
        });

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "investigation.json", new { investigation, notes });
            Write(archive, "trace.json", executions);
            Write(archive, "source-map.json", executions.SelectMany(execution => execution.Steps).Where(step => step.Source is not null).Select(step => new { step.SpanId, step.Source }));
            Write(archive, "errors.json", executions.Select(execution => execution.Error).Where(error => error is not null));
            Write(archive, "performance.json", performance);
            Write(archive, "evidence.json", executions.Select(execution =>
            {
                var findings = RootCauseEngine.Investigate(execution);
                return new
                {
                    execution.TraceId,
                    findings.Evidence,
                    findings.Hypothesis,
                    findings.Confidence,
                    findings.RelatedFiles,
                    findings.Kind,
                };
            }));
            var context = archive.CreateEntry("ai-context.txt");
            using var writer = new StreamWriter(context.Open());
            writer.Write(Context(investigation, notes, executions, null, null));
            _ = answer;
        }

        return stream.ToArray();
    }

    private static void Write(ZipArchive archive, string name, object value)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(Masker.Mask(JsonSerializer.Serialize(value, SessionStore.JsonOptions)));
    }
}
