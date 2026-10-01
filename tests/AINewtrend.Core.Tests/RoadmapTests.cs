using System.IO.Compression;
using Xunit;
using AINewtrend.Core.Agent;
using AINewtrend.Core.Behavior;
using AINewtrend.Core.Investigation;
using AINewtrend.Core.Live;
using AINewtrend.Core.Performance;
using AINewtrend.Core.Privacy;
using AINewtrend.Core.Recording;

namespace AINewtrend.Core.Tests;

public class RoadmapTests
{
    [Fact]
    public void Masker_hides_secrets()
    {
        var masked = Masker.Mask("Authorization: Bearer abc.def token=super-secret {\"password\":\"hunter2\"}");
        Assert.DoesNotContain("super-secret", masked);
        Assert.DoesNotContain("hunter2", masked);
        Assert.DoesNotContain("abc.def", masked);
    }

    [Fact]
    public void Save_provider_diff_reports_only_the_audit_insert()
    {
        var baseline = ExecutionOf("Save Provider", Step("Http", "POST /providers"), Step("Sql", "INSERT Provider", "INSERT INTO Provider (Name) VALUES ('Acme')"));
        var current = ExecutionOf("Save Provider", Step("Http", "POST /providers"), Step("Sql", "INSERT Provider", "INSERT INTO Provider (Name) VALUES ('Acme')"), Step("Sql", "INSERT Audit", "INSERT INTO Audit (Action) VALUES ('save')"));

        var diff = BehaviorDiffEngine.Compare(baseline, current);

        Assert.Equal(["SQL:INSERT:Audit"], diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Reordered);
    }

    [Fact]
    public void Operation_identity_ignores_sql_literals()
    {
        var first = OperationIdentity.Key(Step("Sql", "INSERT Provider", "INSERT INTO Provider (Name) VALUES ('Acme')"));
        var second = OperationIdentity.Key(Step("Sql", "INSERT Provider", "INSERT INTO Provider (Name) VALUES ('Other')"));
        Assert.Equal(first, second);
    }

    [Fact]
    public void Percentiles_use_stored_samples()
    {
        var metrics = PercentileCalculator.Calculate([100, 200, 300, 400, 500]);
        Assert.Equal(300, metrics.Average);
        Assert.Equal(500, metrics.Maximum);
        Assert.True(metrics.P95 >= metrics.P50);
    }

    [Fact]
    public void Bottleneck_is_the_largest_measured_share()
    {
        var execution = ExecutionOf(
            "Save Provider",
            Step("Http", "POST /providers", duration: 100),
            Step("Backend", "CreateProviderHandler.Handle", duration: 50),
            Step("Sql", "INSERT Provider", "INSERT INTO Provider (Id) VALUES (1)", duration: 800));

        var shares = PerformanceAnalyzer.Bottleneck(execution);

        Assert.Equal("SQL", shares[0].Layer);
        Assert.Equal(800, shares[0].DurationMs);
    }

    [Fact]
    public void Repeated_select_under_one_parent_is_a_loop()
    {
        var execution = ExecutionOf(
            "Save Provider",
            Step("Sql", "read", "SELECT Provider WHERE Id = 1", parent: "handler"),
            Step("Sql", "read", "SELECT Provider WHERE Id = 2", parent: "handler"));

        var repeated = PerformanceAnalyzer.RepeatedDatabaseAccess(execution);

        Assert.Single(repeated);
        Assert.Equal(2, repeated[0].Occurrences);
        Assert.True(repeated[0].RepeatedReadInLoop);
    }

    [Fact]
    public void Package_contains_the_required_entries_and_no_secret()
    {
        var execution = ExecutionOf("Save Provider", Step("Sql", "write", "INSERT INTO Provider (Token) VALUES ('super-secret')"));
        Masker.MaskExecution(execution);
        var investigation = new Recording.Investigation
        {
            Id = "inv",
            Name = "Provider Save Failure",
            Objective = "Investigate why Save Provider fails",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var bytes = InvestigationAnalysis.Package(investigation, [], [execution]);
        using var archive = new ZipArchive(new MemoryStream(bytes));
        var names = archive.Entries.Select(entry => entry.Name).OrderBy(name => name).ToArray();
        Assert.Equal(["ai-context.txt", "errors.json", "investigation.json", "performance.json", "source-map.json", "trace.json"], names);

        using var reader = new StreamReader(archive.GetEntry("trace.json")!.Open());
        var trace = reader.ReadToEnd();
        Assert.DoesNotContain("super-secret", trace);
    }

    [Fact]
    public void Root_cause_labels_the_hypothesis()
    {
        using var store = TempStore();
        var execution = ExecutionOf("Save Provider", Step("Backend", "CreateProviderHandler.Handle", status: "failed"));
        execution.Error = new ErrorInfo { Layer = "Backend", Operation = "CreateProviderHandler.Handle", Message = "HTTP 500" };
        execution.Status = "failed";
        execution.Steps[0].Source = new SourceLocation { File = "Handler.cs", Function = "Handle", Line = 12 };
        store.SaveExecution(execution);

        var json = System.Text.Json.JsonSerializer.Serialize(RootCauseEngine.Investigate(store, execution.TraceId));
        Assert.Contains("Inferred:", json);
        Assert.Contains("Handler.cs", json);
        Assert.Contains("Medium", json);
    }

    [Fact]
    public void Patch_does_not_write_until_the_diff_matches()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-newtrend-patch-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "Handler.cs");
        File.WriteAllText(file, "class Handler {}");
        using var store = TempStore();
        var execution = ExecutionOf("Save Provider", Step("Backend", "Handle", status: "failed"));
        execution.Steps[0].Source = new SourceLocation { File = file, Function = "Handle", Line = 1 };
        store.SaveExecution(execution);

        var proposal = FixProposalEngine.Propose(store, "inv", execution.TraceId, root);
        Assert.False(proposal.Applied);
        Assert.Equal("class Handler {}", File.ReadAllText(file));

        var rejected = Assert.Throws<InvalidOperationException>(() => FixProposalEngine.Apply(store, proposal.Id, "D7", root, "different diff"));
        Assert.Contains("does not match", rejected.Message);
        Assert.Equal("class Handler {}", File.ReadAllText(file));

        var applied = FixProposalEngine.Apply(store, proposal.Id, "D7", root, proposal.Diff);
        Assert.True(applied.Applied);
        Assert.True(applied.Approved);
        Assert.Contains("inspect this failure", File.ReadAllText(file));
    }

    [Fact]
    public void Search_finds_an_investigation_by_sql()
    {
        using var store = TempStore();
        var investigation = store.CreateInvestigation("Provider Save Failure", "why", "1.0");
        var execution = ExecutionOf("Save Provider", Step("Sql", "write", "INSERT INTO Provider (Name) VALUES ('Acme')"));
        store.SaveExecution(execution);
        store.AttachExecution(investigation.Id, execution.TraceId);

        var found = store.SearchInvestigations("INSERT INTO Provider");
        Assert.Contains(found, item => item.Id == investigation.Id);
    }

    [Fact]
    public void Version_comparison_quotes_both_percentiles()
    {
        var samples = new List<PerformanceSample>
        {
            Sample("SQL:INSERT:Provider", "2.8", 420),
            Sample("SQL:INSERT:Provider", "2.9", 1240),
        };

        var comparison = PerformanceAnalyzer.Compare("SQL:INSERT:Provider", "2.8", "2.9", samples);

        Assert.NotNull(comparison);
        Assert.Equal(420, comparison.ValueA);
        Assert.Equal(1240, comparison.ValueB);
    }

    private static SessionStore TempStore()
    {
        var path = Path.Combine(Path.GetTempPath(), "ai-newtrend-tests", Guid.NewGuid().ToString("n") + ".db");
        return new SessionStore(path);
    }

    private static PerformanceSample Sample(string key, string version, double duration) => new()
    {
        Id = Guid.NewGuid().ToString("n"),
        OperationKey = key,
        ApplicationVersion = version,
        CapturedAt = DateTimeOffset.UtcNow,
        DurationMs = duration,
        Status = "completed",
    };

    private static Execution ExecutionOf(string action, params ExecutionStep[] steps) => new()
    {
        TraceId = Guid.NewGuid().ToString("n"),
        UserAction = action,
        Status = "completed",
        StartedAt = DateTimeOffset.UtcNow,
        Steps = steps.ToList(),
    };

    private static ExecutionStep Step(string layer, string name, string? sql = null, double? duration = 10, string status = "completed", string? parent = null) => new()
    {
        SpanId = Guid.NewGuid().ToString("n"),
        ParentSpanId = parent,
        Layer = layer,
        Name = name,
        SqlText = sql,
        DurationMs = duration,
        Status = status,
        StartedAt = DateTimeOffset.UtcNow,
    };
}
