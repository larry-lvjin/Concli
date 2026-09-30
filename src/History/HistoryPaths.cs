using System;
using System.IO;

namespace ConCli.History
{
    /// <summary>
    /// Every path the history layer uses. The root is configurable on purpose:
    /// index.db must never end up inside a git repo or a synced folder (OneDrive,
    /// 坚果云) — SQLite + file sync corrupts databases.
    /// </summary>
    public static class HistoryPaths
    {
        public const string RootEnvVar = "CONCLI_HISTORY_ROOT";
        private const string DefaultRoot = @"D:\MyFile\CODE\history";

        public static string Root
        {
            get
            {
                string? configured = Environment.GetEnvironmentVariable(RootEnvVar);
                return string.IsNullOrWhiteSpace(configured) ? DefaultRoot : configured.Trim();
            }
        }

        /// <summary>The single source of truth. Project-local history/ is a projection of this.</summary>
        public static string IndexDb => Path.Combine(Root, "index.db");

        /// <summary>Raw terminal tees, one folder per pane session. The fallback location.</summary>
        public static string SessionsDir => Path.Combine(Root, "sessions");

        /// <summary>
        /// Where a working directory's own terminal records live. Opening a folder means its
        /// transcripts sit next to the code they were about, so moving the project moves them
        /// with it. The central index stays the source of truth for search.
        /// </summary>
        public static string ProjectHistoryDir(string cwd) => Path.Combine(cwd, "history");

        public static string ClaudeProjectsDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

        public static string CodexSessionsDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");

        /// <summary>
        /// The tee file for a new pane: <c>&lt;cwd&gt;\history\term_&lt;stamp&gt;\terminal.txt</c>.
        /// Falls back to the central sessions tree when that folder cannot be created — a
        /// read-only or network path must cost you the record, not the terminal.
        /// </summary>
        public static string NewTerminalTeeFile(string cwd)
        {
            string stamp = $"term_{DateTime.Now:yyyyMMdd-HHmmss-fff}";
            try
            {
                string dir = Path.Combine(ProjectHistoryDir(cwd), stamp);
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "terminal.txt");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                        or ArgumentException or NotSupportedException)
            {
                return Path.Combine(SessionsDir, stamp, "terminal.txt");
            }
        }

        /// <summary>
        /// Comparison key for a working directory: case-insensitive, no trailing separator.
        /// Kept separate from the displayed path so the sidebar can still show real casing.
        /// </summary>
        public static string CwdKey(string? cwd)
        {
            if (string.IsNullOrWhiteSpace(cwd)) return "";
            string s = cwd.Trim().Replace('/', '\\');
            if (s.Length > 3) s = s.TrimEnd('\\');
            return s.ToLowerInvariant();
        }
    }
}
