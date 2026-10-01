using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace AINewtrend;

internal static class SqlCommandListener
{
    private static readonly AsyncLocal<Chain?> Active = new();
    private static int _started;
    internal static IHttpContextAccessor? Accessor { get; set; }

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
            if (value.Name is "Microsoft.EntityFrameworkCore" or "SqlClientDiagnosticListener")
                value.Subscribe(new Commands(value.Name == "Microsoft.EntityFrameworkCore" ? "ef" : "sql"));
        }
    }

    private sealed class Commands(string family) : IObserver<KeyValuePair<string, object?>>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (IsBefore(value.Key))
            {
                Begin(value.Value);
                return;
            }

            if (IsAfter(value.Key))
                End();
        }

        private void Begin(object? payload)
        {
            if (Active.Value is not null)
                return;

            var sql = RenderCommand(payload);
            if (string.IsNullOrWhiteSpace(sql))
                return;

            var endpoint = Activity.Current?.GetTagItem("code.function.name") as string
                ?? MethodSource.EndpointFunction(Accessor?.HttpContext);
            var activities = CallChain.Begin(sql, endpoint);
            if (activities.Length == 0)
                return;

            Active.Value = new Chain(family, activities);
        }

        private void End()
        {
            var chain = Active.Value;
            if (chain is null || chain.Family != family)
                return;

            Active.Value = null;
            CallChain.End(chain.Activities);
        }
    }

    private static bool IsBefore(string name)
        => name is "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuting"
            or "Microsoft.Data.SqlClient.WriteCommandBefore"
            or "System.Data.SqlClient.WriteCommandBefore";

    private static bool IsAfter(string name)
        => name is "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted"
            or "Microsoft.EntityFrameworkCore.Database.Command.CommandError"
            or "Microsoft.Data.SqlClient.WriteCommandAfter"
            or "Microsoft.Data.SqlClient.WriteCommandError"
            or "System.Data.SqlClient.WriteCommandAfter"
            or "System.Data.SqlClient.WriteCommandError";

    internal static string? RenderCommand(object? payload)
    {
        var command = FindCommand(payload);
        if (command is null)
            return null;

        var text = Read(command, "CommandText") as string;
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var parameters = ReadParameters(command);
        return parameters.Count == 0 ? text : Inline(text, parameters);
    }

    private static object? FindCommand(object? payload)
    {
        if (payload is null)
            return null;

        if (HasCommand(payload))
            return payload;

        foreach (var property in payload.GetType().GetProperties())
        {
            if (property.GetIndexParameters().Length > 0)
                continue;

            var value = property.GetValue(payload);
            if (value is not null && !ReferenceEquals(value, payload) && HasCommand(value))
                return value;
        }

        return null;
    }

    private static bool HasCommand(object value)
        => Property(value.GetType(), "CommandText") is not null
           && Property(value.GetType(), "Parameters") is not null;

    private static List<(string Name, object? Value)> ReadParameters(object command)
    {
        var parameters = new List<(string Name, object? Value)>();
        if (Read(command, "Parameters") is not IEnumerable items)
            return parameters;

        foreach (var item in items)
        {
            if (item is null)
                continue;

            var name = Read(item, "ParameterName") as string;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            parameters.Add((name, Read(item, "Value")));
        }

        return parameters;
    }

    internal static string Inline(string sql, IReadOnlyList<(string Name, object? Value)> parameters)
    {
        var lookup = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters.OrderByDescending(parameter => parameter.Name.Length))
        {
            var name = parameter.Name.StartsWith('@') ? parameter.Name : "@" + parameter.Name;
            lookup[name] = parameter.Value;
        }

        if (lookup.Count == 0)
            return sql;

        var pattern = string.Join("|", lookup.Keys.Select(Regex.Escape));
        return Regex.Replace(sql, pattern + @"\b", match => Literal(match.Value, lookup[match.Value]));
    }

    private static string Literal(string name, object? value)
    {
        if (IsSecret(name) || value is null || value == DBNull.Value)
            return IsSecret(name) ? "N'[masked]'" : "NULL";

        return value switch
        {
            bool flag => flag ? "1" : "0",
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
                => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL",
            Guid id => $"'{id}'",
            DateTime date => $"'{date:yyyy-MM-dd HH:mm:ss.fff}'",
            DateTimeOffset date => $"'{date:yyyy-MM-dd HH:mm:ss.fff zzz}'",
            byte[] bytes => "0x" + Convert.ToHexString(bytes),
            _ => "N'" + value.ToString()?.Replace("'", "''") + "'",
        };
    }

    private static object? Read(object target, string name)
        => Property(target.GetType(), name)?.GetValue(target);

    private static PropertyInfo? Property(Type type, string name)
    {
        PropertyInfo? match = null;
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.Name != name || property.GetIndexParameters().Length > 0)
                continue;

            if (match is null || Distance(property.DeclaringType) < Distance(match.DeclaringType))
                match = property;
        }

        return match;
    }

    private static int Distance(Type? type)
    {
        var distance = 0;
        while (type is not null)
        {
            distance++;
            type = type.BaseType;
        }

        return distance;
    }

    private static bool IsSecret(string name)
        => name.Contains("password", StringComparison.OrdinalIgnoreCase)
           || name.Contains("token", StringComparison.OrdinalIgnoreCase)
           || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
           || name.Contains("apikey", StringComparison.OrdinalIgnoreCase)
           || name.Contains("api_key", StringComparison.OrdinalIgnoreCase);

    private sealed record Chain(string Family, Activity[] Activities);
}
