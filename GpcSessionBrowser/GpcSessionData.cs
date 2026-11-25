using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace GpcSessionBrowser;

public static class GpcSessionDataReader
{
    public static IReadOnlyList<ProjectGroup> ScanSessionSummaries(string gpcRoot, CancellationToken cancellationToken)
    {
        var tmpRoot = Path.Combine(gpcRoot, "tmp");
        if (!Directory.Exists(tmpRoot))
        {
            return Array.Empty<ProjectGroup>();
        }

        var projectRootsFromIndex = LoadProjectsIndex(gpcRoot);
        var groups = new List<ProjectGroup>();

        foreach (var projectDirectory in Directory.EnumerateDirectories(tmpRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var projectKey = Path.GetFileName(projectDirectory);
            var projectRoot = ReadProjectRoot(projectDirectory)
                ?? projectRootsFromIndex.GetValueOrDefault(projectKey)
                ?? projectDirectory;
            var chatsDirectory = Path.Combine(projectDirectory, "chats");
            if (!Directory.Exists(chatsDirectory))
            {
                continue;
            }

            var sessions = new List<SessionSummary>();
            foreach (var chatFile in Directory.EnumerateFiles(chatsDirectory, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var summary = TryReadSessionSummary(chatFile, projectKey, projectRoot);
                if (summary is not null)
                {
                    sessions.Add(summary);
                }
            }

            if (sessions.Count == 0)
            {
                continue;
            }

            sessions.Sort((left, right) => Nullable.Compare(right.LastUpdated, left.LastUpdated));
            groups.Add(new ProjectGroup(projectKey, projectRoot, sessions));
        }

        return groups
            .OrderByDescending(group => group.LastUpdated)
            .ThenBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(group => group.ProjectKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<PromptRecord> ParsePrompts(string filePath, CancellationToken cancellationToken)
    {
        var prompts = new List<PromptRecord>();
        var seenPromptTexts = new HashSet<string>(StringComparer.Ordinal);
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!string.Equals(TryGetString(root, "type"), "user", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var text = ExtractUserPromptText(root);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var normalizedText = NormalizePromptTextForDeduplication(text);
                if (!seenPromptTexts.Add(normalizedText))
                {
                    continue;
                }

                prompts.Add(new PromptRecord(prompts.Count + 1, lineNumber, text));
            }
            catch (JsonException)
            {
                // Ignore isolated malformed JSONL records and keep the session browsable.
            }
        }

        return prompts;
    }

    // Aggregate token usage per day across a set of sessions.
    // This attempts to read message-level "usage" objects and timestamp fields when present.
    public static Dictionary<DateOnly, long> AggregateTokenUsageByDate(IReadOnlyList<ProjectGroup> groups, CancellationToken cancellationToken)
    {
        var result = new Dictionary<DateOnly, long>();
        foreach (var session in groups.SelectMany(g => g.Sessions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var stream = new FileStream(session.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                while (reader.ReadLine() is { } line)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var root = document.RootElement;

                        // Find token usage in common fields: usage.total_tokens, usage.totalTokens, or sum of prompt_tokens + completion_tokens
                        long tokens = 0;
                        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                        {
                            if (TryGetLongFromProperty(usage, "total_tokens", out var v) || TryGetLongFromProperty(usage, "totalTokens", out v) || TryGetLongFromProperty(usage, "total", out v) )
                            {
                                tokens = v;
                            }
                            else
                            {
                                var p = 0L; var c = 0L;
                                TryGetLongFromProperty(usage, "prompt_tokens", out p);
                                TryGetLongFromProperty(usage, "completion_tokens", out c);
                                TryGetLongFromProperty(usage, "promptTokens", out p);
                                TryGetLongFromProperty(usage, "completionTokens", out c);
                                tokens = p + c;
                            }
                        }

                        if (tokens == 0)
                        {
                            // No explicit usage; skip
                            continue;
                        }

                        // Determine timestamp for the record. Try common properties.
                        DateTimeOffset? timestamp = null;
                        if (TryGetString(root, "time") is string ts) timestamp = ParseTimestamp(ts);
                        else if (TryGetString(root, "timestamp") is string ts2) timestamp = ParseTimestamp(ts2);
                        else if (TryGetString(root, "createdAt") is string ts3) timestamp = ParseTimestamp(ts3);
                        else if (session.LastUpdated.HasValue) timestamp = session.LastUpdated.Value;
                        else continue; // skip records without a usable timestamp

                        var day = DateOnly.FromDateTime(timestamp.Value.LocalDateTime.Date);
                        result[day] = result.GetValueOrDefault(day) + tokens;
                    }
                    catch (JsonException)
                    {
                        // ignore
                    }
                }
            }
            catch
            {
                // ignore file read issues per-session
            }
        }

        return result;
    }

    // Fallback estimator: estimate token usage per prompt by text length (chars / 4)
    public static Dictionary<DateOnly, long> EstimateTokenUsageByDate(IReadOnlyList<ProjectGroup> groups, CancellationToken cancellationToken)
    {
        var result = new Dictionary<DateOnly, long>();
        foreach (var session in groups.SelectMany(g => g.Sessions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var prompts = ParsePrompts(session.FilePath, cancellationToken);
                var day = session.LastUpdated.HasValue ? DateOnly.FromDateTime(session.LastUpdated.Value.LocalDateTime.Date) : (DateOnly?)null;
                if (!day.HasValue) continue;
                long sum = 0;
                foreach (var p in prompts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var estimated = Math.Max(1, p.Text.Length / 4);
                    sum += estimated;
                }

                if (sum > 0)
                {
                    result[day.Value] = result.GetValueOrDefault(day.Value) + sum;
                }
            }
            catch
            {
                // ignore per-session failures
            }
        }

        return result;
    }

    public static Dictionary<DateOnly, long> ComputeTokenUsageForSession(SessionSummary session, CancellationToken cancellationToken)
    {
        var result = new Dictionary<DateOnly, long>();
        try
        {
            using var stream = new FileStream(session.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;

                    long tokens = 0;
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        if (TryGetLongFromProperty(usage, "total_tokens", out var v) || TryGetLongFromProperty(usage, "totalTokens", out v) || TryGetLongFromProperty(usage, "total", out v))
                        {
                            tokens = v;
                        }
                        else
                        {
                            var p = 0L; var c = 0L;
                            TryGetLongFromProperty(usage, "prompt_tokens", out p);
                            TryGetLongFromProperty(usage, "completion_tokens", out c);
                            TryGetLongFromProperty(usage, "promptTokens", out p);
                            TryGetLongFromProperty(usage, "completionTokens", out c);
                            tokens = p + c;
                        }
                    }

                    DateTimeOffset? timestamp = null;
                    if (TryGetString(root, "time") is string ts) timestamp = ParseTimestamp(ts);
                    else if (TryGetString(root, "timestamp") is string ts2) timestamp = ParseTimestamp(ts2);
                    else if (TryGetString(root, "createdAt") is string ts3) timestamp = ParseTimestamp(ts3);
                    else if (session.LastUpdated.HasValue) timestamp = session.LastUpdated.Value;

                    if (tokens > 0 && timestamp.HasValue)
                    {
                        var day = DateOnly.FromDateTime(timestamp.Value.LocalDateTime.Date);
                        result[day] = result.GetValueOrDefault(day) + tokens;
                    }
                }
                catch (JsonException)
                {
                    // ignore malformed records
                }
            }

            // If no explicit usage was found, fall back to estimator (based on prompts and session last updated)
            if (result.Count == 0)
            {
                try
                {
                    var prompts = ParsePrompts(session.FilePath, cancellationToken);
                    var day = session.LastUpdated.HasValue ? DateOnly.FromDateTime(session.LastUpdated.Value.LocalDateTime.Date) : (DateOnly?)null;
                    if (day.HasValue)
                    {
                        long sum = 0;
                        foreach (var p in prompts)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var estimated = Math.Max(1, p.Text.Length / 4);
                            sum += estimated;
                        }

                        if (sum > 0) result[day.Value] = result.GetValueOrDefault(day.Value) + sum;
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch
        {
            // ignore file access issues
        }

        return result;
    }

    private static bool TryGetLongFromProperty(JsonElement el, string name, out long value)
    {
        value = 0;
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty(name, out var prop)) return false;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var n))
        {
            value = n; return true;
        }
        if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), out var p))
        {
            value = p; return true;
        }
        return false;
    }

    private static DateTimeOffset? ParseTimestamp(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        return DateTimeOffset.TryParse(input, out var parsed) ? parsed : null;
    }

    public static string? ExtractUserPromptText(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("text", out var textElement)
                && textElement.ValueKind == JsonValueKind.String)
            {
                var text = textElement.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    parts.Add(text);
                }
            }
        }

        return parts.Count == 0 ? null : string.Join(Environment.NewLine, parts);
    }

    public static string QuotePowerShell(string value)
    {
        return $"'{value.Replace("'", "''")}'";
    }

    private static Dictionary<string, string> LoadProjectsIndex(string gpcRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var projectsFile = Path.Combine(gpcRoot, "projects.json");
        if (!File.Exists(projectsFile))
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(projectsFile));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            if (document.RootElement.TryGetProperty("projects", out var projects)
                && projects.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in projects.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var projectKey = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(projectKey))
                        {
                            result[projectKey] = property.Name;
                        }
                    }
                }
            }
        }
        catch
        {
            // The per-project .project_root files remain authoritative; ignore a malformed index.
        }

        return result;
    }

    private static string? ReadProjectRoot(string projectDirectory)
    {
        var projectRootFile = Path.Combine(projectDirectory, ".project_root");
        if (!File.Exists(projectRootFile))
        {
            return null;
        }

        try
        {
            var root = File.ReadAllText(projectRootFile).Trim();
            return string.IsNullOrWhiteSpace(root) ? null : root;
        }
        catch
        {
            return null;
        }
    }

    private static SessionSummary? TryReadSessionSummary(string chatFile, string projectKey, string projectRoot)
    {
        try
        {
            using var header = TryReadSessionHeader(chatFile);
            if (header is null && new FileInfo(chatFile).Length == 0)
            {
                return null;
            }

            var fileInfo = new FileInfo(chatFile);
            var root = header?.RootElement;
            var sessionId = root.HasValue
                ? TryGetString(root.Value, "sessionId") ?? Path.GetFileNameWithoutExtension(chatFile)
                : Path.GetFileNameWithoutExtension(chatFile);

            return new SessionSummary(
                SessionId: sessionId,
                ProjectKey: projectKey,
                ProjectRoot: projectRoot,
                FilePath: chatFile,
                ProjectHash: root.HasValue ? TryGetString(root.Value, "projectHash") : null,
                Kind: root.HasValue ? TryGetString(root.Value, "kind") : null,
                StartTime: root.HasValue ? TryGetDateTimeOffset(root.Value, "startTime") : null,
                LastUpdated: (root.HasValue ? TryGetDateTimeOffset(root.Value, "lastUpdated") : null) ?? fileInfo.LastWriteTime,
                FileSizeBytes: fileInfo.Length,
                Title: TryReadFirstUserPromptPreview(chatFile));
        }
        catch
        {
            return null;
        }
    }

    private static JsonDocument? TryReadSessionHeader(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        JsonDocument? firstObject = null;
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    document.Dispose();
                    continue;
                }

                if (TryGetString(document.RootElement, "sessionId") is not null)
                {
                    firstObject?.Dispose();
                    return document;
                }

                firstObject ??= document;
                if (!ReferenceEquals(firstObject, document))
                {
                    document.Dispose();
                }
            }
            catch (JsonException)
            {
                // Ignore malformed records while finding the session metadata header.
            }
        }

        return firstObject;
    }

    private static string? TryReadFirstUserPromptPreview(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!string.Equals(TryGetString(root, "type"), "user", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var text = ExtractUserPromptText(root);
                return string.IsNullOrWhiteSpace(text) ? null : ToSingleLinePreview(text, 96);
            }
            catch (JsonException)
            {
                // Ignore malformed records while finding the title.
            }
        }

        return null;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        var value = TryGetString(element, propertyName);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string NormalizePromptTextForDeduplication(string text)
    {
        using var reader = new StringReader(text);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line.TrimEnd());
        }

        return string.Join(Environment.NewLine, lines).Trim();
    }

    private static string ToSingleLinePreview(string text, int maxLength)
    {
        using var reader = new StringReader(text);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                lines.Add(trimmed);
            }
        }

        var preview = string.Join(" ", lines);
        return preview.Length <= maxLength ? preview : preview[..maxLength] + "...";
    }
}

public sealed record ProjectGroup(string ProjectKey, string ProjectRoot, IReadOnlyList<SessionSummary> Sessions)
{
    public DateTimeOffset? LastUpdated => Sessions.Max(session => session.LastUpdated);

    public string DisplayName
    {
        get
        {
            try
            {
                var name = Path.GetFileName(ProjectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return string.IsNullOrWhiteSpace(name) ? ProjectKey : name;
            }
            catch
            {
                return ProjectKey;
            }
        }
    }
}

public sealed record SessionSummary(
    string SessionId,
    string ProjectKey,
    string ProjectRoot,
    string FilePath,
    string? ProjectHash,
    string? Kind,
    DateTimeOffset? StartTime,
    DateTimeOffset? LastUpdated,
    long FileSizeBytes,
    string? Title)
{
    public string DisplayName => $"{FormatDate(LastUpdated)}  {Title ?? SessionId}";

    private static string FormatDate(DateTimeOffset? value)
    {
        return value?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "(no date)";
    }
}

public sealed record PromptRecord(int Order, int LineNumber, string Text, string SearchQuery = "")
{
    public string Preview
    {
        get
        {
            using var reader = new StringReader(Text);
            var firstLine = string.Empty;
            while (reader.ReadLine() is { } line)
            {
                firstLine = line.Trim();
                if (firstLine.Length > 0)
                {
                    break;
                }
            }

            if (firstLine.Length > 120)
            {
                firstLine = firstLine[..120] + "...";
            }

            return $"#{Order:N0}  {firstLine}";
        }
    }
}
