# Claude Code GUI

A Windows desktop GUI for [Claude Code CLI](https://code.claude.com) — interact with Claude Code through a clean chat interface instead of the terminal.



## Features

- **Chat interface** — send messages to Claude Code and see responses in styled bubbles
- **Skills sidebar** — manage reusable `.md` skill files, insert `/commands` into input with one click
- **Save as Skill** — summarize any conversation into a reusable skill file with one click
- **Auto-permissions** — automatically updates `~/.claude/settings.json` to grant file access to the working directory
- **Image attachment** — attach PNG/JPG/WEBP images to your messages
- **Progress indicator** — animated progress bar with live output preview while Claude is working
- **Timeout warning** — yellow/red alert if no output for 30s/2min
- **Custom window chrome** — draggable title bar, minimize/close buttons
- **New Task** — open a fresh session window while keeping the current one

## Requirements

- Windows 10 or later (x64)
- [Claude Code CLI](https://code.claude.com) installed and on your PATH
  ```
  npm install -g @anthropic-ai/claude-code
  ```
  or use the native installer from [claude.com/download](https://claude.com/download)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (for building from source)

## Quick Start

### Option A — Download installer (recommended)

Download `ClaudeCodeUI_Setup.exe` from the [Releases](../../releases) page and run it.

> First launch: Windows SmartScreen may show a warning. Click **More info → Run anyway**.

### Option B — Build from source

```bash
git clone https://github.com/YOUR_USERNAME/ClaudeCodeUI.git
cd ClaudeCodeUI

dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -o publish

.\publish\ClaudeCodeUI.exe
```

## Usage

1. **Click Browse** — select your working directory. Session starts automatically.
2. **Type a message** and press **Enter** to send.
   - `Ctrl+Enter` inserts a new line.
3. **Skills sidebar** — click `+` to create a new skill, or click `▶` on any skill to insert its `/command` into the input.
4. **Save as Skill** — after a useful conversation, click `✨ Save as Skill` to let Claude summarize it into a reusable skill file.
5. **New Task** — opens a second window for a parallel session.

## Skills

Skills are `.md` files stored in `~/.claude/skills/` following the [Agent Skills Open Standard](https://agentskills.io).

Each skill file has a YAML frontmatter:

```markdown
---
name: convert-ogg-wav
description: >
  Convert all .ogg files in the current folder to .wav using ffmpeg.
  Use when the user asks to batch convert audio files.
---

## Steps
1. List all .ogg files with `Get-ChildItem *.ogg`
2. For each file, run `ffmpeg -i input.ogg output.wav`
3. Report how many files were converted
```

Invoke with `/convert-ogg-wav` in the input box.

## Project Structure

```
ClaudeCodeUI/
├── App.xaml              # WPF application entry point
├── App.xaml.cs
├── MainWindow.xaml       # UI layout (title bar, toolbar, chat, skills sidebar)
├── MainWindow.xaml.cs    # All logic: session management, skills, streaming
├── ClaudeCodeGUI.csproj  # .NET 8 project file
└── ClaudeCodeUI_Setup.iss  # Inno Setup installer script
```

## How It Works

Each message runs:
```
claude [--continue] --dangerously-skip-permissions -p "your message"
```

- `--continue` resumes the previous session context for multi-turn conversations
- `--dangerously-skip-permissions` skips file-write confirmation prompts
- Output is streamed in real time to the chat bubble

## Building the Installer

Install [Inno Setup 6+](https://jrsoftware.org/isdl.php), then:

```cmd
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
iscc ClaudeCodeUI_Setup.iss
```

Output: `installer_output\ClaudeCodeUI_Setup_v1.0.0.exe`

## License

MIT — see [LICENSE](LICENSE) for details.

## Acknowledgements

Built on top of [Anthropic Claude Code CLI](https://code.claude.com).  
UI inspired by macOS design language.
