using System;

namespace ConCli.History
{
    /// <summary>
    /// Where a session's text came from. Native wins over Terminal during dedup:
    /// a CLI's own JSONL is structured, the terminal tee is raw VT bytes.
    /// </summary>
    public enum SessionSource { Native, Terminal }

    public sealed record SessionRecord(
        string Cli,                 // "claude" | "codex" | ...
        string Id,                  // the CLI's own session id, kept verbatim
        string Cwd,                 // as displayed, original casing
        string? Title,
        DateTimeOffset StartedAt,
        DateTimeOffset UpdatedAt,
        int TurnCount,
        string? NativePath,
        string? ResumeCommand,      // e.g. claude --resume <id>
        SessionSource Source,
        // User state, not transcript state: set from the sidebar, never by an adapter.
        bool Pinned = false,
        // Which profile produced it. Not derivable from Cli: two profiles can run the same
        // claude.exe against different endpoints and different CLAUDE_CONFIG_DIRs, and
        // resuming under the wrong one looks for the session in a config dir that never had it.
        string? ProfileId = null,
        bool Favorite = false);   // 收藏 — user state, like Pinned

    public sealed record TurnRecord(string Role, string Text, DateTimeOffset? At);

    /// <summary>A working directory and how many indexed sessions ran there.</summary>
    public sealed record ProjectRecord(string Cwd, int SessionCount, DateTimeOffset UpdatedAt);

    /// <summary>Resume point for one JSONL file. See HistoryDb.ingest_cursor.</summary>
    public sealed record IngestCursor(long MtimeTicks, long ByteOffset, long FileSize);
}
