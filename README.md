# Newtrend-ai backend
Newtrend-ai is a modern web application designed to track, analyze, and visualize application behavior in real time. It helps you see exactly what is happening—from the frontend click all the way to the backend repository layer.

## With Newtrend-ai tool you'll get
- Full traceability from frontend to backend
- Source code file paths, so you know where to look
- Get the SQL statement along with its parameter values—ready to copy into your preferred client
- Get the cURL command along with its parameter values—ready to copy into your preferred client
- Get the parameter values immediately
- Export the trace in JSON format

## Some characteristics
Main window
![Newtrend AI Main Window](https://github.com/D7-Systems-Enterprise-Solutions/assets/raw/main/newtrend-ai/newtrend-ai-main-window.png)

SQL Query using the parameters values
![Newtrend AI Main Window](https://github.com/D7-Systems-Enterprise-Solutions/assets/blob/main/newtrend-ai/newtrend-ai-copy-feature-1.png)

Two things live here: the core that receives traces, and the .NET SDK you add to an ASP.NET Core API. The Java starter is the separate `newtrend-ai.java` sources. The browser SDK (Angular and React) is the [frontend repository](https://github.com/D7-Systems-Enterprise-Solutions/newtrend-ai.frontend).

The browser SDK sends a W3C `traceparent` header. `AddNewtrendAi` on .NET, and the Spring Boot starter on Java, continue that trace on incoming HTTP requests and export spans to the core.

**This is for development time only.** Register `AddNewtrendAi` only inside `builder.Environment.IsDevelopment()`. On Java, leave `newtrend.ai.enabled=false` except in the `dev` profile. Do not deploy the core as a production service. The collector has no authentication. Captured SQL, HTTP metadata, and request data can contain personal data or credentials. Sensitive values are masked on ingest, but masking is a safeguard, not a guarantee. Use synthetic data. Keep the core bound to loopback on your own machine.

The .NET SDK is not a NuGet package. The Java starter is not on Maven Central. You clone the sources and reference them from the application you are developing. That reference stays on your development machine.

| You are adding it to | Where the SDK is | How you reference it |
| --- | --- | --- |
| ASP.NET Core | this repository, `src/NewtrendAi.Sdk` | `ProjectReference` |
| Java (Spring Boot) | `newtrend-ai.java` | local Maven install |
| Angular | frontend repository, `sdk/angular` | `file:` dependency |
| React | frontend repository, `sdk/react` | `file:` dependency |

| Piece | Address |
| --- | --- |
| Core ingest (`POST /v1/traces`) | `http://127.0.0.1:4318` |
| Live updates (`/live`) | `ws://127.0.0.1:4319` |
| Dashboard | `http://127.0.0.1:4317` |

## Download with git

```bash
git clone https://github.com/D7-Systems-Enterprise-Solutions/newtrend-ai.backend.git
cd newtrend-ai.backend
```

Use `main`. Reference `NewtrendAi.Sdk` from that checkout. Do not pack it and publish it for a production feed.

The frontend clone, for the dashboard and for Angular or React:

```bash
git clone https://github.com/D7-Systems-Enterprise-Solutions/newtrend-ai.frontend.git
```

The Java starter is not in this git repository and is not published. Use the `newtrend-ai.java` folder (the one that contains `mvnw` and `newtrend-ai-spring-boot-starter`). Keep it next to this clone and install it only into your local Maven repository.

## Run the core

From this repository:

```bash
dotnet run --project src/NewtrendAi.Core
```

Leave it running. The core listens on `http://127.0.0.1:4318` and `http://127.0.0.1:4319`. Recorded sessions are written to a local SQLite file under your user profile (`%LOCALAPPDATA%\ai-newtrend\sessions.db` on Windows). That file is a development store on your machine. It is not a production database.

## Add it to an ASP.NET Core API

Development only. Reference the SDK project from the API you run locally. Do not add this package to a production publish.

The SDK targets `net9.0`. In your API `.csproj`, adjust the path to this clone:

```xml
<ItemGroup>
  <ProjectReference Include="C:\Sources\newtrend-ai.backend\src\NewtrendAi.Sdk\NewtrendAi.Sdk.csproj" />
</ItemGroup>
```

In `Program.cs`, register it before the app is built, and only when the host is running as Development. `builder.Environment.IsDevelopment()` is true when `ASPNETCORE_ENVIRONMENT` is `Development`, which is what `dotnet run` uses locally. Staging, Production, and any other environment name skip the registration, so a deployed host does not export traces.

```csharp
using NewtrendAi;

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddNewtrendAi(options =>
    {
        options.ServiceName = "MyApi";
        options.Endpoint = new Uri("http://127.0.0.1:4318");
    });
}
```

`ServiceName` is the name stored on backend spans. `Endpoint` is the core. The exporter appends `/v1/traces` and sends OTLP over HTTP protobuf. Export is asynchronous and does not wait on the request.

That is the whole registration. ASP.NET Core instrumentation creates the backend span for each incoming request and joins the trace from `traceparent`. On each response, the SDK reads the endpoint method and, from the PDB, its file and line. The Execution tab in the dashboard shows that path. A Debug build keeps the PDB beside the assembly. A build without symbols still shows the method name.

Every EF Core command is recorded from the EF diagnostic event, with the SQL text in `db.query.text`. You do not register anything on each `DbContext`. `options.AddNewtrendAi()` remains as a no-op so existing registrations keep compiling. Commands that do not go through EF are recorded by `OpenTelemetry.Instrumentation.SqlClient`. Around each command, the SDK also records the application call chain: function, file, and line.

`ConnectionString` is not used for that capture. Setting it has no effect:

```csharp
options.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
```

Your API still needs its own CORS policy if the browser calls it from another origin. Newtrend-ai does not replace that. Allow `traceparent`, `tracestate`, and `X-Newtrend-ai-Burst`. An allow-any-header policy already covers them.

`apps/sample/Sample.Api` is a Debug API on `http://127.0.0.1:5088` with `POST /providers`.

## Add it to a Java Spring Boot API

Development only. Do not put this starter on a production classpath, and do not attach the OpenTelemetry Java agent as well. This starter owns the tracer provider. A second agent would export a second copy of the same work.

From the `newtrend-ai.java` directory, install the starter into your local Maven repository. On Windows:

```powershell
.\mvnw.cmd -pl newtrend-ai-spring-boot-starter -am install
```

On macOS or Linux:

```bash
./mvnw -pl newtrend-ai-spring-boot-starter -am install
```

In your API `pom.xml`:

```xml
<dependency>
  <groupId>com.newtrendai</groupId>
  <artifactId>newtrend-ai-spring-boot-starter</artifactId>
  <version>0.1.5</version>
</dependency>
```

Keep the starter off unless the `dev` profile is active. The starter turns itself on when `newtrend.ai.enabled` is missing, so the default file must disable it. In `application.properties`:

```properties
newtrend.ai.enabled=false
```

In `application-dev.properties`:

```properties
newtrend.ai.enabled=true
newtrend.ai.service-name=MyApi
newtrend.ai.endpoint=http://127.0.0.1:4318
```

Run locally with the `dev` profile (`spring.profiles.active=dev`, or `--spring.profiles.active=dev`). Any other profile, including production, leaves the starter off.

`service-name` is the name stored on backend spans. `endpoint` is the core. The exporter appends `/v1/traces` and sends OTLP over HTTP protobuf.

That is the whole registration. The starter creates the backend span for each incoming request and joins the trace from `traceparent`. On each request it reads the controller method and, from the class file, its file and line. Compile with debug line numbers. Maven does that by default. The Execution tab in the dashboard shows that path.

Every JDBC statement is recorded with the SQL text in `db.statement`. `@Service` and `@Repository` methods called during the request become child steps, and the SQL span hangs off the innermost one.

The API still needs its own CORS policy if the browser calls it from another origin. Allow the `traceparent`, `tracestate`, and `X-Newtrend-AI-Burst` headers. An allow-any-header policy already covers them.

The sample API in `newtrend-ai.java/sample-api` listens on `http://127.0.0.1:5089`:

```powershell
.\mvnw.cmd -pl sample-api spring-boot:run
```

## Add it to an Angular or React app

Those packages are in the frontend repository. Clone it, build the SDK, and call `initNewtrendAi` before bootstrap. The same development-only rule applies: do not ship that call in a production build. The frontend README has the Angular and React steps.

```bash
git clone https://github.com/D7-Systems-Enterprise-Solutions/newtrend-ai.frontend.git
```

## Watch it

The dashboard is in the frontend repository:

```bash
cd ../newtrend-ai.frontend/apps/dashboard
npm install
npm run dev
```

Open `http://127.0.0.1:4317`. One user action that calls your API shows a frontend step, an HTTP step, and a backend step for that request. A SQL step appears when the API runs a statement.

## Configuration

The dashboard uses `VITE_NEW_TREND_API_URL` to choose the collector URL. The default is `http://127.0.0.1:4318`. CORS on the core allows the local dashboard and common dev-server origins (`4317`, `5173`, `4200` on `localhost` and `127.0.0.1`). For another development origin, set `Cors:AllowedOrigins` to a list of exact origins (scheme + host + port):

```json
{
  "Cors": {
    "AllowedOrigins": ["http://localhost:5173", "http://127.0.0.1:4200"]
  }
}
```

Do not set this to `*`. Do not add a production origin. The collector has no authentication and is for trusted local development only.
