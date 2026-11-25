using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace GpcSessionBrowser;

public sealed class GpcSearchIndex
{
    private readonly string _databasePath;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    public GpcSearchIndex(string localApplicationData)
    {
        _databasePath = Path.Combine(localApplicationData, "GpcSessionBrowser", "search-index.db");
    }

    public async Task UpdateAsync(IReadOnlyList<ProjectGroup> groups, bool rebuild, CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(() => Update(groups, rebuild, cancellationToken), cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<IReadOnlyList<PromptSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => Search(query, cancellationToken), cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private void Update(IReadOnlyList<ProjectGroup> groups, bool rebuild, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        using var connection = OpenConnection();
        Initialize(connection);

        if (rebuild)
        {
            ExecuteNonQuery(connection, "DELETE FROM search_prompts; DELETE FROM indexed_sessions;");
        }

        foreach (var session in groups.SelectMany(group => group.Sessions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileInfo = new FileInfo(session.FilePath);
            if (!fileInfo.Exists)
            {
                continue;
            }

            if (!rebuild && IsCurrent(connection, session.FilePath, fileInfo))
            {
                continue;
            }

            var prompts = GpcSessionDataReader.ParsePrompts(session.FilePath, cancellationToken);
            using var transaction = connection.BeginTransaction();
            DeleteSession(connection, transaction, session.FilePath);
            InsertSession(connection, transaction, session, fileInfo);
            foreach (var prompt in prompts)
            {
                InsertPrompt(connection, transaction, session, prompt);
            }

            transaction.Commit();
        }

        RemoveMissingFiles(connection, cancellationToken);
    }

    private IReadOnlyList<PromptSearchResult> Search(string query, CancellationToken cancellationToken)
    {
        var terms = GetSearchTerms(query);
        var matchQuery = BuildMatchQuery(terms);
        if (terms.Count == 0 || !File.Exists(_databasePath))
        {
            return Array.Empty<PromptSearchResult>();
        }

        using var connection = OpenConnection();
        Initialize(connection);
        using var command = connection.CreateCommand();
        command.CommandText = ContainsCjk(query)
            ? BuildSubstringSearchSql(terms)
            : """
            SELECT s.session_id, s.project_key, s.project_root, s.file_path, s.project_hash, s.kind,
                   s.start_time, s.last_updated, s.file_size, s.title,
                   p.prompt_order, p.line_number, p.prompt_text
            FROM search_prompts p
            INNER JOIN indexed_sessions s ON s.file_path = p.file_path
            WHERE search_prompts MATCH $query
            ORDER BY rank
            LIMIT 100;
            """;
        if (ContainsCjk(query))
        {
            for (var index = 0; index < terms.Count; index++)
            {
                command.Parameters.AddWithValue($"$term{index}", terms[index]);
            }
        }
        else
        {
            command.Parameters.AddWithValue("$query", matchQuery);
        }

        using var reader = command.ExecuteReader();
        var results = new List<PromptSearchResult>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = new SessionSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
                reader.GetInt64(8),
                reader.IsDBNull(9) ? null : reader.GetString(9));
            results.Add(new PromptSearchResult(session, reader.GetInt32(10), reader.GetInt32(11), reader.GetString(12), query));
        }

        return results;
    }

    private SqliteConnection OpenConnection()
    {
        return new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = false
        }.ToString());
    }

    private static void Initialize(SqliteConnection connection)
    {
        connection.Open();
        ExecuteNonQuery(connection, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS indexed_sessions (
                file_path TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                project_key TEXT NOT NULL,
                project_root TEXT NOT NULL,
                project_hash TEXT,
                kind TEXT,
                start_time TEXT,
                last_updated TEXT,
                file_size INTEGER NOT NULL,
                file_last_write_ticks INTEGER NOT NULL,
                title TEXT
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS search_prompts USING fts5(
                file_path UNINDEXED,
                session_id UNINDEXED,
                prompt_order UNINDEXED,
                line_number UNINDEXED,
                prompt_text
            );
            """);
    }

    private static bool IsCurrent(SqliteConnection connection, string filePath, FileInfo fileInfo)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT file_size, file_last_write_ticks FROM indexed_sessions WHERE file_path = $filePath;";
        command.Parameters.AddWithValue("$filePath", filePath);
        using var reader = command.ExecuteReader();
        return reader.Read()
            && reader.GetInt64(0) == fileInfo.Length
            && reader.GetInt64(1) == fileInfo.LastWriteTimeUtc.Ticks;
    }

    private static void DeleteSession(SqliteConnection connection, SqliteTransaction transaction, string filePath)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM search_prompts WHERE file_path = $filePath; DELETE FROM indexed_sessions WHERE file_path = $filePath;";
        command.Parameters.AddWithValue("$filePath", filePath);
        command.ExecuteNonQuery();
    }

    private static void InsertSession(SqliteConnection connection, SqliteTransaction transaction, SessionSummary session, FileInfo fileInfo)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO indexed_sessions (
                file_path, session_id, project_key, project_root, project_hash, kind, start_time, last_updated,
                file_size, file_last_write_ticks, title)
            VALUES ($filePath, $sessionId, $projectKey, $projectRoot, $projectHash, $kind, $startTime, $lastUpdated,
                $fileSize, $fileLastWriteTicks, $title);
            """;
        command.Parameters.AddWithValue("$filePath", session.FilePath);
        command.Parameters.AddWithValue("$sessionId", session.SessionId);
        command.Parameters.AddWithValue("$projectKey", session.ProjectKey);
        command.Parameters.AddWithValue("$projectRoot", session.ProjectRoot);
        command.Parameters.AddWithValue("$projectHash", (object?)session.ProjectHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", (object?)session.Kind ?? DBNull.Value);
        command.Parameters.AddWithValue("$startTime", session.StartTime?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$lastUpdated", session.LastUpdated?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$fileSize", fileInfo.Length);
        command.Parameters.AddWithValue("$fileLastWriteTicks", fileInfo.LastWriteTimeUtc.Ticks);
        command.Parameters.AddWithValue("$title", (object?)session.Title ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void InsertPrompt(SqliteConnection connection, SqliteTransaction transaction, SessionSummary session, PromptRecord prompt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO search_prompts (file_path, session_id, prompt_order, line_number, prompt_text)
            VALUES ($filePath, $sessionId, $promptOrder, $lineNumber, $promptText);
            """;
        command.Parameters.AddWithValue("$filePath", session.FilePath);
        command.Parameters.AddWithValue("$sessionId", session.SessionId);
        command.Parameters.AddWithValue("$promptOrder", prompt.Order);
        command.Parameters.AddWithValue("$lineNumber", prompt.LineNumber);
        command.Parameters.AddWithValue("$promptText", prompt.Text);
        command.ExecuteNonQuery();
    }

    private static void RemoveMissingFiles(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT file_path FROM indexed_sessions;";
        using var reader = command.ExecuteReader();
        var missingFiles = new List<string>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filePath = reader.GetString(0);
            if (!File.Exists(filePath))
            {
                missingFiles.Add(filePath);
            }
        }

        foreach (var filePath in missingFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var transaction = connection.BeginTransaction();
            DeleteSession(connection, transaction, filePath);
            transaction.Commit();
        }
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> GetSearchTerms(string query)
    {
        return Regex.Split(query.Trim(), @"\s+")
            .Where(term => term.Length > 0)
            .ToArray();
    }

    private static string BuildMatchQuery(IReadOnlyList<string> terms)
    {
        return string.Join(" AND ", terms.Select(term => $"\"{term.Replace("\"", "\"\"")}\"*"));
    }

    private static string BuildSubstringSearchSql(IReadOnlyList<string> terms)
    {
        var conditions = string.Join(" AND ", terms.Select((_, index) => $"instr(p.prompt_text, $term{index}) > 0"));
        return $"""
            SELECT s.session_id, s.project_key, s.project_root, s.file_path, s.project_hash, s.kind,
                   s.start_time, s.last_updated, s.file_size, s.title,
                   p.prompt_order, p.line_number, p.prompt_text
            FROM search_prompts p
            INNER JOIN indexed_sessions s ON s.file_path = p.file_path
            WHERE {conditions}
            ORDER BY s.last_updated DESC
            LIMIT 100;
            """;
    }

    private static bool ContainsCjk(string value)
    {
        return value.Any(character =>
            character is >= '\u2E80' and <= '\u9FFF'
                or >= '\uF900' and <= '\uFAFF');
    }
}

public sealed record PromptSearchResult(SessionSummary Session, int PromptOrder, int LineNumber, string PromptText, string SearchQuery)
{
    public string DisplayName
    {
        get
        {
            var preview = PromptText.Replace("\r", " ").Replace("\n", " ").Trim();
            if (preview.Length > 110)
            {
                preview = preview[..110] + "...";
            }

            return $"{Session.ProjectKey} · {Session.Title ?? Session.SessionId} · #{PromptOrder:N0} · {preview}";
        }
    }
}
