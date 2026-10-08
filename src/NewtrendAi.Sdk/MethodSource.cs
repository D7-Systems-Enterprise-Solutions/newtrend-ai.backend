using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace NewtrendAi;

internal sealed class MethodSourceStartupFilter(IHttpContextAccessor accessor) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        => app =>
        {
            MethodSource.Accessor = accessor;
            app.Use(async (context, nextDelegate) =>
            {
                try
                {
                    await nextDelegate();
                }
                finally
                {
                    var activity = Activity.Current;
                    if (activity is { IsAllDataRequested: true, IsStopped: false })
                        MethodSource.Apply(activity, context.Response);
                }
            });
            next(app);
        };
}

internal static class MethodSource
{
    private static readonly Dictionary<MethodBase, Located> Cache = new();

    internal static IHttpContextAccessor? Accessor { get; set; }

    internal static HttpContext? CurrentHttpContext => Accessor?.HttpContext;

    internal static string? EndpointFunction(HttpContext? context)
    {
        if (context is null)
            return null;

        var method = FindMethod(context);
        if (method is null)
            return null;

        var located = Locate(method);
        var activity = Activity.Current;
        if (activity is { IsAllDataRequested: true, IsStopped: false })
            Write(activity, located);
        return located.Function;
    }

    public static void Apply(Activity activity, HttpResponse response)
    {
        var method = FindMethod(response.HttpContext) as MethodBase;
        if (method is null)
            return;

        Write(activity, Locate(method));
    }

    private static void Write(Activity activity, Located located)
    {
        activity.SetTag("code.function.name", located.Function);
        if (located.File is not null)
            activity.SetTag("code.file.path", located.File);
        if (located.Line is int line)
            activity.SetTag("code.line.number", line);
    }

    private static MethodInfo? FindMethod(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is null)
            return null;

        MethodInfo? fallback = null;
        foreach (var item in endpoint.Metadata)
        {
            if (item is MethodInfo method)
            {
                if (IsApplication(method))
                    return method;
                fallback ??= method;
                continue;
            }

            var property = item.GetType().GetProperty("MethodInfo");
            if (property?.GetValue(item) is not MethodInfo action)
                continue;

            if (IsApplication(action))
                return action;
            fallback ??= action;
        }

        return fallback;
    }

    private static bool IsApplication(MethodInfo method)
    {
        var ns = method.DeclaringType?.Namespace ?? "";
        return !ns.StartsWith("Microsoft.", StringComparison.Ordinal)
            && !ns.StartsWith("System.", StringComparison.Ordinal)
            && !ns.StartsWith("OpenTelemetry.", StringComparison.Ordinal);
    }

    internal static Located Locate(MethodBase method)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(method, out var cached))
                return cached;
        }

        var located = Read(method);
        lock (Cache)
            Cache[method] = located;
        return located;
    }

    private static Located Read(MethodBase method)
    {
        string function;
        try
        {
            function = method.DeclaringType is null
                ? method.Name
                : $"{method.DeclaringType.FullName}.{method.Name}";
        }
        catch (Exception)
        {
            function = method.Name;
        }

        string? modulePath;
        try
        {
            modulePath = method.Module.FullyQualifiedName;
        }
        catch (Exception)
        {
            return new Located(function, null, null);
        }

        if (string.IsNullOrEmpty(modulePath) || !File.Exists(modulePath))
            return new Located(function, null, null);

        try
        {
            using var stream = File.OpenRead(modulePath);
            using var pe = new PEReader(stream);
            if (TrySidecar(modulePath, method, out var sidecar))
                return sidecar with { Function = function };
            if (TryEmbedded(pe, method, out var embedded))
                return embedded with { Function = function };
        }
        catch (IOException)
        {
        }
        catch (BadImageFormatException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return new Located(function, null, null);
    }

    private static bool TrySidecar(string modulePath, MethodBase method, out Located located)
    {
        located = default;
        var pdbPath = Path.ChangeExtension(modulePath, ".pdb");
        if (!File.Exists(pdbPath))
            return false;

        using var stream = File.OpenRead(pdbPath);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        return TryPoints(provider.GetMetadataReader(), method, out located);
    }

    private static bool TryEmbedded(PEReader pe, MethodBase method, out Located located)
    {
        located = default;
        foreach (var entry in pe.ReadDebugDirectory())
        {
            if (entry.Type != DebugDirectoryEntryType.EmbeddedPortablePdb)
                continue;

            using var provider = pe.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
            return TryPoints(provider.GetMetadataReader(), method, out located);
        }

        return false;
    }

    private static bool TryPoints(MetadataReader reader, MethodBase method, out Located located)
    {
        located = default;
        var entity = MetadataTokens.EntityHandle(method.MetadataToken);
        if (entity.Kind != HandleKind.MethodDefinition)
            return false;

        var handle = (MethodDefinitionHandle)entity;
        foreach (var point in reader.GetMethodDebugInformation(handle).GetSequencePoints())
        {
            if (point.IsHidden || point.Document.IsNil || point.StartLine is 0 or SequencePoint.HiddenLine)
                continue;

            var document = reader.GetDocument(point.Document);
            var file = reader.GetString(document.Name);
            if (string.IsNullOrWhiteSpace(file))
                continue;

            located = new Located("", file, point.StartLine);
            return true;
        }

        return false;
    }

    internal readonly record struct Located(string Function, string? File, int? Line);
}
