using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ConCli.History
{
    public sealed record IngestStats(int FilesSeen, int FilesRead, int TurnsAdded, int Errors)
    {
        public override string ToString() =>
            $"{FilesRead}/{FilesSeen} files, +{TurnsAdded} turns" + (Errors > 0 ? $", {Errors} errors" : "");
    }

    /// <summary>
    /// Pulls native CLI transcripts into index.db. Incremental: each file is resumed from its
    /// stored byte offset, so a rescan over a few hundred MB only touches what was appended.
    /// Call from a background thread — it does synchronous file I/O by design.
    /// </summary>
    public static class HistoryIngest
    {
        /// <summary>Scans every CLI in <paramref name="adapters"/> (default: all known ones).</summary>
        public static IngestStats Run(
            HistoryDb db,
            IReadOnlyList<ITranscriptAdapter>? adapters = null,
            CancellationToken ct = default)
        {
            int seen = 0, read = 0, turns = 0, errors = 0;

            foreach (var ad in adapters ?? TranscriptAdapters.All)
            {
                IReadOnlyDictionary<string, string> titles;
                try { titles = ad.ExternalTitles(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { titles = new Dictionary<string, string>(); }

                IEnumerable<string> files;
                try { files = ad.EnumerateFiles().ToList(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { errors++; continue; }

                foreach (string path in files)
                {
                    ct.ThrowIfCancellationRequested();
                    seen++;
                    try
                    {
                        int n = IngestFile(db, ad, path, titles);
                        if (n >= 0) { read++; turns += n; }
                    }
                    catch (IOException) { errors++; }            // being written right now; next pass gets it
                    catch (UnauthorizedAccessException) { errors++; }
                }

                // A rename in the CLI's own index leaves the transcript untouched, so the
                // cursor check above would never notice it.
                foreach (var (id, title) in titles) db.SetNativeTitle(ad.Cli, id, title);
            }

            return new IngestStats(seen, read, turns, errors);
        }

        /// <summary>Returns turns added, or -1 if the file was unchanged and skipped.</summary>
        private static int IngestFile(HistoryDb db, ITranscriptAdapter ad, string path,
                                      IReadOnlyDictionary<string, string> titles)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return -1;

            // A tombstoned session is skipped before any parsing. Deliberately does not write
            // a cursor: lifting the tombstone must leave the file looking unread, otherwise
            // the restore would find nothing to do.
            string pathId = ad.SessionIdFromPath(path);
            if (db.IsHidden(ad.Cli, pathId)) return -1;

            long mtime = fi.LastWriteTimeUtc.Ticks;
            long size = fi.Length;

            var cursor = db.GetCursor(path);
            if (cursor != null && cursor.MtimeTicks == mtime && cursor.FileSize == size)
                return -1;

            // Shrunk => rewritten from scratch, not appended. Anything we indexed is stale.
            bool restart = cursor == null || size < cursor.ByteOffset;
            long offset = restart ? 0 : cursor!.ByteOffset;

            var (lines, newOffset) = ReadNewLines(path, offset);
            if (lines.Count == 0 && !restart)
            {
                db.SetCursor(path, new IngestCursor(mtime, newOffset, size));
                return 0;
            }

            var slice = ad.Parse(lines);

            string sessionId = slice.SessionId ?? pathId;
            string cwd = slice.Cwd ?? ad.FallbackCwd(path) ?? "";

            // Only a full read may invent a title from the first prompt. An appended slice
            // without its own title keeps the stored one (null = keep, in UpsertSession) —
            // otherwise every rescan renames the session after its latest message.
            string? title = slice.Title
                ?? (titles.TryGetValue(sessionId, out var t) ? t : null)
                ?? (restart ? FallbackTitle(slice) : null);

            using var tx = db.Begin();

            if (restart && cursor != null) db.DeleteTurns(ad.Cli, sessionId, tx);

            int startSeq = restart ? 0 : db.NextTurnSeq(ad.Cli, sessionId, tx);
            db.AppendTurns(ad.Cli, sessionId, startSeq, slice.Turns, tx);

            var lastWrite = new DateTimeOffset(fi.LastWriteTimeUtc);
            db.UpsertSession(new SessionRecord(
                Cli: ad.Cli,
                Id: sessionId,
                Cwd: cwd,
                Title: title,
                StartedAt: slice.FirstAt ?? lastWrite,
                UpdatedAt: slice.LastAt ?? lastWrite,
                TurnCount: startSeq + slice.Turns.Count,
                NativePath: path,
                ResumeCommand: ad.ResumeCommand(sessionId),
                Source: SessionSource.Native), tx);

            db.SetCursor(path, new IngestCursor(mtime, newOffset, size), tx);
            tx.Commit();

            return slice.Turns.Count;
        }

        /// <summary>
        /// Reads whole lines only. A transcript can be mid-write, so the trailing partial line
        /// is left behind and the cursor stops at the last newline — never inside a record.
        /// </summary>
        private static (List<string> Lines, long NewOffset) ReadNewLines(string path, long offset)
        {
            var lines = new List<string>();

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            if (offset > fs.Length) offset = 0;
            fs.Seek(offset, SeekOrigin.Begin);

            long remaining = fs.Length - offset;
            if (remaining <= 0) return (lines, offset);

            var buf = new byte[remaining];
            int got = 0;
            while (got < buf.Length)
            {
                int n = fs.Read(buf, got, buf.Length - got);
                if (n <= 0) break;
                got += n;
            }

            int lastNl = Array.LastIndexOf(buf, (byte)'\n', got - 1);
            if (lastNl < 0) return (lines, offset);        // no complete line yet

            string text = Encoding.UTF8.GetString(buf, 0, lastNl + 1);
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length > 0) lines.Add(line);
            }

            return (lines, offset + lastNl + 1);
        }

        private static string FallbackTitle(TranscriptSlice slice)
        {
            foreach (var t in slice.Turns)
            {
                if (t.Role != "user") continue;
                string s = t.Text.Replace('\n', ' ').Replace('\r', ' ').Trim();
                if (s.Length == 0) continue;
                return s.Length <= 60 ? s : s[..60] + "…";
            }
            return "";
        }
    }
}
