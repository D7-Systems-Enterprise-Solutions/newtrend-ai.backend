using System.Text.Json;
using NewtrendAi.Core.Live;
using NewtrendAi.Core.Performance;
using NewtrendAi.Core.Privacy;
using Microsoft.Data.Sqlite;

namespace NewtrendAi.Core.Recording;

public sealed class Investigation
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public string? Objective { get; set; }
    public string Status { get; set; } = "Open";
    public string? ApplicationVersion { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<string> ExecutionIds { get; set; } = [];
}

public sealed class InvestigationNote
{
    public required string Id { get; init; }
    public required string InvestigationId { get; init; }
    public string? ExecutionId { get; init; }
    public string? Author { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public required string Text { get; set; }
}

public sealed class StoredBaseline
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string FeatureKey { get; init; }
    public required string ExecutionId { get; init; }
    public string? ApplicationVersion { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class PatchAction
{
    public required string Id { get; init; }
    public string? InvestigationId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string? User { get; set; }
    public List<string> Files { get; init; } = [];
    public required string Diff { get; init; }
    public string Problem { get; init; } = "";
    public List<string> Evidence { get; init; } = [];
    public string ProposedChange { get; init; } = "";
    public string ExpectedImpact { get; init; } = "";
    public bool Approved { get; set; }
    public bool Applied { get; set; }
}

public sealed class SessionStore : IExecutionRecorder, IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteConnection _connection;
    private readonly object _gate = new();

    public SessionStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
        CreateSchema();
    }

    public ValueTask RecordAsync(Execution execution, CancellationToken cancellationToken = default)
    {
        SaveExecution(execution);
        return ValueTask.CompletedTask;
    }

    public void SaveExecution(Execution execution)
    {
        Masker.MaskExecution(execution);
        var payload = JsonSerializer.Serialize(execution, JsonOptions);
        var endpoints = string.Join('\n', execution.Steps.Where(step => step.Layer == "Http").Select(step => step.Name));
        var files = string.Join('\n', execution.Steps.Select(step => step.Source?.File).Where(file => file is not null));
        var sql = string.Join('\n', execution.Steps.Select(step => step.SqlText).Where(text => text is not null));

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO executions (trace_id, payload, user_action, application_version, status, started_at, error_text, endpoints, files, sql_text)
                VALUES ($id, $payload, $action, $version, $status, $started, $error, $endpoints, $files, $sql)
                ON CONFLICT(trace_id) DO UPDATE SET
                  payload = excluded.payload,
                  user_action = excluded.user_action,
                  application_version = excluded.application_version,
                  status = excluded.status,
                  started_at = excluded.started_at,
                  error_text = excluded.error_text,
                  endpoints = excluded.endpoints,
                  files = excluded.files,
                  sql_text = excluded.sql_text;
                """;
            command.Parameters.AddWithValue("$id", execution.TraceId);
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$action", (object?)execution.UserAction ?? DBNull.Value);
            command.Parameters.AddWithValue("$version", (object?)execution.ApplicationVersion ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", execution.Status);
            command.Parameters.AddWithValue("$started", execution.StartedAt.ToString("O"));
            command.Parameters.AddWithValue("$error", (object?)execution.Error?.Message ?? DBNull.Value);
            command.Parameters.AddWithValue("$endpoints", endpoints);
            command.Parameters.AddWithValue("$files", files);
            command.Parameters.AddWithValue("$sql", sql);
            command.ExecuteNonQuery();

            foreach (var sample in PerformanceAnalyzer.Samples(execution))
            {
                using var insert = _connection.CreateCommand();
                insert.CommandText =
                    """
                    INSERT INTO performance_samples (id, operation_key, application_version, captured_at, duration_ms, status)
                    VALUES ($id, $key, $version, $captured, $duration, $status);
                    """;
                insert.Parameters.AddWithValue("$id", sample.Id);
                insert.Parameters.AddWithValue("$key", sample.OperationKey);
                insert.Parameters.AddWithValue("$version", (object?)sample.ApplicationVersion ?? DBNull.Value);
                insert.Parameters.AddWithValue("$captured", sample.CapturedAt.ToString("O"));
                insert.Parameters.AddWithValue("$duration", sample.DurationMs);
                insert.Parameters.AddWithValue("$status", sample.Status);
                insert.ExecuteNonQuery();
            }
        }
    }

    public Execution? GetExecution(string traceId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT payload FROM executions WHERE trace_id = $id;";
            command.Parameters.AddWithValue("$id", traceId);
            var payload = command.ExecuteScalar() as string;
            return payload is null ? null : JsonSerializer.Deserialize<Execution>(payload, JsonOptions);
        }
    }

    public Investigation CreateInvestigation(string name, string? objective, string? version)
    {
        var now = DateTimeOffset.UtcNow;
        var investigation = new Investigation
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = name,
            Objective = objective,
            Status = "Open",
            ApplicationVersion = version,
            CreatedAt = now,
            UpdatedAt = now,
        };

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO investigations (id, name, objective, status, application_version, created_at, updated_at)
                VALUES ($id, $name, $objective, $status, $version, $created, $updated);
                """;
            BindInvestigation(command, investigation);
            command.ExecuteNonQuery();
        }

        return investigation;
    }

    public IReadOnlyList<Investigation> SearchInvestigations(string? query)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                SELECT DISTINCT i.id, i.name, i.objective, i.status, i.application_version, i.created_at, i.updated_at
                FROM investigations i
                LEFT JOIN investigation_executions ie ON ie.investigation_id = i.id
                LEFT JOIN executions e ON e.trace_id = ie.execution_id
                WHERE $q = ''
                   OR i.name LIKE $like
                   OR ifnull(i.objective, '') LIKE $like
                   OR ifnull(e.trace_id, '') LIKE $like
                   OR ifnull(e.user_action, '') LIKE $like
                   OR ifnull(e.error_text, '') LIKE $like
                   OR ifnull(e.endpoints, '') LIKE $like
                   OR ifnull(e.files, '') LIKE $like
                   OR ifnull(e.sql_text, '') LIKE $like
                ORDER BY i.updated_at DESC;
                """;
            var text = query?.Trim() ?? "";
            command.Parameters.AddWithValue("$q", text);
            command.Parameters.AddWithValue("$like", $"%{text}%");
            return ReadInvestigations(command);
        }
    }

    public Investigation? GetInvestigation(string id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, name, objective, status, application_version, created_at, updated_at
                FROM investigations WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            var investigation = ReadInvestigations(command).FirstOrDefault();
            if (investigation is null)
                return null;

            investigation.ExecutionIds = ExecutionIds(id);
            return investigation;
        }
    }

    public Investigation? UpdateInvestigation(string id, string? name, string? objective, string? status, string? version)
    {
        var current = GetInvestigation(id);
        if (current is null)
            return null;

        current.Name = name ?? current.Name;
        current.Objective = objective ?? current.Objective;
        current.Status = status ?? current.Status;
        current.ApplicationVersion = version ?? current.ApplicationVersion;
        current.UpdatedAt = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                UPDATE investigations
                SET name = $name, objective = $objective, status = $status, application_version = $version, updated_at = $updated
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", current.Name);
            command.Parameters.AddWithValue("$objective", (object?)current.Objective ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", current.Status);
            command.Parameters.AddWithValue("$version", (object?)current.ApplicationVersion ?? DBNull.Value);
            command.Parameters.AddWithValue("$updated", current.UpdatedAt.ToString("O"));
            command.ExecuteNonQuery();
        }

        return current;
    }

    public bool DeleteInvestigation(string id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM notes WHERE investigation_id = $id;
                DELETE FROM investigation_executions WHERE investigation_id = $id;
                DELETE FROM investigations WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public void AttachExecution(string investigationId, string executionId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO investigation_executions (investigation_id, execution_id, added_at)
                VALUES ($investigation, $execution, $added)
                ON CONFLICT(investigation_id, execution_id) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$investigation", investigationId);
            command.Parameters.AddWithValue("$execution", executionId);
            command.Parameters.AddWithValue("$added", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
            Touch(investigationId);
        }
    }

    public IReadOnlyList<Execution> Timeline(string investigationId)
    {
        return ExecutionIds(investigationId)
            .Select(GetExecution)
            .Where(execution => execution is not null)
            .Cast<Execution>()
            .OrderBy(execution => execution.StartedAt)
            .ToArray();
    }

    public InvestigationNote AddNote(string investigationId, string text, string? author, string? executionId)
    {
        var note = new InvestigationNote
        {
            Id = Guid.NewGuid().ToString("n"),
            InvestigationId = investigationId,
            ExecutionId = executionId,
            Author = author,
            CreatedAt = DateTimeOffset.UtcNow,
            Text = Masker.Mask(text) ?? text,
        };

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO notes (id, investigation_id, execution_id, author, created_at, text)
                VALUES ($id, $investigation, $execution, $author, $created, $text);
                """;
            command.Parameters.AddWithValue("$id", note.Id);
            command.Parameters.AddWithValue("$investigation", note.InvestigationId);
            command.Parameters.AddWithValue("$execution", (object?)note.ExecutionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$author", (object?)note.Author ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", note.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$text", note.Text);
            command.ExecuteNonQuery();
            Touch(investigationId);
        }

        return note;
    }

    public IReadOnlyList<InvestigationNote> Notes(string investigationId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, investigation_id, execution_id, author, created_at, text
                FROM notes WHERE investigation_id = $id ORDER BY created_at;
                """;
            command.Parameters.AddWithValue("$id", investigationId);
            using var reader = command.ExecuteReader();
            var notes = new List<InvestigationNote>();
            while (reader.Read())
            {
                notes.Add(new InvestigationNote
                {
                    Id = reader.GetString(0),
                    InvestigationId = reader.GetString(1),
                    ExecutionId = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Author = reader.IsDBNull(3) ? null : reader.GetString(3),
                    CreatedAt = DateTimeOffset.Parse(reader.GetString(4)),
                    Text = reader.GetString(5),
                });
            }

            return notes;
        }
    }

    public InvestigationNote? UpdateNote(string id, string text)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE notes SET text = $text WHERE id = $id;";
            command.Parameters.AddWithValue("$text", Masker.Mask(text) ?? text);
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteNonQuery() == 0)
                return null;
        }

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id, investigation_id, execution_id, author, created_at, text FROM notes WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            return new InvestigationNote
            {
                Id = reader.GetString(0),
                InvestigationId = reader.GetString(1),
                ExecutionId = reader.IsDBNull(2) ? null : reader.GetString(2),
                Author = reader.IsDBNull(3) ? null : reader.GetString(3),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(4)),
                Text = reader.GetString(5),
            };
        }
    }

    public bool DeleteNote(string id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM notes WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public StoredBaseline SaveBaseline(string name, string featureKey, string executionId, string? version)
    {
        var baseline = new StoredBaseline
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = name,
            FeatureKey = featureKey,
            ExecutionId = executionId,
            ApplicationVersion = version,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO baselines (id, name, feature_key, execution_id, application_version, created_at)
                VALUES ($id, $name, $feature, $execution, $version, $created);
                """;
            command.Parameters.AddWithValue("$id", baseline.Id);
            command.Parameters.AddWithValue("$name", baseline.Name);
            command.Parameters.AddWithValue("$feature", baseline.FeatureKey);
            command.Parameters.AddWithValue("$execution", baseline.ExecutionId);
            command.Parameters.AddWithValue("$version", (object?)baseline.ApplicationVersion ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", baseline.CreatedAt.ToString("O"));
            command.ExecuteNonQuery();
        }

        return baseline;
    }

    public IReadOnlyList<StoredBaseline> Baselines()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id, name, feature_key, execution_id, application_version, created_at FROM baselines ORDER BY created_at DESC;";
            return ReadBaselines(command);
        }
    }

    public StoredBaseline? GetBaseline(string id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id, name, feature_key, execution_id, application_version, created_at FROM baselines WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return ReadBaselines(command).FirstOrDefault();
        }
    }

    public bool DeleteBaseline(string id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM baselines WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public void SaveDependencies(IEnumerable<Behavior.FeatureDependency> dependencies)
    {
        lock (_gate)
        {
            foreach (var dependency in dependencies)
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO feature_dependencies (feature_key, type, name, source_location, first_seen, last_seen)
                    VALUES ($feature, $type, $name, $source, $seen, $seen)
                    ON CONFLICT(feature_key, type, name) DO UPDATE SET
                      source_location = excluded.source_location,
                      last_seen = excluded.last_seen;
                    """;
                command.Parameters.AddWithValue("$feature", dependency.FeatureKey);
                command.Parameters.AddWithValue("$type", dependency.Type);
                command.Parameters.AddWithValue("$name", dependency.Name);
                command.Parameters.AddWithValue("$source", (object?)dependency.SourceLocation ?? DBNull.Value);
                command.Parameters.AddWithValue("$seen", dependency.LastSeen.ToString("O"));
                command.ExecuteNonQuery();
            }
        }
    }

    public IReadOnlyList<Behavior.FeatureDependency> Dependencies(string? featureKey)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                SELECT feature_key, type, name, source_location, first_seen, last_seen
                FROM feature_dependencies
                WHERE $feature = '' OR feature_key = $feature
                ORDER BY feature_key, type;
                """;
            command.Parameters.AddWithValue("$feature", featureKey ?? "");
            using var reader = command.ExecuteReader();
            var items = new List<Behavior.FeatureDependency>();
            while (reader.Read())
            {
                items.Add(new Behavior.FeatureDependency
                {
                    FeatureKey = reader.GetString(0),
                    Type = reader.GetString(1),
                    Name = reader.GetString(2),
                    SourceLocation = reader.IsDBNull(3) ? null : reader.GetString(3),
                    FirstSeen = DateTimeOffset.Parse(reader.GetString(4)),
                    LastSeen = DateTimeOffset.Parse(reader.GetString(5)),
                });
            }

            return items;
        }
    }

    public IReadOnlyList<PerformanceSample> PerformanceSamples(string? operationKey)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, operation_key, application_version, captured_at, duration_ms, status
                FROM performance_samples
                WHERE $key = '' OR operation_key = $key;
                """;
            command.Parameters.AddWithValue("$key", operationKey ?? "");
            using var reader = command.ExecuteReader();
            var samples = new List<PerformanceSample>();
            while (reader.Read())
            {
                samples.Add(new PerformanceSample
                {
                    Id = reader.GetString(0),
                    OperationKey = reader.GetString(1),
                    ApplicationVersion = reader.IsDBNull(2) ? null : reader.GetString(2),
                    CapturedAt = DateTimeOffset.Parse(reader.GetString(3)),
                    DurationMs = reader.GetDouble(4),
                    Status = reader.GetString(5),
                });
            }

            return samples;
        }
    }

    public void SavePatch(PatchAction action)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO patch_actions (id, investigation_id, timestamp, user_name, files, diff, problem, evidence, proposed_change, expected_impact, approved, applied)
                VALUES ($id, $investigation, $timestamp, $user, $files, $diff, $problem, $evidence, $change, $impact, $approved, $applied);
                """;
            command.Parameters.AddWithValue("$id", action.Id);
            command.Parameters.AddWithValue("$investigation", (object?)action.InvestigationId ?? DBNull.Value);
            command.Parameters.AddWithValue("$timestamp", action.Timestamp.ToString("O"));
            command.Parameters.AddWithValue("$user", (object?)action.User ?? DBNull.Value);
            command.Parameters.AddWithValue("$files", JsonSerializer.Serialize(action.Files, JsonOptions));
            command.Parameters.AddWithValue("$diff", action.Diff);
            command.Parameters.AddWithValue("$problem", action.Problem);
            command.Parameters.AddWithValue("$evidence", JsonSerializer.Serialize(action.Evidence, JsonOptions));
            command.Parameters.AddWithValue("$change", action.ProposedChange);
            command.Parameters.AddWithValue("$impact", action.ExpectedImpact);
            command.Parameters.AddWithValue("$approved", action.Approved ? 1 : 0);
            command.Parameters.AddWithValue("$applied", action.Applied ? 1 : 0);
            command.ExecuteNonQuery();
        }
    }

    public PatchAction? GetPatch(string id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, investigation_id, timestamp, user_name, files, diff, problem, evidence, proposed_change, expected_impact, approved, applied
                FROM patch_actions WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            return new PatchAction
            {
                Id = reader.GetString(0),
                InvestigationId = reader.IsDBNull(1) ? null : reader.GetString(1),
                Timestamp = DateTimeOffset.Parse(reader.GetString(2)),
                User = reader.IsDBNull(3) ? null : reader.GetString(3),
                Files = JsonSerializer.Deserialize<List<string>>(reader.GetString(4), JsonOptions) ?? [],
                Diff = reader.GetString(5),
                Problem = reader.GetString(6),
                Evidence = JsonSerializer.Deserialize<List<string>>(reader.GetString(7), JsonOptions) ?? [],
                ProposedChange = reader.GetString(8),
                ExpectedImpact = reader.GetString(9),
                Approved = reader.GetInt32(10) == 1,
                Applied = reader.GetInt32(11) == 1,
            };
        }
    }

    public void MarkPatch(string id, string? user, bool approved, bool applied)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE patch_actions SET user_name = $user, approved = $approved, applied = $applied WHERE id = $id;";
            command.Parameters.AddWithValue("$user", (object?)user ?? DBNull.Value);
            command.Parameters.AddWithValue("$approved", approved ? 1 : 0);
            command.Parameters.AddWithValue("$applied", applied ? 1 : 0);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public void Dispose() => _connection.Dispose();

    private List<string> ExecutionIds(string investigationId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT execution_id FROM investigation_executions WHERE investigation_id = $id ORDER BY added_at;";
            command.Parameters.AddWithValue("$id", investigationId);
            using var reader = command.ExecuteReader();
            var ids = new List<string>();
            while (reader.Read())
                ids.Add(reader.GetString(0));
            return ids;
        }
    }

    private void Touch(string investigationId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE investigations SET updated_at = $updated, status = CASE WHEN status = 'Open' THEN 'Investigating' ELSE status END WHERE id = $id;";
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", investigationId);
        command.ExecuteNonQuery();
    }

    private static void BindInvestigation(SqliteCommand command, Investigation investigation)
    {
        command.Parameters.AddWithValue("$id", investigation.Id);
        command.Parameters.AddWithValue("$name", investigation.Name);
        command.Parameters.AddWithValue("$objective", (object?)investigation.Objective ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", investigation.Status);
        command.Parameters.AddWithValue("$version", (object?)investigation.ApplicationVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", investigation.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", investigation.UpdatedAt.ToString("O"));
    }

    private static List<Investigation> ReadInvestigations(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var items = new List<Investigation>();
        while (reader.Read())
        {
            items.Add(new Investigation
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Objective = reader.IsDBNull(2) ? null : reader.GetString(2),
                Status = reader.GetString(3),
                ApplicationVersion = reader.IsDBNull(4) ? null : reader.GetString(4),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(5)),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(6)),
            });
        }

        return items;
    }

    private static List<StoredBaseline> ReadBaselines(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var items = new List<StoredBaseline>();
        while (reader.Read())
        {
            items.Add(new StoredBaseline
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                FeatureKey = reader.GetString(2),
                ExecutionId = reader.GetString(3),
                ApplicationVersion = reader.IsDBNull(4) ? null : reader.GetString(4),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(5)),
            });
        }

        return items;
    }

    private void CreateSchema()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS executions (
              trace_id TEXT PRIMARY KEY,
              payload TEXT NOT NULL,
              user_action TEXT,
              application_version TEXT,
              status TEXT,
              started_at TEXT,
              error_text TEXT,
              endpoints TEXT,
              files TEXT,
              sql_text TEXT
            );
            CREATE TABLE IF NOT EXISTS investigations (
              id TEXT PRIMARY KEY,
              name TEXT NOT NULL,
              objective TEXT,
              status TEXT NOT NULL,
              application_version TEXT,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS investigation_executions (
              investigation_id TEXT NOT NULL,
              execution_id TEXT NOT NULL,
              added_at TEXT NOT NULL,
              PRIMARY KEY (investigation_id, execution_id)
            );
            CREATE TABLE IF NOT EXISTS notes (
              id TEXT PRIMARY KEY,
              investigation_id TEXT NOT NULL,
              execution_id TEXT,
              author TEXT,
              created_at TEXT NOT NULL,
              text TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS baselines (
              id TEXT PRIMARY KEY,
              name TEXT NOT NULL,
              feature_key TEXT NOT NULL,
              execution_id TEXT NOT NULL,
              application_version TEXT,
              created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS feature_dependencies (
              feature_key TEXT NOT NULL,
              type TEXT NOT NULL,
              name TEXT NOT NULL,
              source_location TEXT,
              first_seen TEXT NOT NULL,
              last_seen TEXT NOT NULL,
              PRIMARY KEY (feature_key, type, name)
            );
            CREATE TABLE IF NOT EXISTS performance_samples (
              id TEXT PRIMARY KEY,
              operation_key TEXT NOT NULL,
              application_version TEXT,
              captured_at TEXT NOT NULL,
              duration_ms REAL NOT NULL,
              status TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS patch_actions (
              id TEXT PRIMARY KEY,
              investigation_id TEXT,
              timestamp TEXT NOT NULL,
              user_name TEXT,
              files TEXT NOT NULL,
              diff TEXT NOT NULL,
              problem TEXT NOT NULL,
              evidence TEXT NOT NULL,
              proposed_change TEXT NOT NULL,
              expected_impact TEXT NOT NULL,
              approved INTEGER NOT NULL,
              applied INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_executions_error ON executions(error_text);
            CREATE INDEX IF NOT EXISTS ix_executions_action ON executions(user_action);
            CREATE INDEX IF NOT EXISTS ix_samples_key ON performance_samples(operation_key, application_version);
            """;
        command.ExecuteNonQuery();
    }
}
