using System.Diagnostics;
using System.Runtime.CompilerServices;
using AINewtrend;
using Xunit;

namespace HostApplication;

public class SqlCaptureTests
{
    [Fact]
    public void Backend_chain_keeps_function_file_line_information()
    {
        var stopped = Listen();

        using var request = new Activity("test-request").Start();

        var activities = CallChain.BeginBackendChain(endpointFunction: null);

        Assert.NotEmpty(activities);

        CallChain.End(activities);
        request.Stop();

        var backend = stopped
            .Where(activity => activity.GetTagItem("ai.newtrend.layer") as string == "backend")
            .Where(activity => activity.GetTagItem("code.function.name") is string function
                && function.StartsWith("HostApplication.", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(backend);

        Assert.All(backend, activity =>
        {
            Assert.NotNull(activity.GetTagItem("code.function.name"));
            Assert.NotNull(activity.GetTagItem("code.file.path"));
            Assert.True(Convert.ToInt32(activity.GetTagItem("code.line.number")) > 0);
        });
    }

    [Fact]
    public void Sql_text_replaces_parameter_names_with_values()
    {
        var sql = CallChain.InlineParameters(
            """
            WHERE [p0].[WorkspaceId] = @__workspaceId_0 AND [h].[StartsAt] <= @__today_1 AND [h].[HierarchyNodeId] IN (
                SELECT [p1].[value] FROM OPENJSON(@__projectNodeIds_3)
            ) AND [h].[Count] = @__today_10
            """,
            [
                ("@__workspaceId_0", Guid.Parse("11111111-1111-1111-1111-111111111111")),
                ("@__today_1", new DateTime(2026, 10, 6)),
                ("@__today_10", 3),
                ("@__projectNodeIds_3", "[\"22222222-2222-2222-2222-222222222222\"]"),
            ]);

        Assert.Contains("'11111111-1111-1111-1111-111111111111'", sql);
        Assert.Contains("'2026-10-06 00:00:00.0000000'", sql);
        Assert.Contains("'[\"22222222-2222-2222-2222-222222222222\"]'", sql);
        Assert.Contains("[h].[Count] = 3", sql);
        Assert.DoesNotContain("@__workspaceId_0", sql);
        Assert.DoesNotContain("@__today_1", sql);
        Assert.DoesNotContain("@__today_10", sql);
    }

    [Fact]
    public void Sql_span_is_recorded_for_an_unrecorded_parent()
    {
        var stopped = Listen();
        var noise = new Activity("unrecorded");
        noise.Start();
        try
        {
            Assert.False(noise.Recorded);
            var sql = CallChain.BeginSql(
                "SELECT [f].[HierarchyNodeId] FROM [financial].[FinancialCharges] AS [f]");
            Assert.NotNull(sql);
            sql.Stop();
        }
        finally
        {
            noise.Stop();
        }

        var span = Assert.Single(stopped, activity =>
            activity.GetTagItem("db.query.text") is string text
            && text.Contains("FinancialCharges", StringComparison.Ordinal));
        Assert.True(span.Recorded);
        Assert.Equal("sql", span.GetTagItem("ai.newtrend.layer"));
    }

    [Fact]
    public void Backend_chain_is_nested()
    {
        Listen();

        using var request = new Activity("test-request").Start();

        var activities = Outer();

        Assert.True(activities.Length >= 2);

        for (var i = 1; i < activities.Length; i++)
        {
            Assert.Equal(activities[i - 1].SpanId, activities[i].ParentSpanId);
        }

        CallChain.End(activities);
        request.Stop();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Activity[] Outer() => Inner();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Activity[] Inner() => CallChain.BeginBackendChain(endpointFunction: null);

    private static List<Activity> Listen()
    {
        var stopped = new List<Activity>();

        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = source => source.Name == CallChain.SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add,
        });

        return stopped;
    }
}
