# AI-Newtrend for an ASP.NET Core API

Two things live here: the core that receives traces, and the .NET SDK you add to any API you want to watch.

The Angular app sends a W3C `traceparent` header. `AddAINewtrend` continues that trace on incoming HTTP requests and exports spans to the core. SQL statements are captured by the official SqlClient instrumentation for every database command in the process. `ConnectionString` is accepted and unused.

| Piece | Address |
| --- | --- |
| Core ingest (`POST /v1/traces`) | `http://127.0.0.1:4318` |
| Live updates (`/live`) | `ws://127.0.0.1:4319` |
| Dashboard | `http://127.0.0.1:4317` |

## 1. Run the core

From this repository:

```bash
dotnet run --project src/AINewtrend.Core
```

Leave it running. Live executions stay in memory and disappear when this process stops. Nothing is written to a database.

## 2. Add the SDK to your API

The SDK is not a NuGet package yet. Reference the project from your API `.csproj`. Adjust the path to this repository:

```xml
<ItemGroup>
  <ProjectReference Include="C:\Sources\ai-newtrend\ai-newtrend.backend\src\AINewtrend.Sdk\AINewtrend.Sdk.csproj" />
</ItemGroup>
```

In `Program.cs`, register it before the app is built:

```csharp
using AINewtrend;

builder.Services.AddAINewtrend(options =>
{
    options.ServiceName = "MyApi";
    options.Endpoint = new Uri("http://127.0.0.1:4318");
});
```

`ServiceName` is the name stored on backend spans. `Endpoint` is the core. The exporter appends `/v1/traces` and sends OTLP over HTTP protobuf. Export is asynchronous and does not wait on the request.

That is the whole registration. ASP.NET Core instrumentation creates the backend span for each incoming request and joins the trace from `traceparent`. On each response, the SDK reads the endpoint method and, from the PDB, its file and line. The Execution tab in the dashboard shows that path. A Debug build keeps the PDB beside the assembly. A build without symbols still shows the method name.

Every EF Core command is recorded by the SDK from the EF diagnostic event, with the SQL text in `db.query.text`. You do not register anything on each `DbContext`. `options.AddAINewtrend()` remains as a no-op so existing registrations keep compiling. Commands that do not go through EF are recorded by `OpenTelemetry.Instrumentation.SqlClient`. Around each command, the SDK also records the application call chain: function, file, and line.

`ConnectionString` is not used for that capture. Setting it has no effect:

```csharp
options.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
```

## 3. Watch it

The dashboard is in the frontend repository:

```bash
cd ../ai-newtrend.FrontEnd/apps/dashboard
npm install
npm run dev
```

Open `http://127.0.0.1:4317`. Use the Angular app. One user action that calls your API shows a frontend step, an HTTP step, and a backend step for that request.

Your API still needs its own CORS policy if the browser calls it from another origin. AI-Newtrend does not replace that.
