<h1 align="center">ConCli</h1>

<p align="center">
  <strong>A proper Windows terminal workbench for AI CLIs</strong>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/platform-Windows%2010%2F11-0078D4?style=flat" alt="Platform: Windows 10 / 11">
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat&logo=dotnet&logoColor=white" alt=".NET 10">
  <img src="https://img.shields.io/badge/terminal-ConPTY%20%2B%20xterm.js-2F6F44?style=flat" alt="ConPTY + xterm.js">
  <img src="https://img.shields.io/badge/UI-WPF--UI%20Fluent-5C2D91?style=flat" alt="WPF-UI Fluent">
  <img src="https://img.shields.io/badge/history-SQLite%20FTS5-003B57?style=flat&logo=sqlite&logoColor=white" alt="SQLite FTS5">
  <a href="src/LICENSE"><img src="https://img.shields.io/badge/license-MIT-green?style=flat" alt="MIT license"></a>
</p>

<p align="center"><a href="README.md">简体中文</a> · <strong>English</strong></p>

---

> [!NOTE]
> This README describes the rebuilt ConCli: **an embedded terminal plus a multi-CLI history index**. The old "Claude Code chat UI" was replaced wholesale — see [Project history](#project-history) for why, and for the deletion list.

## What it is

ConCli is a Windows desktop workbench: **pick a folder, open a real PowerShell in it**, and run your AI CLIs (Claude Code, Codex, …) inside that terminal.

It does not wrap `claude -p`, does not parse CLI output, and does not decide which model or endpoint a CLI should use — that belongs to the shell and the CLI. It does two things nothing else does:

1. **All your terminals in one window**: ConPTY starts the process, xterm.js renders it, and tabs shrink the way Chrome's do. Focus, keybindings, copy/paste, CJK input and full-screen TUIs like `vim` behave the same as PowerShell in Windows Terminal.
2. **Conversation history as a searchable local asset**: Claude Code's and Codex's own transcript files are parsed incrementally (byte-cursor based) into one SQLite + FTS5 index. Browse the sidebar by time or by project, double-click to resume that session in its original folder. Every terminal also leaves a raw record inside its own working directory.

**Explicit non-goal**: continuing context across CLIs. No CLI can read another's session format; the feature cannot exist.

## The window

```text
title bar    [sidebar] [⌄ tab search]   tab1 │ tab2 │ tab3   [＋]        ─ □ ✕
───────────  ─────────────────────────────────────────────────────────────────
history      [search sessions…]  ⟳  ⋯  🌙
sidebar       Recent 128    ☆
(collapsible)  Today
                 ▸ title · claude · 12 turns · 3 minutes ago
               Yesterday
                 ▸ …
               status · shell and where records go
───────────  ─────────────────────────────────────────────────────────────────
terminal     PowerShell rendered by xterm.js (one tab = one independent ConPTY session)
dir bar      [working directory________________]  [📂]
```

- **The title bar row *is* the tab strip**: sidebar toggle and tab search (Chrome's ⌄) on the left, caption buttons on the right, tabs and ＋ in between.
- **The history sidebar** collapses (its width is remembered): search, refresh, a ⋯ menu and the theme switch on top; a 最近 / 文件夹 (Recent / Folders) view switch below.
- **The content area** is an opaque card: the terminal on top, a working-directory bar at the bottom (Enter opens a new terminal there).
- **A permanent line at the bottom of the sidebar** shows which shell is in use and where terminal records are written — a record you cannot find is the same as no record, so it is never hidden in a tooltip.

## Features

### Terminal

- **A real terminal, not an emulator**: `ConPty.cs` P/Invokes `CreatePseudoConsole` / `ResizePseudoConsole` / `CreateProcessW` directly. Windows 10 1809+ is enough.
- **xterm.js does the rendering**: the VT state machine, alt-screen, SGR, the East Asian width tables and IME all belong to xterm.js; the C# side parses not a single byte. xterm.js ships with the app (`src/Terminal/xterm/`) so it works offline, and the page keeps a strict `script-src 'self'` CSP.
- **UTF-8 both ways**: pipes read and write UTF-8 with a decoder that carries a split multi-byte sequence across reads, so CJK never corrupts. `chcp 65001` and `[Console]::OutputEncoding = UTF8` are injected into the startup command line to dodge Windows PowerShell 5.1's OEM code page.
- **Size stays in sync**: `ResizeObserver` plus FitAddon compute rows/cols (50 ms debounce) and `ResizePseudoConsole` follows, so resizing never mis-wraps output.
- **Output is coalesced**: ConPTY output is batched into one WebView2 message per 16 ms frame, so a spinner emitting a few bytes at a time cannot flood the IPC channel.
- **Copy/paste the Windows Terminal way**: `Ctrl+C` copies when something is selected and is `^C` otherwise; `Ctrl+V` / `Ctrl+Shift+C/V` copy and paste; right-click copies a selection or pastes. Paste goes through xterm's bracketed paste, so PSReadLine sees a multi-line paste as one edit.
- **Image paste**: when the clipboard holds an image, `Alt+V` (Claude Code's image-paste key on Windows) is sent; copying files in Explorer pastes them as quoted paths.
- **An IME anchor for CJK**: TUIs hide the real cursor and draw their own reverse-video caret, which parks the IME candidate window at the far right of the status line. While the cursor is hidden, `boot.js` anchors on the bottom-most reverse-video cell instead.
- **OSC titles become tab titles**: shell noise such as its own exe path is filtered out; titles set by `claude`, `vim` or `git` show as `title — folder`.
- **Indistinguishable from PowerShell**: Cascadia Mono 16px (12pt), bar cursor, 9001 lines of scrollback, and Windows Terminal's own colour schemes (Campbell for dark, One Half Light for light).
- **No stray processes**: closing a tab closes the PTY to signal the child, waits 3 seconds, then calls `TerminateProcess` if needed.

### Tabs

- **Chrome's shrink rules**: every tab is the same width, clamped between 220 px and 40 px as tabs are added. Below 100 px, inactive tabs drop their close button; below 64 px every tab drops its title; only at the minimum width does the strip overflow, and then it slides to keep the active tab in view.
- **Deliberately not a `TabControl`**: WPF destroys and recreates the content of a tab when you switch, which would reload the WebView2 and lose the terminal session. This is a hand-drawn strip plus `Visibility` switching.
- **Tab context menu**: new tab to the right, duplicate (a fresh shell in the same folder), reveal the folder in Explorer, copy the folder path, Save as…, close others, close tabs to the right.
- **Middle-click closes**, like a browser; so does `exit` in the shell.
- **Tab search**: with too many tabs, `Ctrl+Shift+A` opens a search box that filters by title or folder — arrow keys to move, Enter to jump, and each row has its own close button.

### History index

- **One SQLite database as the single source of truth** (`index.db`, WAL mode): `sessions` (primary key `cli + id`, holding `cwd` / `cwd_key`, title, start/end times, turn count, native file path, resume command, source, pinned, favorite, custom title), `turns` (primary key `cli + session_id + seq`), `turns_fts` (FTS5), `ingest_cursor` (incremental cursors) and `hidden` (tombstones).
- **Incremental ingest**: compare `(mtime, size)` first and skip unchanged files; if the file shrank it was rewritten, so reparse from 0; otherwise resume from the recorded byte offset. Only whole lines are read, so the cursor never stops inside a record.
- **An adapter is the extension point**: implement `ITranscriptAdapter` (`Cli` / `EnumerateFiles` / `SessionIdFromPath` / `Parse` / `FallbackCwd` / `ResumeCommand` / `ExternalTitles`) and add it to `TranscriptAdapters.All`. The ingester, the index and the sidebar are format-agnostic.
- **Full-text search that works in Chinese**: FTS5 uses `tokenize='trigram'` — the default `unicode61` does not segment Chinese at all, which would make full-text search useless for most of the corpus. Queries of 1–2 characters fall back to a `LIKE` scan over `turns`, because a trigram tokenizer cannot represent fragments shorter than 3 characters and two-character queries ("重构", "终端") are the most common kind.
- **A virtualized sidebar**: `VirtualizingStackPanel` with recycling and virtualization while grouping; search is debounced by 180 ms (each query is a SQLite round trip, and a CJK IME commits `TextChanged` in bursts); only the newest query's results are allowed to land.
- **Two views**:
  - **Recent**: grouped into Pinned / Today / Yesterday / Past 7 days / Past 30 days / by month / by year, all CLIs mixed, with a relative timestamp on the right. On hover the timestamp and the row actions share the same slot, so the row never changes width.
  - **Folders**: projects (working directories) first, then the sessions inside one; double-click or Enter to enter / resume, `Backspace` to go up a level, `F2` to rename.
- **Row actions**: favorite, pin, rename, open PowerShell here, reveal in Explorer, copy session ID, remove from the list, delete permanently.
  - **Rename** is stored in its own `custom_title` column: ingest overwrites `title` with every new `ai-title` record, reads prefer the custom title, and clearing it restores the CLI's own title.
  - **Remove from the list** writes a tombstone. Ingest skips the file *before parsing* by file name, and deliberately writes no cursor — otherwise, once the tombstone were lifted, the file would look "already read" and could never be restored. "Restore removed records" in the ⋯ menu clears the tombstones and rescans.
  - **Delete permanently** also deletes the CLI's own transcript file, after showing the exact path in a confirmation dialog.

### Records follow the folder

- **Raw records**: every terminal opened in a folder tees ConPTY's raw bytes to `<cwd>\history\term_<yyyyMMdd-HHmmss-fff>\terminal.txt`. Move the project and its records move with it.
- **Fallback**: when that folder cannot be created (read-only drive, network path, UNC), the tee goes to `<root>\sessions\<stamp>\terminal.txt` instead — losing the record to a different location is better than failing to open the terminal.
- **Save as…**: writes the current screen plus scrollback, already VT-resolved (not raw bytes), to `<cwd>\history\<name>.txt`; invalid file-name characters are rejected and overwriting is confirmed.
- **⋯ menu**: open the index folder, or open the current folder's `history` (a look never creates the folder).

### Appearance

- **Fluent design language**: built on WPF-UI 4.3.0 — `FluentWindow`, Mica backdrop, rounded corners, content extended into the title bar.
- **Not one hardcoded colour**: everything goes through `DynamicResource` references to WinUI theme brushes, which is what makes one-click light/dark switching possible. The accent is pinned to WinUI's default blue `#0078D4` (`App.Accent` — the one line to change to rebrand).
- **Theme switch**: a moon/sun button in the sidebar header. WPF swaps its theme dictionary; the terminal gets a whole palette pushed through `ptyHost.setTheme(<ITheme JSON>)`, including the scrollbar and the area outside the grid.
- **Accessibility**: icon-only buttons always carry an `AutomationProperties.Name`, so a screen reader announces "history sidebar", "search tabs", "new tab" and so on.

## Supported CLIs

Two adapters ship in the box; any other CLI whose transcripts get an adapter joins the same index.

| CLI | Transcript location | Session ID | Resume command | Title source |
| --- | --- | --- | --- | --- |
| Claude Code | `~/.claude/projects/<escaped-cwd>/<sessionId>.jsonl` | the file name | `claude --resume <id>` | `ai-title` records inside the transcript |
| Codex CLI | `~/.codex/sessions/YYYY/MM/DD/rollout-<stamp>-<uuid>.jsonl` | last 36 characters of the file name | `codex resume <id>` | `thread_name` in `~/.codex/session_index.jsonl` |
| Any other CLI | needs a new `ITranscriptAdapter` added to `TranscriptAdapters.All` | — | — | fallback: first 60 characters of the first user message |

Both adapters keep only what a person actually typed:

- Claude: drops `isSidechain` subagent transcripts (they would swamp search results), tool results and thinking / tool_use blocks, and strips harness-injected `<system-reminder>` blocks (without that, every session matches every query).
- Codex: drops tool calls and their output, encrypted reasoning, and the `<environment_context>` / `<user_instructions>` / image wrappers and AGENTS.md preamble that codex injects itself.

## Install and run

### Requirements

- **Windows 10 1809 (build 17763) or later, x64** — the floor for ConPTY, and the installer's `MinVersion`.
- **WebView2 Runtime**: bundled with Windows 11, normally installed alongside Edge on Windows 10. Without it the terminal pane fails to start and says so in the status line.
- **PowerShell**: PowerShell 7 (`pwsh.exe`) is preferred, otherwise the built-in Windows PowerShell 5.1. Nothing extra to install.
- **Claude Code / Codex are not required**: without them this is an ordinary PowerShell, with an empty history sidebar. You need their transcripts only if you want them indexed.
- Building from source also needs the **.NET 10 SDK**.

### Option A — download the installer (recommended)

Download `ConCli_Setup.exe` from the [Releases](../../releases) page and run it.

> On first launch Windows SmartScreen may warn you. Click **More info → Run anyway**.

### Option B — build from source

```powershell
git clone https://github.com/larry-lvjin/ClaudeCodeWindows.git
cd ClaudeCodeWindows

dotnet run --project src/ConCli.csproj
```

To publish a single self-contained exe (matching the installer's `SourceDir`):

```powershell
dotnet publish src/ConCli.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:PublishReadyToRun=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true `
  -o src/bin/Release/net10.0-windows/publish
```

## Quick start

1. **Set the working directory**: type an absolute path in the bar at the bottom (or pick one with 📂) and press Enter — a PowerShell tab opens in that folder immediately.
2. **Use it**: type `claude` or `codex` in the terminal, exactly as you would in any shell. `Ctrl+T` opens another tab, `Ctrl+W` closes one.
3. **Browse history**: press ⟳ in the sidebar to scan the CLIs' transcripts. Then double-click (or press Enter on) any record to open a new tab in that session's original folder and run its resume command.
4. **Organize**: hover a record and the favorite / pin / remove buttons appear on the right; the context menu has the full set (rename, copy session ID, reveal in Explorer, delete permanently, …).
5. **Keep a copy**: right-click a tab → "Save as…" to write the terminal's text into that folder's `history\`.

## Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| `Ctrl+T` | New terminal in the working-directory bar's folder |
| `Ctrl+W` | Close the current tab |
| `Ctrl+Shift+A` | Open tab search |
| `Enter` (working-directory bar) | New terminal in that folder |
| Middle-click on a tab | Close that tab |
| Double-click / `Enter` on a history row | Resume that session in its original folder |
| `F2` (a session selected in the folder view) | Rename |
| `Backspace` (folder view) | Go up one level |
| `Ctrl+C` | Copy when something is selected, otherwise send `^C` to the shell |
| `Ctrl+V`, `Ctrl+Shift+C/V` | Copy / paste |
| Right-click | Copy when something is selected, otherwise paste |
| `Alt+V` | Paste an image from the clipboard (Claude Code's image-paste key) |

The terminal owns keyboard focus: `Ctrl+C` / `Ctrl+R` / `Ctrl+L` belong to the shell, and every application shortcut avoids those combinations.

## Project layout

```text
ConCli/
├── README.md                       # Simplified Chinese
├── README.en.md                    # this file
└── src/
    ├── ConCli.slnx                 # solution
    ├── ConCli.csproj               # net10.0-windows / WPF / WinExe
    ├── ConCli.ico                  # application icon
    ├── ConCli.iss                  # Inno Setup installer script
    ├── App.xaml(.cs)               # entry point, theme and accent (App.Accent)
    ├── MainWindow.xaml(.cs)        # layout and orchestration: title bar, tabs, sidebar, row actions
    ├── TabStrip.cs                 # ChromeTabPanel / TabStripLayout: tab shrinking and overflow sliding
    ├── RenameDialog.cs             # rename / save-as dialog
    ├── AssemblyInfo.cs
    ├── History/                    # history index layer
    │   ├── Models.cs               # SessionRecord / TurnRecord / ProjectRecord / IngestCursor
    │   ├── HistoryPaths.cs         # every path: index db, record folders, CLI transcript roots
    │   ├── HistoryDb.cs            # SQLite + FTS5: schema, writes, search, tombstones, migrations
    │   ├── HistoryIngest.cs        # byte-cursor incremental scan
    │   ├── TranscriptAdapter.cs    # ITranscriptAdapter + the adapter registry
    │   ├── ClaudeAdapter.cs        # Claude Code JSONL
    │   └── CodexAdapter.cs         # Codex CLI JSONL + session_index
    ├── Terminal/                   # terminal layer
    │   ├── ConPty.cs               # P/Invoke, pipes, tee, environment block, shell resolution
    │   ├── TerminalView.cs         # WebView2 host + message bridge + clipboard
    │   ├── TerminalTheme.cs        # single source of truth for terminal colour (Campbell / One Half Light)
    │   ├── TerminalWindow.cs       # the early "one terminal per window" implementation, superseded by tabs (kept for reference)
    │   └── xterm/                  # xterm.js assets shipped with the app
    ├── _attic/                     # out of the build: profile / gateway / credential layer and ChoiceDialog
    ├── Properties/PublishProfiles/FolderProfile.pubxml
    └── LICENSE                     # MIT
```

## How it works

### Terminal

```text
[WPF FluentWindow]
  └ WebView2 ─ xterm.js (VT parsing, rendering, scrolling, selection, IME)
        ↕ JSON message bridge (input / resize / copy / paste / title)
      TerminalView (C#, output coalesced per 16 ms frame)
        ↕ UTF-8 pipes + a tee on the side
      ConPty.cs (CreatePseudoConsole / ResizePseudoConsole / CreateProcessW)
        ↕
      powershell.exe
```

`CommandLineWith(initialCommand)` folds the UTF-8 setup script and the resume command into `-Command` rather than writing to stdin after `Start()` — a write that early races the shell's own initialisation, and that timing cannot be detected reliably. `-NoExit` keeps you in an interactive shell after the CLI exits.

### History

```text
~/.claude/projects/**/*.jsonl ─┐
~/.codex/sessions/**/*.jsonl ──┤ adapters parse incrementally by byte cursor (read-only, never written back)
                               ▼
                <root>\index.db      ← single source of truth (SQLite + FTS5)
                               │
                               │ the sidebar / search / resume read only here
                               ▼
                sidebar: Recent · Favorites · Folders

<cwd>\history\term_<stamp>\terminal.txt   ← raw bytes of every terminal opened in that folder
```

## Design and implementation notes

- **`ui:TitleBar` must not span the title-bar row.** WPF-UI 4.3.0's `TitleBar` answers `HTCAPTION` unconditionally for its whole rectangle; it only exempts its own `Header` / `CenterContent` / `TrailingContent`, does not consult `WindowChrome.IsHitTestVisibleInChrome`, and knows nothing about siblings drawn on top of it. Spanning the row turns the whole tab strip into a drag area: tabs render, highlight on hover, and ignore every click. It now covers only the three caption buttons; the rest of the row is ordinary content with a transparent `Border` underneath handling dragging and double-click maximize. UIA automation missed this bug (`Invoke()` bypasses hit-testing) — it only shows up in the return code of `WM_NCHITTEST`.
- **The sidebar toggle is declared before the sidebar it controls.** `IsChecked="True"` fires `Checked` while the XAML is still being parsed, when the `SidebarPanel` field is still null, so the handler starts with `if (SidebarPanel is null) return;` to drop that one call.
- **`STARTF_USESTDHANDLES` is what makes ConPTY work.** Without that flag the child inherits the parent console's standard handles: the pseudoconsole attaches (OSC titles come through) but its screen buffer stays empty forever. Setting it and passing `NULL` for all three handles forces the child to open `CONIN$` / `CONOUT$` on its own new console.
- **The CSP stays strict.** `script-src 'self'` blocks inline `<script>`, so xterm.js would load and never initialise. Hence the bootstrap lives in a separate `boot.js` instead of loosening the policy.
- **Environment variables can only be injected at `CreateProcess` time** (`ConPtyOptions.Environment`), and an empty override must return `IntPtr.Zero` so the child inherits, not an empty block — an empty block means the child has no `PATH` at all.
- **`cwd` and `cwd_key` are separate columns.** `cwd` keeps the original casing for display; `cwd_key` (lowercase, no trailing separator) is what grouping and indexing use, so `D:\X` and `d:\x\` are one folder while the sidebar still shows the real casing.
- **Two colour traps**: FTS5 must use `tokenize='trigram'` (`unicode61` does not segment Chinese), and the light theme's ANSI `white` is deliberately mapped to a dark grey `#5D5D5D`, otherwise a CLI printing white text disappears on a light background.
- **The `profile_id` column remains.** The whole profile / model / gateway / credential layer was removed from the product (the code sits in `_attic/`), but old databases keep `profile_id`, and resume falls back to the CLI name when it is absent.

More measured numbers, dead ends and rejected designs live in the comments at the top of each source file — every hard-won constraint sits next to the code it protects.

## Building the installer

Requires [Inno Setup 6+](https://jrsoftware.org/isdl.php):

```powershell
# 1. publish (the output folder matches SourceDir in ConCli.iss)
dotnet publish src/ConCli.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:PublishReadyToRun=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true `
  -o src/bin/Release/net10.0-windows/publish

# 2. package
iscc src/ConCli.iss
```

Output: `src/installer_output/ConCli_Setup_v1.0.0.exe` — it kills a running ConCli first, requires administrator rights, and ships Simplified Chinese and English wizard languages.

Single-file publish packs the native libraries (`e_sqlite3`, `WebView2Loader`, `wpfgfx`, …) and the xterm assets into the exe and unpacks them to `%TEMP%\.net\ConCli\` on first run; xterm is located through `AppContext.BaseDirectory`, which points at that extraction directory. WPF cannot be IL-trimmed, so the only safe size cut is dropping the framework's satellite resource directories (keeping `en` and `zh-Hans`).

## Configuration

| Setting | Location | Notes |
| --- | --- | --- |
| Index and record root | Environment variable `CONCLI_HISTORY_ROOT`, default `D:\MyFile\CODE\history` | Where `index.db` lives. **Never put it in a git repository or a synced folder such as OneDrive** — SQLite plus file sync corrupts databases. The default is the author's own path; change it. |
| Last working directory | `%LOCALAPPDATA%\ConCli\lastdir.txt` | Restored at startup; falls back to the user profile if the path no longer exists. |
| WebView2 user data | `%LOCALAPPDATA%\ConCli\WebView2` | Browser data directory for the xterm page. |
| Raw terminal records | `<cwd>\history\term_<yyyyMMdd-HHmmss-fff>\terminal.txt` | Falls back to `<root>\sessions\<stamp>\terminal.txt` when the folder cannot be created. |

## Roadmap

By implementation stage, P0–P4 and P7 are done. Two remain:

- **P5 — in-project projection**: project the index one-way into `<cwd>\history\` (`index.jsonl` metadata, `<cli>\<id>.md` plain text, an AI-writable `notes.md`), rebuildable from scratch after deleting the whole folder, and never a read source.
- **P6 — `GenericJsonlAdapter` + `adapters.json`**: add a CLI by editing configuration, not code.

## Project history

The early version was a "Claude Code chat UI": a bottom input box, chat bubbles, a skills sidebar, image attachments, a `cmd /c claude -p` wrapper, regex parsing of progress steps — plus a `WritePermissions()` that rewrote `~/.claude/settings.json` on every session start.

That method hand-built JSON by string concatenation with **zero escaping**, writing Windows paths as `"D:\MyFile\..."`. `\M` is not a legal JSON escape, the whole file failed to parse, and Claude Code silently ignored **every** setting in it — the root cause of the `Expected object, but received undefined` error at the time.

The rebuild deleted all of it (about 900 lines) in favour of an honest terminal and a local history index. `WritePermissions()` has no place in the new architecture: permissions belong to you, managed in `/permissions`. The deleted classes were not thrown away but moved to `src/_attic/` and excluded from the build, to be deleted once they are certainly not coming back.

## FAQ

<details>
<summary><b>Do I need Claude Code or Codex installed?</b></summary>

No. The app itself is just a terminal; without them it is ordinary PowerShell. The history sidebar stays empty until you have transcripts to index.
</details>

<details>
<summary><b>Can search find two-character Chinese words?</b></summary>

Yes. In message bodies, queries of 3 or more characters go through FTS5 `MATCH` (a real index lookup); 1–2 character queries fall back to a `LIKE` scan over `turns`, with results identical to a full table scan; titles (`custom_title` / `title`) are always matched with `LIKE`. A trigram tokenizer cannot represent fragments shorter than 3 characters (`MATCH '重构'` returns 0 rows in practice while the real hit count is 11), and two-character queries are the most common kind — this is currently the only correct approach. That path will slow down once the corpus reaches hundreds of megabytes, at which point a bigram shadow column is needed.
</details>

<details>
<summary><b>Can the index database live inside a project folder?</b></summary>

No. Point `CONCLI_HISTORY_ROOT` somewhere that is **neither in git nor in a synced folder**. A project folder should only contain that folder's own terminal records (`history\`), and deleting those does not affect the index — the database is the source of truth.
</details>

<details>
<summary><b>Why did a <code>history\</code> folder appear in my directory?</b></summary>

It holds the raw record of every terminal opened in that folder, plus any text written by "Save as…". Deleting it does not affect the sidebar (which reads only `index.db`), but those record files are gone.
</details>

<details>
<summary><b>Does closing a tab leave a stray powershell.exe behind?</b></summary>

No. Closing calls `ClosePseudoConsole` to signal the child, waits 3 seconds, then calls `TerminateProcess`; after that it joins the reader/writer threads and releases the handles.
</details>

<details>
<summary><b>My settings.json was already corrupted — what now?</b></summary>

Repair the file by hand (escape the backslashes in the path, or drop that section) and upgrade to this version. `WritePermissions()` has been removed from the code, so it cannot happen again.
</details>

<details>
<summary><b>Typing Chinese in a TUI puts the candidate window in the wrong place.</b></summary>

That is caused by the TUI hiding the real cursor. `boot.js` anchors the IME to the bottom-most reverse-video cell (the caret the TUI draws itself) whenever the cursor is hidden. If a particular TUI still misbehaves, please open an issue with the program name and version.
</details>

<details>
<summary><b>Can it continue context across different CLIs?</b></summary>

No, and that is deliberate. No CLI can read another's session format; forcing it would only produce a context that looks plausible and is wrong.
</details>

## License

MIT — see [src/LICENSE](src/LICENSE). Copyright (c) 2026 PLAUD.

## Acknowledgements

- Terminal rendering: [xterm.js](https://xtermjs.org/) (MIT)
- Fluent UI: [WPF-UI](https://github.com/lepoco/wpfui)
- Colour schemes taken from the Campbell and One Half Light schemes built into [Windows Terminal](https://github.com/microsoft/terminal)
- Claude Code and Codex transcript formats belong to their respective projects; this repository only reads the files they write

---

<p align="center"><a href="README.md">简体中文</a> · <strong>English</strong></p>
