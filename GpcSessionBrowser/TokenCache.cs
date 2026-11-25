using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace GpcSessionBrowser
{
    internal class TokenCache
    {
        public DateTimeOffset CachedAt { get; set; }
        public string ScannerVersion { get; set; } = "1";

        // sessionId -> entry
        public Dictionary<string, SessionEntry> Sessions { get; set; } = new();

        // aggregate per-day keys are ISO date strings (yyyy-MM-dd)
        public Dictionary<string, long> Aggregate { get; set; } = new();

        public static TokenCache LoadFromFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return new TokenCache { CachedAt = DateTimeOffset.MinValue };
                var txt = File.ReadAllText(path);
                var opt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var cache = JsonSerializer.Deserialize<TokenCache>(txt, opt);
                return cache ?? new TokenCache { CachedAt = DateTimeOffset.MinValue };
            }
            catch
            {
                return new TokenCache { CachedAt = DateTimeOffset.MinValue };
            }
        }

        public static void SaveToFileAtomic(TokenCache cache, string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path) ?? string.Empty;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var opt = new JsonSerializerOptions { WriteIndented = true };
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(cache, opt));
                // Atomic replace
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch
            {
                // best-effort; ignore failures
            }
        }
    }

    internal class SessionEntry
    {
        public string FilePath { get; set; } = string.Empty;
        public DateTimeOffset LastWriteUtc { get; set; }
        public Dictionary<string, long> PerDayTokens { get; set; } = new();
    }
}
