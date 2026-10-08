using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NewtrendAi;

/// <summary>
/// Observes every EF Core command in the process. The SQL span is created here,
/// from the same diagnostic EF Core uses for "Executed DbCommand", so capture
/// does not depend on a per-DbContext interceptor or on SqlClient events.
/// </summary>
internal static class EfCommandChainListener
{
    private static readonly ConcurrentDictionary<Guid, Activity[]> Inflight = new();
    private static readonly AsyncLocal<int> EfDepth = new();

    internal static bool InEfCommand => EfDepth.Value > 0;

    private static int _started;

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;

        DiagnosticListener.AllListeners.Subscribe(new Listeners());
    }

    private sealed class Listeners : IObserver<DiagnosticListener>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(DiagnosticListener value)
        {
            if (value.Name != "Microsoft.EntityFrameworkCore")
                return;

            value.Subscribe(
                new Commands(),
                static (_, _, _) => true);
        }
    }

    private sealed class Commands :
        IObserver<KeyValuePair<string, object?>>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            try
            {
                if (value.Value is not CommandEventData commandEvent)
                    return;

                switch (value.Key)
                {
                    case "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuting":
                        Begin(commandEvent);
                        break;

                    case "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted":
                    case "Microsoft.EntityFrameworkCore.Database.Command.CommandError":
                    case "Microsoft.EntityFrameworkCore.Database.Command.CommandCanceled":
                        End(commandEvent.CommandId);
                        break;
                }
            }
            catch (Exception)
            {
                // A capture failure must not break the command.
            }
        }
    }

    private static void Begin(CommandEventData eventData)
    {
        if (eventData.CommandId == Guid.Empty || Inflight.ContainsKey(eventData.CommandId))
            return;

        var endpoint =
            MethodSource.EndpointFunction(
                MethodSource.CurrentHttpContext);

        var opened = new List<Activity>(CallChain.BeginBackendChain(endpoint));
        var command = eventData.Command;
        var parameters = command.Parameters
            .Cast<System.Data.Common.DbParameter>()
            .Select(parameter => (parameter.ParameterName, parameter.Value));
        var text = CallChain.InlineParameters(command.CommandText, parameters);
        var sql = CallChain.BeginSql(text);
        if (sql is not null)
        {
            EfDepth.Value++;
            opened.Add(sql);
        }

        if (opened.Count > 0)
            Inflight[eventData.CommandId] = opened.ToArray();
    }

    private static void End(Guid commandId)
    {
        if (commandId == Guid.Empty)
            return;

        if (!Inflight.TryRemove(commandId, out var activities))
            return;

        if (activities.Any(activity => activity.GetTagItem("ai.newtrend.layer") as string == "sql")
            && EfDepth.Value > 0)
            EfDepth.Value--;

        CallChain.End(activities);
    }
}
