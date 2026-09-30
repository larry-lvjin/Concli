using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace ConCli.History
{
    /// <summary>
    /// One CLI's transcript format. Supporting another CLI (gemini, qwen, …) means writing one
    /// of these and adding it to <see cref="TranscriptAdapters.All"/> — the ingester, the index
    /// and the sidebar are format-agnostic.
    /// </summary>
    public interface ITranscriptAdapter
    {
        /// <summary>Key stored in sessions.cli. Never change it: it is half the primary key.</summary>
        string Cli { get; }

        /// <summary>Every transcript file this CLI has written. Missing roots yield nothing.</summary>
        IEnumerable<string> EnumerateFiles();

        /// <summary>
        /// The session id a file belongs to, from its path alone — checked against tombstones
        /// before any parsing.
        /// </summary>
        string SessionIdFromPath(string path);

        /// <summary>
        /// Parses complete lines appended since the last ingest. A slice may start mid-file,
        /// so fields a later slice does not repeat (cwd, id) come back null.
        /// </summary>
        TranscriptSlice Parse(IEnumerable<string> lines);

        /// <summary>
        /// Working directory when the slice did not carry one. May read the file's head; must
        /// not throw for an unreadable file — null means "unknown".
        /// </summary>
        string? FallbackCwd(string path);

        string ResumeCommand(string sessionId);

        /// <summary>
        /// Titles the CLI keeps outside the transcript (codex's session_index), by session id.
        /// Re-applied on every scan, because a rename there does not touch the transcript file.
        /// </summary>
        IReadOnlyDictionary<string, string> ExternalTitles();
    }

    /// <summary>What one slice of a transcript contributed. Fields are null when the slice didn't say.</summary>
    public sealed class TranscriptSlice
    {
        public string? SessionId;
        public string? Cwd;
        public string? Title;
        public DateTimeOffset? FirstAt;
        public DateTimeOffset? LastAt;
        public readonly List<TurnRecord> Turns = new();

        public void Add(string role, string text, DateTimeOffset? at)
        {
            if (at.HasValue)
            {
                if (!FirstAt.HasValue || at < FirstAt) FirstAt = at;
                if (!LastAt.HasValue  || at > LastAt)  LastAt  = at;
            }
            Turns.Add(new TurnRecord(role, text, at));
        }
    }

    public static class TranscriptAdapters
    {
        /// <summary>Every CLI the sidebar indexes, in scan order.</summary>
        public static IReadOnlyList<ITranscriptAdapter> All { get; } = new ITranscriptAdapter[]
        {
            new ClaudeAdapter(),
            new CodexAdapter(),
        };

        // ── JSON helpers shared by the JSONL adapters ─────────────

        /// <summary>One JSONL line as an object, or null for a truncated / corrupt line.</summary>
        internal static JsonElement? ParseLine(string line)
        {
            if (line.Length == 0) return null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var o = doc.RootElement.Clone();
                return o.ValueKind == JsonValueKind.Object ? o : null;
            }
            catch (JsonException) { return null; }
        }

        internal static string? Str(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        internal static DateTimeOffset? Time(JsonElement o, string name)
        {
            string? raw = Str(o, name);
            return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                       DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
                ? t : null;
        }
    }
}
