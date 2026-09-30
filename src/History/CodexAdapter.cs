using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ConCli.History
{
    /// <summary>
    /// Reads Codex CLI's transcripts: ~/.codex/sessions/YYYY/MM/DD/rollout-&lt;stamp&gt;-&lt;uuid&gt;.jsonl.
    /// Append-only JSONL like Claude's, but every record is an envelope
    /// <c>{timestamp, type, payload}</c>, and the cwd is only stated once, in the first
    /// (session_meta) record.
    /// </summary>
    public sealed class CodexAdapter : ITranscriptAdapter
    {
        public string Cli => "codex";

        public IEnumerable<string> EnumerateFiles()
        {
            string root = HistoryPaths.CodexSessionsDir;
            if (!Directory.Exists(root)) return Array.Empty<string>();
            return Directory.EnumerateFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories);
        }

        /// <summary>The uuid is the last 36 characters of the file name.</summary>
        public string SessionIdFromPath(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            return name.Length >= 36 ? name[^36..] : name;
        }

        public string ResumeCommand(string sessionId) => $"codex resume {sessionId}";

        /// <summary>The session_meta record at the top of the file carries the only cwd.</summary>
        public string? FallbackCwd(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(fs, Encoding.UTF8);
                string? first = r.ReadLine();
                return first is null ? null : Parse(new[] { first }).Cwd;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }

        /// <summary>
        /// ~/.codex/session_index.jsonl: one line per rename, so the last line for an id wins.
        /// This is where codex keeps the thread names its own resume picker shows.
        /// </summary>
        public IReadOnlyDictionary<string, string> ExternalTitles()
        {
            var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string file = Path.Combine(Path.GetDirectoryName(HistoryPaths.CodexSessionsDir)!, "session_index.jsonl");
            try
            {
                if (!File.Exists(file)) return titles;
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(fs, Encoding.UTF8);
                string? line;
                while ((line = r.ReadLine()) != null)
                {
                    if (TranscriptAdapters.ParseLine(line) is not { } o) continue;
                    if (Str(o, "id") is { Length: > 0 } id && Str(o, "thread_name") is { Length: > 0 } name)
                        titles[id] = name;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return titles;
        }

        public TranscriptSlice Parse(IEnumerable<string> lines)
        {
            var s = new TranscriptSlice();

            foreach (string line in lines)
            {
                if (TranscriptAdapters.ParseLine(line) is not { } o) continue;
                if (!o.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object) continue;

                switch (Str(o, "type"))
                {
                    case "session_meta":
                        s.SessionId ??= Str(p, "id") ?? Str(p, "session_id");
                        s.Cwd ??= Str(p, "cwd");
                        continue;

                    case "response_item":
                        break;

                    default:
                        continue;   // events, token counts, turn context: bookkeeping, not dialogue
                }

                // Only messages. Tool calls, their output and (encrypted) reasoning are dropped,
                // same as Claude's tool_use/thinking blocks. "developer" is injected instructions.
                if (Str(p, "type") != "message") continue;
                string? role = Str(p, "role");
                if (role is not ("user" or "assistant")) continue;
                if (!p.TryGetProperty("content", out var content)) continue;

                string text = Flatten(content, role == "user");
                if (text.Length == 0) continue;

                s.Add(role, text, Time(o, "timestamp"));
            }

            return s;
        }

        /// <summary>
        /// Text blocks joined. For user messages, blocks codex injects itself are dropped:
        /// they arrive as user input but nobody typed them, and every session carries them.
        /// </summary>
        private static string Flatten(JsonElement content, bool user)
        {
            if (content.ValueKind != JsonValueKind.Array) return "";

            var sb = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                if (Str(block, "type") is not ("input_text" or "output_text" or "text")) continue;
                string t = (Str(block, "text") ?? "").Trim();
                if (t.Length == 0 || user && IsInjected(t)) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(t);
            }
            return sb.ToString();
        }

        /// <summary>
        /// &lt;environment_context&gt;…, &lt;user_instructions&gt;…, the &lt;image …&gt; / &lt;/image&gt;
        /// wrappers around a pasted picture, and the AGENTS.md preamble.
        /// </summary>
        private static bool IsInjected(string t) =>
            t.StartsWith('<') && t.EndsWith('>') ||
            t.StartsWith("# AGENTS.md instructions", StringComparison.Ordinal);

        private static string? Str(JsonElement o, string name) => TranscriptAdapters.Str(o, name);
        private static DateTimeOffset? Time(JsonElement o, string name) => TranscriptAdapters.Time(o, name);
    }
}
