using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ConCli.History
{
    /// <summary>
    /// Reads Claude Code's own transcripts: ~/.claude/projects/&lt;escaped-cwd&gt;/&lt;sessionId&gt;.jsonl.
    /// Append-only files, one JSON object per line, so a slice can be parsed in isolation.
    /// </summary>
    public sealed class ClaudeAdapter : ITranscriptAdapter
    {
        public const string CliName = "claude";
        public string Cli => CliName;

        /// <summary>
        /// ~/.claude/projects/*/*.jsonl. A profile that moved CLAUDE_CONFIG_DIR would need its
        /// own root here; the profile layer that did that is shelved (see _attic).
        /// </summary>
        public IEnumerable<string> EnumerateFiles()
        {
            string root = HistoryPaths.ClaudeProjectsDir;
            if (!Directory.Exists(root)) yield break;
            foreach (var dir in Directory.EnumerateDirectories(root))
                foreach (var f in Directory.EnumerateFiles(dir, "*.jsonl"))
                    yield return f;
        }

        /// <summary>The filename is the session id.</summary>
        public string SessionIdFromPath(string path) => Path.GetFileNameWithoutExtension(path);

        public string ResumeCommand(string sessionId) => $"claude --resume {sessionId}";

        /// <summary>Claude's titles live in the transcript (ai-title records).</summary>
        public IReadOnlyDictionary<string, string> ExternalTitles() => new Dictionary<string, string>();

        /// <summary>
        /// Last-resort cwd recovery from the folder name Claude derives from the path
        /// (D--MyFile-CODE → D:\MyFile\CODE). Lossy — a real folder name containing '-'
        /// is indistinguishable from a separator — so it is only used when no record
        /// in the slice carried a cwd.
        /// </summary>
        public string? FallbackCwd(string path)
        {
            string dirName = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
            if (dirName.Length == 0) return null;

            string s = dirName;
            if (s.Length > 3 && char.IsLetter(s[0]) && s[1] == '-' && s[2] == '-')
                s = s[0] + ":\\" + s[3..].Replace('-', '\\');
            else
                s = s.Replace('-', '\\');
            return s;
        }

        public TranscriptSlice Parse(IEnumerable<string> lines)
        {
            var s = new TranscriptSlice();

            foreach (string line in lines)
            {
                if (TranscriptAdapters.ParseLine(line) is not { } o) continue;   // corrupt line: skip, keep going

                string type = Str(o, "type") ?? "";
                s.SessionId ??= Str(o, "sessionId");

                switch (type)
                {
                    case "ai-title":
                        s.Title = Str(o, "aiTitle") ?? s.Title;
                        continue;

                    case "user":
                    case "assistant":
                        break;

                    default:
                        continue;
                }

                // Subagent transcripts are a separate conversation; they'd swamp search results.
                if (o.TryGetProperty("isSidechain", out var sc) &&
                    sc.ValueKind == JsonValueKind.True) continue;

                s.Cwd ??= Str(o, "cwd");

                if (!o.TryGetProperty("message", out var msg) ||
                    msg.ValueKind != JsonValueKind.Object) continue;

                string text = type == "user" ? UserText(o, msg) : AssistantText(msg);
                if (text.Length == 0) continue;

                s.Add(type, text, Time(o, "timestamp"));
            }

            return s;
        }

        /// <summary>
        /// Only what a person actually typed. Tool results arrive as user records too — they
        /// carry no origin and a block list — and they are by far the bulk of the corpus.
        /// </summary>
        private static string UserText(JsonElement rec, JsonElement msg)
        {
            bool hasOrigin = rec.TryGetProperty("origin", out var origin);
            string? kind = hasOrigin && origin.ValueKind == JsonValueKind.Object
                ? Str(origin, "kind") : null;

            if (!msg.TryGetProperty("content", out var content)) return "";

            // Older transcripts predate `origin`; there, a bare string is a real prompt.
            if (kind != "human" && !(!hasOrigin && content.ValueKind == JsonValueKind.String))
                return "";

            return StripSystemReminders(Flatten(content));
        }

        private static string AssistantText(JsonElement msg)
        {
            if (!msg.TryGetProperty("content", out var content)) return "";
            return Flatten(content);   // text blocks only; thinking and tool_use are dropped
        }

        private static string Flatten(JsonElement content)
        {
            if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
            if (content.ValueKind != JsonValueKind.Array) return "";

            var sb = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                if (Str(block, "type") != "text") continue;
                string? t = Str(block, "text");
                if (string.IsNullOrEmpty(t)) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(t);
            }
            return sb.ToString();
        }

        /// <summary>
        /// &lt;system-reminder&gt; blocks are injected by the harness, not typed. Indexing them
        /// makes every session match every search.
        /// </summary>
        private static string StripSystemReminders(string text)
        {
            const string open = "<system-reminder>", close = "</system-reminder>";
            int i = text.IndexOf(open, StringComparison.Ordinal);
            if (i < 0) return text.Trim();

            var sb = new StringBuilder(text.Length);
            int pos = 0;
            while (i >= 0)
            {
                sb.Append(text, pos, i - pos);
                int j = text.IndexOf(close, i + open.Length, StringComparison.Ordinal);
                if (j < 0) { pos = text.Length; break; }
                pos = j + close.Length;
                i = text.IndexOf(open, pos, StringComparison.Ordinal);
            }
            if (pos < text.Length) sb.Append(text, pos, text.Length - pos);
            return sb.ToString().Trim();
        }

        private static string? Str(JsonElement o, string name) => TranscriptAdapters.Str(o, name);
        private static DateTimeOffset? Time(JsonElement o, string name) => TranscriptAdapters.Time(o, name);
    }
}
