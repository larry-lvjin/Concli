using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using Microsoft.Data.Sqlite;

namespace ConCli.History
{
    /// <summary>
    /// The single source of truth for session history. Everything the UI reads — sidebar,
    /// search, dedup — comes from here; nothing reads the per-project projection.
    /// Not thread-safe: own one instance per worker, or serialise calls.
    /// </summary>
    public sealed class HistoryDb : IDisposable
    {
        private readonly SqliteConnection _cn;

        public HistoryDb(string? dbPath = null)
        {
            string path = dbPath ?? HistoryPaths.IndexDb;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            _cn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
            }.ToString());
            _cn.Open();

            Exec("PRAGMA journal_mode=WAL");
            Exec("PRAGMA synchronous=NORMAL");
            Exec("PRAGMA foreign_keys=ON");
            Exec("PRAGMA busy_timeout=5000");
            EnsureSchema();
        }

        // ── schema ────────────────────────────────────────────
        private void EnsureSchema()
        {
            Exec(@"
CREATE TABLE IF NOT EXISTS sessions (
  cli         TEXT    NOT NULL,
  id          TEXT    NOT NULL,
  cwd         TEXT    NOT NULL,
  cwd_key     TEXT    NOT NULL,
  title       TEXT,
  started_at  INTEGER,
  updated_at  INTEGER,
  turn_count  INTEGER NOT NULL DEFAULT 0,
  native_path TEXT,
  resume_cmd  TEXT,
  source      TEXT    NOT NULL,
  PRIMARY KEY (cli, id)
);
CREATE INDEX IF NOT EXISTS ix_sessions_cwd  ON sessions(cwd_key, updated_at DESC);
CREATE INDEX IF NOT EXISTS ix_sessions_time ON sessions(updated_at DESC);

CREATE TABLE IF NOT EXISTS turns (
  cli         TEXT    NOT NULL,
  session_id  TEXT    NOT NULL,
  seq         INTEGER NOT NULL,
  role        TEXT    NOT NULL,
  text        TEXT    NOT NULL,
  at          INTEGER,
  PRIMARY KEY (cli, session_id, seq)
);

-- trigram, not the unicode61 default: unicode61 does not segment Chinese, which
-- would make full-text search useless for most of this corpus.
CREATE VIRTUAL TABLE IF NOT EXISTS turns_fts USING fts5(
  text, content='turns', content_rowid='rowid', tokenize='trigram'
);

CREATE TRIGGER IF NOT EXISTS turns_ai AFTER INSERT ON turns BEGIN
  INSERT INTO turns_fts(rowid, text) VALUES (new.rowid, new.text);
END;
CREATE TRIGGER IF NOT EXISTS turns_ad AFTER DELETE ON turns BEGIN
  INSERT INTO turns_fts(turns_fts, rowid, text) VALUES ('delete', old.rowid, old.text);
END;
CREATE TRIGGER IF NOT EXISTS turns_au AFTER UPDATE ON turns BEGIN
  INSERT INTO turns_fts(turns_fts, rowid, text) VALUES ('delete', old.rowid, old.text);
  INSERT INTO turns_fts(rowid, text) VALUES (new.rowid, new.text);
END;

CREATE TABLE IF NOT EXISTS ingest_cursor (
  native_path TEXT PRIMARY KEY,
  mtime_ticks INTEGER NOT NULL,
  byte_offset INTEGER NOT NULL,
  file_size   INTEGER NOT NULL
);

-- Tombstones. The transcripts are owned by the CLIs, not by us, so 'delete' in the
-- sidebar means 'never index this again' — dropping the row alone would just let the
-- next scan put it straight back.
CREATE TABLE IF NOT EXISTS hidden (
  cli       TEXT    NOT NULL,
  id        TEXT    NOT NULL,
  hidden_at INTEGER NOT NULL,
  PRIMARY KEY (cli, id)
);");

            AddColumnIfMissing("sessions", "pinned", "INTEGER NOT NULL DEFAULT 0");
            Exec("CREATE INDEX IF NOT EXISTS ix_sessions_pin ON sessions(pinned DESC, updated_at DESC)");

            // Rows indexed before profiles existed have it NULL; resume falls back to the CLI name.
            AddColumnIfMissing("sessions", "profile_id", "TEXT");

            // 收藏. Separate from pinned: pinning is "keep at the top", favouriting is "keep
            // in the 收藏 list" — someone can want a session findable without it crowding
            // the top of 最近.
            AddColumnIfMissing("sessions", "favorite", "INTEGER NOT NULL DEFAULT 0");

            // User-chosen name. Its own column because ingest rewrites `title` on every new
            // ai-title record; reads prefer this one, and clearing it restores the CLI's title.
            AddColumnIfMissing("sessions", "custom_title", "TEXT");
        }

        /// <summary>Additive migration for databases created before the column existed.</summary>
        private void AddColumnIfMissing(string table, string column, string decl)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$c";
            cmd.Parameters.AddWithValue("$c", column);
            if (Convert.ToInt32(cmd.ExecuteScalar()) > 0) return;
            Exec($"ALTER TABLE {table} ADD COLUMN {column} {decl}");
        }

        // ── writes ────────────────────────────────────────────
        public SqliteTransaction Begin() => _cn.BeginTransaction();

        /// <summary>
        /// Merges a session header. Existing rows keep their earliest StartedAt and take the
        /// latest UpdatedAt; a non-null title overwrites (Claude appends ai-title records, so
        /// the last one parsed is the current one). A 'native' row is never replaced by a
        /// 'terminal' one.
        /// </summary>
        public void UpsertSession(SessionRecord s, SqliteTransaction? tx = null)
        {
            using var cmd = _cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO sessions (cli,id,cwd,cwd_key,title,started_at,updated_at,turn_count,native_path,resume_cmd,source,profile_id)
VALUES ($cli,$id,$cwd,$key,$title,$start,$upd,$tc,$np,$rc,$src,$pid)
ON CONFLICT(cli,id) DO UPDATE SET
  cwd         = CASE WHEN excluded.source='native' OR sessions.source<>'native'
                     THEN excluded.cwd ELSE sessions.cwd END,
  cwd_key     = CASE WHEN excluded.source='native' OR sessions.source<>'native'
                     THEN excluded.cwd_key ELSE sessions.cwd_key END,
  title       = COALESCE(excluded.title, sessions.title),
  started_at  = MIN(sessions.started_at, excluded.started_at),
  updated_at  = MAX(sessions.updated_at, excluded.updated_at),
  turn_count  = excluded.turn_count,
  native_path = COALESCE(excluded.native_path, sessions.native_path),
  resume_cmd  = COALESCE(excluded.resume_cmd, sessions.resume_cmd),
  profile_id  = COALESCE(excluded.profile_id, sessions.profile_id),
  source      = CASE WHEN sessions.source='native' THEN 'native' ELSE excluded.source END;";
            cmd.Parameters.AddWithValue("$cli", s.Cli);
            cmd.Parameters.AddWithValue("$id", s.Id);
            cmd.Parameters.AddWithValue("$cwd", s.Cwd);
            cmd.Parameters.AddWithValue("$key", HistoryPaths.CwdKey(s.Cwd));
            cmd.Parameters.AddWithValue("$title", (object?)s.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$start", s.StartedAt.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$upd", s.UpdatedAt.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$tc", s.TurnCount);
            cmd.Parameters.AddWithValue("$np", (object?)s.NativePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rc", (object?)s.ResumeCommand ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$src", s.Source == SessionSource.Native ? "native" : "terminal");
            cmd.Parameters.AddWithValue("$pid", (object?)s.ProfileId ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        public int NextTurnSeq(string cli, string sessionId, SqliteTransaction? tx = null)
        {
            using var cmd = _cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "SELECT COALESCE(MAX(seq),-1)+1 FROM turns WHERE cli=$cli AND session_id=$id";
            cmd.Parameters.AddWithValue("$cli", cli);
            cmd.Parameters.AddWithValue("$id", sessionId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        public void AppendTurns(string cli, string sessionId, int startSeq,
                                IReadOnlyList<TurnRecord> turns, SqliteTransaction? tx = null)
        {
            if (turns.Count == 0) return;

            using var cmd = _cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT OR REPLACE INTO turns (cli,session_id,seq,role,text,at)
                                VALUES ($cli,$id,$seq,$role,$text,$at)";
            var pCli  = cmd.Parameters.Add("$cli",  SqliteType.Text);
            var pId   = cmd.Parameters.Add("$id",   SqliteType.Text);
            var pSeq  = cmd.Parameters.Add("$seq",  SqliteType.Integer);
            var pRole = cmd.Parameters.Add("$role", SqliteType.Text);
            var pText = cmd.Parameters.Add("$text", SqliteType.Text);
            var pAt   = cmd.Parameters.Add("$at",   SqliteType.Integer);

            pCli.Value = cli;
            pId.Value = sessionId;
            for (int i = 0; i < turns.Count; i++)
            {
                pSeq.Value  = startSeq + i;
                pRole.Value = turns[i].Role;
                pText.Value = turns[i].Text;
                pAt.Value   = (object?)turns[i].At?.ToUnixTimeMilliseconds() ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Drops a session's turns. Used when a JSONL file is rewritten from scratch.</summary>
        public void DeleteTurns(string cli, string sessionId, SqliteTransaction? tx = null)
        {
            using var cmd = _cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM turns WHERE cli=$cli AND session_id=$id";
            cmd.Parameters.AddWithValue("$cli", cli);
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.ExecuteNonQuery();
        }

        // ── user state: pin / hide ────────────────────────────
        public void SetPinned(string cli, string id, bool pinned)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "UPDATE sessions SET pinned=$p WHERE cli=$cli AND id=$id";
            cmd.Parameters.AddWithValue("$p", pinned ? 1 : 0);
            cmd.Parameters.AddWithValue("$cli", cli);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        public void SetFavorite(string cli, string id, bool favorite)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "UPDATE sessions SET favorite=$f WHERE cli=$cli AND id=$id";
            cmd.Parameters.AddWithValue("$f", favorite ? 1 : 0);
            cmd.Parameters.AddWithValue("$cli", cli);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        /// <summary>Null or blank drops the custom name and falls back to the CLI's title.</summary>
        public void SetCustomTitle(string cli, string id, string? title)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "UPDATE sessions SET custom_title=$t WHERE cli=$cli AND id=$id";
            cmd.Parameters.AddWithValue("$t", string.IsNullOrWhiteSpace(title) ? DBNull.Value : title.Trim());
            cmd.Parameters.AddWithValue("$cli", cli);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// The CLI's own title (not the user's 重命名, which lives in custom_title and wins
        /// on display). No-op when unchanged, so re-applying every scan is cheap.
        /// </summary>
        public void SetNativeTitle(string cli, string id, string title)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "UPDATE sessions SET title=$t WHERE cli=$cli AND id=$id AND title IS NOT $t";
            cmd.Parameters.AddWithValue("$t", title);
            cmd.Parameters.AddWithValue("$cli", cli);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        public bool IsHidden(string cli, string id)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM hidden WHERE cli=$cli AND id=$id";
            cmd.Parameters.AddWithValue("$cli", cli);
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() != null;
        }

        public int HiddenCount => Scalar("SELECT COUNT(*) FROM hidden");

        /// <summary>
        /// Drops a session from the index and tombstones it. The CLI's own transcript file is
        /// left alone, so this is undoable via <see cref="UnhideAll"/> + a rescan — which is
        /// why the cursor goes too: without that the rescan would find nothing new to read.
        /// </summary>
        public void HideSession(string cli, string id, string? nativePath)
        {
            using var tx = Begin();
            Run(tx, "INSERT OR REPLACE INTO hidden (cli,id,hidden_at) VALUES ($cli,$id,$t)",
                ("$cli", cli), ("$id", id), ("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            Run(tx, "DELETE FROM turns WHERE cli=$cli AND session_id=$id", ("$cli", cli), ("$id", id));
            Run(tx, "DELETE FROM sessions WHERE cli=$cli AND id=$id", ("$cli", cli), ("$id", id));
            if (!string.IsNullOrEmpty(nativePath))
                Run(tx, "DELETE FROM ingest_cursor WHERE native_path=$p", ("$p", nativePath));
            tx.Commit();
        }

        /// <summary>Lifts every tombstone. The next ingest re-reads those files from offset 0.</summary>
        public int UnhideAll()
        {
            int n = HiddenCount;
            Exec("DELETE FROM hidden");
            return n;
        }

        private void Run(SqliteTransaction? tx, string sql, params (string Name, object Value)[] args)
        {
            using var cmd = _cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
            cmd.ExecuteNonQuery();
        }

        // ── cursor ────────────────────────────────────────────
        public IngestCursor? GetCursor(string nativePath)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText =
                "SELECT mtime_ticks, byte_offset, file_size FROM ingest_cursor WHERE native_path=$p";
            cmd.Parameters.AddWithValue("$p", nativePath);
            using var r = cmd.ExecuteReader();
            return r.Read() ? new IngestCursor(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)) : null;
        }

        public void SetCursor(string nativePath, IngestCursor c, SqliteTransaction? tx = null)
        {
            using var cmd = _cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO ingest_cursor (native_path,mtime_ticks,byte_offset,file_size)
                                VALUES ($p,$m,$o,$s)
                                ON CONFLICT(native_path) DO UPDATE SET
                                  mtime_ticks=excluded.mtime_ticks,
                                  byte_offset=excluded.byte_offset,
                                  file_size=excluded.file_size";
            cmd.Parameters.AddWithValue("$p", nativePath);
            cmd.Parameters.AddWithValue("$m", c.MtimeTicks);
            cmd.Parameters.AddWithValue("$o", c.ByteOffset);
            cmd.Parameters.AddWithValue("$s", c.FileSize);
            cmd.ExecuteNonQuery();
        }

        // ── reads ─────────────────────────────────────────────
        public List<SessionRecord> RecentSessions(int limit = 200, string? cwd = null,
                                                  bool favoritesOnly = false)
        {
            using var cmd = _cn.CreateCommand();
            bool byCwd = !string.IsNullOrWhiteSpace(cwd);
            var where = new List<string>();
            if (byCwd) where.Add("cwd_key=$key");
            if (favoritesOnly) where.Add("favorite=1");
            cmd.CommandText = SelectSessions +
                (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") +
                OrderSessions;
            if (byCwd) cmd.Parameters.AddWithValue("$key", HistoryPaths.CwdKey(cwd));
            cmd.Parameters.AddWithValue("$n", limit);
            return ReadSessions(cmd);
        }

        /// <summary>
        /// Full-text search over turn bodies and titles, newest session first.
        /// </summary>
        /// <remarks>
        /// The trigram index cannot represent a fragment shorter than 3 characters, and
        /// 2-character CJK queries ("重构", "终端") are the common case here — MATCH returns
        /// nothing for them. Those fall back to a LIKE scan, which gives identical results
        /// at the cost of reading the turns table; measured at ~1 ms per 1000 turns, and
        /// SQLite offers no indexed alternative for CJK bigrams.
        /// </remarks>
        public List<SessionRecord> Search(string query, int limit = 200, bool favoritesOnly = false)
        {
            string q = (query ?? "").Trim();
            if (q.Length == 0) return RecentSessions(limit, favoritesOnly: favoritesOnly);

            bool indexed = q.Length >= 3;

            string body = indexed
                // MATCH only binds to the table's real name, never to an alias.
                ? @"SELECT 1 FROM turns_fts JOIN turns t ON t.rowid = turns_fts.rowid
                    WHERE turns_fts MATCH $q
                      AND t.cli = sessions.cli AND t.session_id = sessions.id"
                : @"SELECT 1 FROM turns t
                    WHERE t.text LIKE $like ESCAPE '\'
                      AND t.cli = sessions.cli AND t.session_id = sessions.id";

            using var cmd = _cn.CreateCommand();
            cmd.CommandText = SelectSessions +
                $" WHERE (EXISTS ({body}) OR COALESCE(custom_title, title) LIKE $like ESCAPE '\\')" +
                (favoritesOnly ? " AND favorite=1" : "") +
                OrderSessions;

            if (indexed)
                cmd.Parameters.AddWithValue("$q", "\"" + q.Replace("\"", "\"\"") + "\"");
            cmd.Parameters.AddWithValue("$like", "%" + Escape(q) + "%");
            cmd.Parameters.AddWithValue("$n", limit);
            return ReadSessions(cmd);
        }

        /// <summary>
        /// One row per working directory, newest activity first — the 文件夹 view's top level.
        /// Grouped on cwd_key so "D:\X" and "d:\x\" are one folder; the displayed cwd is the
        /// casing of that folder's most recent session.
        /// </summary>
        public List<ProjectRecord> Projects()
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText =
                @"SELECT (SELECT s2.cwd FROM sessions s2 WHERE s2.cwd_key = s.cwd_key
                          ORDER BY s2.updated_at DESC LIMIT 1),
                         COUNT(*), MAX(updated_at)
                  FROM sessions s GROUP BY cwd_key ORDER BY MAX(updated_at) DESC";
            var list = new List<ProjectRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new ProjectRecord(r.GetString(0), r.GetInt32(1),
                    DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2))));
            return list;
        }

        public List<TurnRecord> TurnsOf(string cli, string sessionId)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText =
                "SELECT role,text,at FROM turns WHERE cli=$cli AND session_id=$id ORDER BY seq";
            cmd.Parameters.AddWithValue("$cli", cli);
            cmd.Parameters.AddWithValue("$id", sessionId);
            var list = new List<TurnRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new TurnRecord(r.GetString(0), r.GetString(1),
                    r.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2))));
            return list;
        }

        public int SessionCount => Scalar("SELECT COUNT(*) FROM sessions");
        public int TurnCount    => Scalar("SELECT COUNT(*) FROM turns");

        private const string SelectSessions =
            "SELECT cli,id,cwd,COALESCE(custom_title,title),started_at,updated_at,turn_count,native_path,resume_cmd,source,pinned,profile_id,favorite FROM sessions";

        /// <summary>Pinned first, then newest. Both list queries share it so grouping stays stable.</summary>
        private const string OrderSessions = " ORDER BY pinned DESC, updated_at DESC LIMIT $n";

        private static List<SessionRecord> ReadSessions(SqliteCommand cmd)
        {
            var list = new List<SessionRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new SessionRecord(
                    r.GetString(0), r.GetString(1), r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)),
                    DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)),
                    r.GetInt32(6),
                    r.IsDBNull(7) ? null : r.GetString(7),
                    r.IsDBNull(8) ? null : r.GetString(8),
                    r.GetString(9) == "native" ? SessionSource.Native : SessionSource.Terminal,
                    r.GetInt32(10) != 0,
                    r.IsDBNull(11) ? null : r.GetString(11),
                    r.GetInt32(12) != 0));
            return list;
        }

        // LIKE has its own wildcards; a literal search for "100%" must not match everything.
        private static string Escape(string s) =>
            s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

        private int Scalar(string sql)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        private void Exec(string sql)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        public void Dispose() => _cn.Dispose();
    }
}
