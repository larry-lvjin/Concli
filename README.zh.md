<h1 align="center">ConCli</h1>

<p align="center"><a href="README.md">English</a> | 中文</p>

<p align="center">
  <strong>给 AI CLI 一个像样的 Windows 终端工作台</strong>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/platform-Windows%2010%2F11-0078D4?style=flat" alt="平台：Windows 10 / 11">
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat&logo=dotnet&logoColor=white" alt=".NET 10">
  <img src="https://img.shields.io/badge/terminal-ConPTY%20%2B%20xterm.js-2F6F44?style=flat" alt="ConPTY + xterm.js">
  <img src="https://img.shields.io/badge/UI-WPF--UI%20Fluent-5C2D91?style=flat" alt="WPF-UI Fluent">
  <img src="https://img.shields.io/badge/history-SQLite%20FTS5-003B57?style=flat&logo=sqlite&logoColor=white" alt="SQLite FTS5">
  <a href="src/LICENSE"><img src="https://img.shields.io/badge/license-MIT-green?style=flat" alt="MIT 许可证"></a>
</p>

---

> [!NOTE]
> 这是重构后的 ConCli：**内嵌终端 + 多 CLI 历史索引**。旧的「Claude Code 聊天界面」版本已被整体替换，原因和删除清单见[项目沿革](#项目沿革)。

## 简介

ConCli 是一个 Windows 桌面工作台：**选一个文件夹，在里面开一个真正的 PowerShell**，然后把 AI CLI（Claude Code、Codex……）都跑在这个终端里。

它不包装 `claude -p`，不解析 CLI 的输出，也不替 CLI 决定用哪个模型、打哪个端点 —— 那些是 shell 和 CLI 自己的事。它只做两件别人做不了的事：

1. **一个窗口装下所有终端**：ConPTY 起进程、xterm.js 渲染，标签页按 Chrome 的规则伸缩；焦点、快捷键、复制粘贴、中文输入、`vim` 这类全屏 TUI，表现都和 Windows Terminal 里的 PowerShell 一致。
2. **把对话历史变成可搜索的本地资产**：按字节游标增量解析 Claude Code / Codex 各自的 transcript 文件，统一进一个 SQLite + FTS5 索引；侧栏可以按时间或按项目浏览，双击即在原目录 resume 那个会话。每个终端还会在它自己的文件夹里留下一份原始记录。

**明确不做**：跨 CLI 续接上下文。没有任何 CLI 能读别家的 session 格式，这件事不成立。

## 界面

```text
标题栏      [侧栏开关] [⌄ 标签页搜索]   tab1 │ tab2 │ tab3   [＋]        ─ □ ✕
──────────  ──────────────────────────────────────────────────────────────────
历史侧栏     [搜索会话…]  ⟳  ⋯  🌙
（可收起）   最近 128    ☆
             今天
               ▸ 标题 · claude · 12 条 · 3 分钟前
             昨天
               ▸ …
             状态 · shell 与记录位置
──────────  ──────────────────────────────────────────────────────────────────
终端内容区   xterm.js 渲染的 PowerShell（一个标签页 = 一个独立 ConPTY 会话）
工作目录栏   [工作目录__________________________]  [📂]
```

- **标题栏那一行就是标签栏**：左边是历史侧栏开关和标签页搜索（Chrome 的 ⌄），右边是窗口按钮，中间是标签页和 ＋。
- **历史侧栏**可收起（宽度会被记住）：顶部是搜索、刷新、⋯ 菜单和主题开关；下面在「最近」和「文件夹」两个视图之间切换。
- **内容区**是一块不透明卡片：上面是终端，底部一条工作目录栏（回车即在该目录新建终端）。
- **侧栏底部常驻一行**显示当前用的哪个 shell、终端记录写到哪个目录 —— 找不到的记录等于没有记录，所以不藏进 tooltip。

## 功能特性

### 终端

- **真终端，不是模拟**：`ConPty.cs` 直接 P/Invoke `CreatePseudoConsole` / `ResizePseudoConsole` / `CreateProcessW`，Windows 10 1809+ 即可。
- **xterm.js 负责渲染**：VT 状态机、alt-screen、SGR、宽字符表、IME 全部交给 xterm.js，C# 侧一个字节都不解析。xterm.js 随程序发布（`src/Terminal/xterm/`），离线可用，页面 CSP 保持 `script-src 'self'`。
- **UTF-8 双向**：管道按 UTF-8 读写，解码器带跨读取的半个多字节序列缓冲，中文不会乱码；启动命令行里注入 `chcp 65001` 和 `[Console]::OutputEncoding = UTF8`，避开 Windows PowerShell 5.1 的 OEM 代码页。
- **尺寸实时同步**：`ResizeObserver` + FitAddon 算出行列数（50 ms 防抖），`ResizePseudoConsole` 跟上，缩放窗口不会错行。
- **输出合帧**：ConPTY 的输出按 16 ms 一帧合并成一条 WebView2 消息，spinner 那种一字节一字节的输出不会把 IPC 打满。
- **复制粘贴按 Windows Terminal 的规矩**：选中时 `Ctrl+C` 复制、没选中时是 `^C`；`Ctrl+V` / `Ctrl+Shift+C/V` 复制粘贴；右键有选中就复制、没有就粘贴。粘贴走 xterm 的 bracketed paste，PSReadLine 收到多行粘贴算一次编辑。
- **图片粘贴**：剪贴板里是图片时按 Claude Code 的图片粘贴键 `Alt+V`；从资源管理器复制文件会粘贴成带引号的路径。
- **中文输入法锚点**：TUI 会隐藏真实光标并自己画一个反显光标，导致 IME 候选框跑到状态栏最右边。`boot.js` 在光标隐藏时改为锚定最底部那个反显单元格，中文输入才不会飘。
- **OSC 标题进标签**：shell 把标题设成自己的 exe 路径这种噪音会被过滤，`claude` / `vim` / `git` 设的标题才会显示成 `标题 — 目录`。
- **像 PowerShell 本身**：Cascadia Mono 16px（12pt）、竖线光标、9001 行回滚，配色用 Windows Terminal 内置方案（深色 Campbell、浅色 One Half Light）。
- **退出不留残留**：关闭标签页先关 PTY 通知子进程退出，等 3 秒仍不退就 `TerminateProcess`，不会留下一堆 `powershell.exe`。

### 标签页

- **Chrome 的伸缩规则**：所有标签等宽，宽度在 220 px 和 40 px 之间随数量收缩；窄到 100 px 以下，非活动标签先收起关闭按钮，窄到 64 px 以下所有标签收起标题；全部到最小宽度后标签条才溢出，并自动滑动让活动标签留在视野里。
- **不用 `TabControl`**：WPF 在切换标签时会销毁并重建内容，WebView2 每次都要重新加载、终端会话会丢。这里是自绘标签条 + `Visibility` 切换。
- **标签页右键菜单**：在右侧新建、复制标签页（同目录开一个新 shell）、在资源管理器中打开目录、复制目录路径、另存为…、关闭其他、关闭右侧。
- **中键关闭**，和浏览器一致；`exit` 退出 shell 也会关掉标签页。
- **标签页搜索**：标签太多时用 `Ctrl+Shift+A` 打开搜索框，按标题或目录过滤，上下键选择、回车跳转、每行可直接关闭。

### 历史索引

- **一个 SQLite 库做唯一真相源**（`index.db`，WAL 模式）：`sessions`（主键 `cli + id`，含 `cwd` / `cwd_key`、标题、起止时间、轮数、原始文件路径、resume 命令、来源、置顶、收藏、自定义标题）、`turns`（主键 `cli + session_id + seq`）、`turns_fts`（FTS5）、`ingest_cursor`（增量游标）、`hidden`（墓碑）。
- **增量摄取**：先比 `(mtime, size)`，没变直接跳过；文件变小说明被重写，从 0 重解析；否则从记录的字节偏移续读。只读整行，绝不把游标停在半条记录里。
- **适配器即扩展点**：实现一个 `ITranscriptAdapter`（`Cli` / `EnumerateFiles` / `SessionIdFromPath` / `Parse` / `FallbackCwd` / `ResumeCommand` / `ExternalTitles`）并加入 `TranscriptAdapters.All` 即可接入新 CLI，摄取器、索引和侧栏都跟格式无关。
- **中文全文搜索**：FTS5 用 `tokenize='trigram'`，默认的 `unicode61` 不切分中文，中文全文搜索会完全失效。1–2 个字符的查询退回 `turns` 表的 `LIKE` 扫描（trigram 无法表示长度小于 3 的片段，而「重构」「终端」这类两字查询恰恰最常用）。
- **侧栏虚拟化**：`VirtualizingStackPanel` + 回收模式 + 分组虚拟化；搜索 180 ms 防抖（每次查询都是一次 SQLite 往返，CJK 输入法的候选上屏还会成批触发 `TextChanged`）；只允许最新一次查询的结果落地。
- **两个视图**：
  - **最近**：按 `已置顶 / 今天 / 昨天 / 过去 7 天 / 过去 30 天 / 月 / 年` 分组，混排所有 CLI，右侧显示相对时间；鼠标悬停时时间和操作按钮抢同一个格子（行宽不会跳）。
  - **文件夹**：先列项目（工作目录），进去后列该目录下的会话；双击或回车进入 / resume，`Backspace` 返回上一级，`F2` 重命名。
- **行操作**：收藏、置顶、重命名、在此目录打开 PowerShell、在资源管理器中打开、复制会话 ID、从列表移除、彻底删除。
  - **重命名**存在独立的 `custom_title` 列，摄取每次都会用新的 `ai-title` 覆盖 `title`，读取时优先用自定义标题，清空即恢复 CLI 自己的标题。
  - **从列表移除**写入墓碑表，摄取时在**解析之前**按文件名跳过，并且故意不写游标 —— 否则解除墓碑后那个文件看起来"已读完"，就恢复不回来了。⋯ 菜单里的「恢复已移除的记录」会清空墓碑再重扫。
  - **彻底删除**连 CLI 自己的 transcript 文件一起删，弹窗里写明文件路径，确认后才执行。

### 记录跟着文件夹走

- **原始记录**：在某个目录里开的每一个终端，都会把 ConPTY 的原始字节 tee 到 `<工作目录>\history\term_<yyyyMMdd-HHmmss-fff>\terminal.txt`。项目搬走，记录跟着搬走。
- **兜底**：目录建不出来（只读盘、网络路径、UNC）时回落到集中式的 `<根目录>\sessions\<时间戳>\terminal.txt` —— 宁可记录换个地方，也不能因此开不出终端。
- **另存为…**：把当前终端屏幕上已经 VT 解析过的文本（屏幕 + 回滚，不是原始字节）存成 `<工作目录>\history\<名字>.txt`，文件名里的非法字符会被拒绝，覆盖前确认。
- **⋯ 菜单**：打开索引目录、打开当前目录的 history（只是看看，不会凭空创建目录）。

### 外观

- **Fluent 设计语言**：建在 WPF-UI 4.3.0 上，`FluentWindow` + Mica 背景 + 圆角 + 内容延伸到标题栏。
- **没有一处写死颜色**：全部走 `DynamicResource` 引用 WinUI 主题画刷，这是深浅色一键切换的前提。强调色钉死在 WinUI 默认蓝 `#0078D4`（`App.Accent`，改品牌色就改这一行）。
- **深浅色切换**：侧栏顶部一个月亮/太阳按钮，WPF 换主题字典，终端通过 `ptyHost.setTheme(<ITheme JSON>)` 重新推一整套配色，连滚动条和网格外的区域一起刷。
- **无障碍**：纯图标按钮一律带 `AutomationProperties.Name`，读屏能读出「历史侧栏」「搜索标签页」「新建标签页」等。

## 支持的 CLI

内置两个适配器，其余 CLI 的 transcript 一旦有适配器就能进同一个索引。

| CLI | transcript 位置 | 会话 ID | resume 命令 | 标题来源 |
| --- | --- | --- | --- | --- |
| Claude Code | `~/.claude/projects/<转义后的 cwd>/<sessionId>.jsonl` | 文件名 | `claude --resume <id>` | transcript 里的 `ai-title` 记录 |
| Codex CLI | `~/.codex/sessions/YYYY/MM/DD/rollout-<时间戳>-<uuid>.jsonl` | 文件名末 36 个字符 | `codex resume <id>` | `~/.codex/session_index.jsonl` 里的 `thread_name` |
| 其他 CLI | 需要新增一个 `ITranscriptAdapter` 并加入 `TranscriptAdapters.All` | — | — | 兜底取首条用户消息前 60 个字符 |

两个适配器都只保留"人真的说过的话"：

- Claude：丢弃 `isSidechain` 的子代理 transcript（否则搜索会被淹没）、工具结果、thinking / tool_use 块，并剥掉 harness 注入的 `<system-reminder>` 块（不剥的话每个会话都能命中每个查询）。
- Codex：丢弃工具调用与输出、加密的 reasoning，以及 codex 自己注入的 `<environment_context>` / `<user_instructions>` / 图片包装和 AGENTS.md 前言。

## 安装与运行

### 环境要求

- **Windows 10 1809（build 17763）或更高版本，x64** —— ConPTY 的最低要求，也是安装脚本的 `MinVersion`。
- **WebView2 Runtime**：Windows 11 自带，Windows 10 通常随 Edge 装上。缺失时终端面板会启动失败并在状态栏提示。
- **PowerShell**：优先用 PowerShell 7（`pwsh.exe`），没有就用系统自带的 Windows PowerShell 5.1。不需要额外安装。
- **Claude Code / Codex 不是必需的**：不装就是普通 PowerShell，历史侧栏空着而已。要索引历史，才需要它们各自的 transcript 文件。
- 从源码构建还需要 **.NET 10 SDK**。

### 方式一：下载发布版 exe（推荐）

从 [Releases](../../releases) 页面下载 `ConcliV<版本号>.exe`（例如 `ConcliV1.0.0.exe`）。这是[发布 workflow](#发布流程) 产出的自包含单文件，无需安装、也无需 .NET 运行时，下载后直接运行。给同事用的公开下载页：<https://github.com/larry-plaud/Concli/releases/latest>。

> 首次启动时 Windows SmartScreen 可能报警，点 **更多信息 → 仍要运行**。

如果想要常规安装包（开始菜单入口、卸载程序），请用 Inno Setup 自行构建 `ConCli_Setup_v<版本号>.exe`，见[打包发布](#打包发布)。

### 方式二：从源码构建

```powershell
git clone https://github.com/larry-lvjin/Concli.git
cd Concli

dotnet run --project src/ConCli.csproj
```

发布成单个自包含 exe（与安装脚本的 `SourceDir` 对齐）：

```powershell
dotnet publish src/ConCli.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:PublishReadyToRun=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true `
  -o src/bin/Release/net10.0-windows/publish
```

## 快速上手

1. **填工作目录**：在底部目录栏填一个绝对路径（或点右边的 📂 选），按回车 —— 这个目录里立刻开出一个 PowerShell 标签页。
2. **用起来**：在终端里敲 `claude` 或 `codex`，和你平时在终端里做的一样。`Ctrl+T` 再开一个标签页，`Ctrl+W` 关掉。
3. **看历史**：侧栏点 ⟳ 扫描各 CLI 的 transcript。扫描完成后，双击（或回车）任意一条记录，会在它原来的目录里开一个新标签页并自动执行 resume 命令。
4. **整理**：悬停一条记录，右侧出现收藏 / 置顶 / 移除按钮；右键有完整菜单（重命名、复制会话 ID、在资源管理器中打开、彻底删除……）。
5. **留档**：需要把当前终端内容存下来时，在标签页上右键 →「另存为…」，文本会写进该目录的 `history\` 里。

## 键盘快捷键

| 快捷键 | 作用 |
| --- | --- |
| `Ctrl+T` | 在工作目录栏的目录新建终端 |
| `Ctrl+W` | 关闭当前标签页 |
| `Ctrl+Shift+A` | 打开标签页搜索 |
| `Enter`（在工作目录栏中） | 在该目录新建终端 |
| 标签页上按中键 | 关闭该标签页 |
| 历史行上双击 / `Enter` | 在原目录 resume 该会话 |
| `F2`（文件夹视图选中会话） | 重命名 |
| `Backspace`（文件夹视图） | 返回上一级 |
| `Ctrl+C` | 有选中内容时复制，否则发送 `^C` 给 shell |
| `Ctrl+V`、`Ctrl+Shift+C/V` | 复制 / 粘贴 |
| 右键 | 有选中内容时复制，否则粘贴 |
| `Alt+V` | 粘贴剪贴板里的图片（Claude Code 的图片粘贴键） |

终端独占键盘焦点：`Ctrl+C` / `Ctrl+R` / `Ctrl+L` 都归 shell，应用自己的快捷键全部避开这些组合。

## 项目结构

```text
ConCli/
├── README.md                       # English
├── README.zh.md                    # 本文件（简体中文）
├── .github/workflows/release.yml   # 打 tag 触发发布：单文件 exe 发到私有 + 公开仓库
└── src/
    ├── ConCli.slnx                 # 解决方案
    ├── ConCli.csproj               # net10.0-windows / WPF / WinExe
    ├── ConCli.ico                  # 应用图标
    ├── ConCli.iss                  # Inno Setup 安装脚本
    ├── App.xaml(.cs)               # 应用入口、主题与强调色（App.Accent）
    ├── MainWindow.xaml(.cs)        # 布局与编排：标题栏、标签页、侧栏、行操作
    ├── TabStrip.cs                 # ChromeTabPanel / TabStripLayout：标签伸缩与溢出滑动
    ├── RenameDialog.cs             # 重命名 / 另存为 对话框
    ├── AssemblyInfo.cs
    ├── History/                    # 历史索引层
    │   ├── Models.cs               # SessionRecord / TurnRecord / ProjectRecord / IngestCursor
    │   ├── HistoryPaths.cs         # 所有路径：索引库、记录目录、各 CLI transcript 根目录
    │   ├── HistoryDb.cs            # SQLite + FTS5：schema、写入、搜索、墓碑、迁移
    │   ├── HistoryIngest.cs        # 按字节游标增量扫描
    │   ├── TranscriptAdapter.cs    # ITranscriptAdapter 接口 + 适配器注册表
    │   ├── ClaudeAdapter.cs        # Claude Code JSONL
    │   └── CodexAdapter.cs         # Codex CLI JSONL + session_index
    ├── Terminal/                   # 终端层
    │   ├── ConPty.cs               # P/Invoke、管道、tee、环境块、Shell 解析
    │   ├── TerminalView.cs         # WebView2 宿主 + 消息桥 + 剪贴板
    │   ├── TerminalTheme.cs        # 终端配色唯一真相源（Campbell / One Half Light）
    │   ├── TerminalWindow.cs       # 早期「一窗口一终端」实现，已被标签页取代（留作参考）
    │   └── xterm/                  # 随程序发布的 xterm.js 资源
    ├── _attic/                     # 已出构建：Profile / 网关 / 凭据层与 ChoiceDialog
    ├── Properties/PublishProfiles/FolderProfile.pubxml
    └── LICENSE                     # MIT
```

## 工作原理

### 终端

```text
[WPF FluentWindow]
  └ WebView2 ─ xterm.js（VT 解析、渲染、滚动、选中、IME）
        ↕ JSON 消息桥（input / resize / copy / paste / title）
      TerminalView（C#，输出按 16 ms 合帧）
        ↕ UTF-8 管道 + 旁路 tee
      ConPty.cs（CreatePseudoConsole / ResizePseudoConsole / CreateProcessW）
        ↕
      powershell.exe
```

`CommandLineWith(initialCommand)` 把 UTF-8 初始化脚本和 resume 命令拼进 `-Command`，而不是在 `Start()` 之后往 stdin 写 —— 写早了会和 shell 自己的启动过程抢输入，时序无法可靠检测。`-NoExit` 保证 CLI 退出后你还留在交互 shell 里。

### 历史

```text
~/.claude/projects/**/*.jsonl ─┐
~/.codex/sessions/**/*.jsonl ──┤ 适配器按字节游标增量解析（只读，永不写回）
                               ▼
                <根目录>\index.db     ← 唯一真相源（SQLite + FTS5）
                               │
                               │ 侧栏 / 搜索 / resume 只读这里
                               ▼
                侧栏：最近 · 收藏 · 文件夹

<工作目录>\history\term_<时间戳>\terminal.txt   ← 该目录下每个终端的原始字节
```

## 设计与实现要点

- **`ui:TitleBar` 不能铺满标题栏那一行。** WPF-UI 4.3.0 的 `TitleBar` 对自己整个矩形无条件返回 `HTCAPTION`，只看自己的 `Header` / `CenterContent` / `TrailingContent`，既不看 `WindowChrome.IsHitTestVisibleInChrome`，也不知道有兄弟元素画在它上面。铺满的话整条标签栏变成拖拽区：标签画得出来、悬停有高亮、点击全部无效。现在它只占右上角三个窗口按钮，这一行其余部分是普通内容，最底层一个透明 `Border` 负责拖动和双击最大化。这个 bug 骗过了 UIA 自动化（`Invoke()` 绕过命中测试），只能靠 `WM_NCHITTEST` 的返回码验出来。
- **侧栏开关排在它所控制的侧栏之前。** `IsChecked="True"` 会在解析到那一行时就触发 `Checked`，此时 `SidebarPanel` 字段还是 null。处理器开头 `if (SidebarPanel is null) return;` 放弃解析期那一次调用。
- **`STARTF_USESTDHANDLES` 是 ConPTY 的关键。** 不置这个位，子进程会沿用父进程控制台的标准句柄：伪控制台挂上了（OSC 标题能传回来），但屏幕缓冲区永远是空的。置位并把三个标准句柄都传 `NULL`，子进程才会在自己的新控制台上打开 `CONIN$` / `CONOUT$`。
- **CSP 保持严格。** `script-src 'self'` 会拦掉内联 `<script>`，xterm.js 能加载但永远不初始化。所以引导脚本拆成独立的 `boot.js`，而不是放宽 CSP。
- **环境变量只在 `CreateProcess` 那一刻能注入**（`ConPtyOptions.Environment`），空覆盖必须返回 `IntPtr.Zero` 让子进程继承，而不是返回空块 —— 空块意味着子进程连 `PATH` 都没有。
- **`cwd` 和 `cwd_key` 是两列。** `cwd` 保留原始大小写供侧栏显示，`cwd_key`（小写、去尾部分隔符）用于分组和索引，所以 `D:\X` 和 `d:\x\` 算同一个文件夹，而显示的路径仍是真实大小写。
- **两个颜色的坑**：FTS5 必须 `tokenize='trigram'`（`unicode61` 不切分中文）；浅色主题的 ANSI `white` 故意映射成深灰 `#5D5D5D`，否则用白字的 CLI 在浅色底上整段消失。
- **`profile_id` 列还在**。Profile / 模型 / 网关 / 凭据那一整层已经从产品里删掉（代码在 `_attic/`），但老库里的 `profile_id` 保留，resume 时按它回退到 CLI 名。

更多实测数据、踩坑记录和被否掉的方案，写在各个源文件顶部的注释里 —— 关键约束都留在它所保护的那段代码旁边。

## 打包发布

需要 [Inno Setup 6+](https://jrsoftware.org/isdl.php)：

```powershell
# 1. 发布（输出目录与 ConCli.iss 的 SourceDir 一致）
dotnet publish src/ConCli.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:PublishReadyToRun=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true `
  -o src/bin/Release/net10.0-windows/publish

# 2. 打包
iscc src/ConCli.iss
```

产物：`src/installer_output/ConCli_Setup_v1.0.0.exe`（安装时先 `taskkill` 掉正在运行的 ConCli，需要管理员权限，语言含简体中文和 English）。

单文件发布把原生库（`e_sqlite3`、`WebView2Loader`、`wpfgfx`……）和 xterm 资源一起塞进 exe，首次运行解包到 `%TEMP%\.net\ConCli\`；xterm 是通过 `AppContext.BaseDirectory` 找到的，正好指向那个解包目录。WPF 无法被 IL 裁剪，所以体积只砍掉框架自带的语言资源目录（保留 `en` 和 `zh-Hans`）。

## 发布流程

发布由 [`.github/workflows/release.yml`](.github/workflows/release.yml) 自动完成，推送 tag 即触发：

```powershell
git tag v1.0.0
git push origin v1.0.0
```

遇到 `v*` / `V*` tag 时，workflow 在 `windows-latest` + .NET 10 上依次：

1. 定位唯一的 `.csproj`（`src/ConCli.csproj`），按其 `PlatformTarget` 决定 RuntimeIdentifier（未设置，因此是 `win-x64`），然后以自包含单文件方式 `dotnet publish`（`PublishSingleFile`、`IncludeNativeLibrariesForSelfExtract`、`EnableCompressionInSingleFile`、`DebugType=none`）。
2. 把主 exe 重命名为 `<仓库名>V<版本号>.exe`，即 `ConcliV1.0.0.exe`（tag 开头的 `v`/`V` 会去掉）。
3. 在本（私有）仓库 `larry-lvjin/Concli` 创建 Release：exe + GitHub 自动附带的源码包 + 自动生成的 release notes。
4. 把同一个 exe 发到公开仓库 `larry-plaud/Concli` 的 Release（只有 exe，没有源码）。如果仓库根目录有 `README.public.md`，会同步成公开仓库的 `README.md`；目前没有这个文件，该步骤只打一条 warning 并跳过 —— 内部 README 永远不会被公开。
5. 输出公开下载地址：`https://github.com/larry-plaud/Concli/releases/latest` 以及本版本的直链。

在 Actions 页面手动运行（`workflow_dispatch`）只会编译并把 exe 作为构建产物上传，不会创建任何 Release。

首次使用的一次性准备（详见 workflow 文件顶部注释）：用 `larry-plaud` 账号手工建公开仓库 `larry-plaud/Concli` 并勾选初始化 README（空仓库无法创建 Release）；为 `larry-plaud` 生成一个 Contents 读写权限的 fine-grained PAT；把它存为本仓库的 Actions secret `RELEASE_TOKEN`。

## 配置

| 配置项 | 位置 | 说明 |
| --- | --- | --- |
| 索引与记录根目录 | 环境变量 `CONCLI_HISTORY_ROOT`，默认 `D:\MyFile\CODE\history` | `index.db` 所在目录。**不要放进 git 仓库或 OneDrive / 坚果云这类同步目录** —— SQLite 加文件同步会损坏数据库。默认值是作者本机路径，请改成你自己的。 |
| 上次使用的工作目录 | `%LOCALAPPDATA%\ConCli\lastdir.txt` | 启动时恢复；路径不存在则回落到用户目录。 |
| WebView2 用户数据 | `%LOCALAPPDATA%\ConCli\WebView2` | xterm 页面的浏览器数据目录。 |
| 终端原始记录 | `<工作目录>\history\term_<yyyyMMdd-HHmmss-fff>\terminal.txt` | 建不出来时回落到 `<根目录>\sessions\<时间戳>\terminal.txt`。 |

## 路线图

按实施阶段划分，P0–P4、P7 已完成，还剩两项：

- **P5 项目内投影**：把索引单向投影到 `<工作目录>\history\`（`index.jsonl` 元数据、`<cli>\<id>.md` 纯文本、AI 可写的 `notes.md`），整个目录删掉能一键重建，且永不作为读取来源。
- **P6 `GenericJsonlAdapter` + `adapters.json`**：新增一个 CLI 只改配置、不改代码。

## 项目沿革

早期版本是一个「Claude Code 专用聊天界面」：底部输入框 + 聊天气泡 + Skills 侧栏 + 图片附件 + `cmd /c claude -p` 包装 + 正则解析进度步骤，还带一个自动改写 `~/.claude/settings.json` 的 `WritePermissions()`。

那个方法用字符串拼接手搓 JSON、**零转义**，会把 Windows 路径写成 `"D:\MyFile\..."`；`\M` 不是合法的 JSON 转义，整个文件解析失败，Claude Code 会静默忽略该文件的**全部**设置 —— 这就是当年那条 `Expected object, but received undefined` 的根因。

重构把这些全部删掉了（约 900 行），换成一个诚实的终端 + 一个本地历史索引。`WritePermissions()` 不在新架构里：权限交回给你自己在 `/permissions` 里管。删掉的类没有直接扔，而是移进了 `src/_attic/` 并从构建中排除，确认不要了再删目录。

## 常见问题

<details>
<summary><b>一定要装 Claude Code 或 Codex 吗？</b></summary>

不。程序本身只是终端，不装就是普通 PowerShell。历史侧栏会是空的，直到你有了对应的 transcript 文件。
</details>

<details>
<summary><b>中文搜索只有两个字的词能搜到吗？</b></summary>

能。正文里 ≥3 个字符走 FTS5 的 `MATCH`（真正的索引查找）；1–2 个字符退回 `turns` 表的 `LIKE` 扫描，结果与全表扫描一致；标题（`custom_title` / `title`）始终用 `LIKE` 匹配。trigram 分词器无法表示长度小于 3 的片段（`MATCH '重构'` 实测返回 0 行而真实命中 11 行），而两字查询恰恰最常用，这是目前唯一正确的做法。语料涨到几百 MB 后这条路径会变慢，届时需要另建 bigram 影子列。
</details>

<details>
<summary><b>索引库能放进项目目录吗？</b></summary>

不要。用 `CONCLI_HISTORY_ROOT` 把它指到一个**不在 git 里、也不在云同步目录里**的位置。项目目录里应该只有那个目录自己的终端记录（`history\`），它删掉不影响索引 —— 索引库才是真相源。
</details>

<details>
<summary><b>目录里为什么多出一个 <code>history\</code>？</b></summary>

那是该目录下每个终端的原始记录，以及「另存为…」写出的文本。删掉不会影响侧栏（侧栏只读 `index.db`），但会丢掉那些记录文件。
</details>

<details>
<summary><b>关闭标签页后会不会留下 powershell.exe？</b></summary>

不会。关闭时先 `ClosePseudoConsole` 通知子进程退出，等 3 秒仍不退才 `TerminateProcess`，然后 join 读写线程、释放句柄。
</details>

<details>
<summary><b>旧的 settings.json 已经被写坏了怎么办？</b></summary>

手工修好那个文件（路径里的反斜杠要转义，或干脆删掉那一段），然后升级到本版本。`WritePermissions()` 已经从代码里删除，不会再发生。
</details>

<details>
<summary><b>终端里输入中文，候选框位置不对？</b></summary>

这是 TUI 隐藏真实光标导致的。`boot.js` 会在光标隐藏时把 IME 锚定到最底部那个反显单元格（TUI 自己画的光标）上，已经是针对这个问题的处理；如果某个 TUI 表现仍然异常，欢迎提 issue 并附上程序名和版本。
</details>

<details>
<summary><b>能续接另一个 CLI 的上下文吗？</b></summary>

不能，这是明确不做的。没有任何 CLI 能读别家的 session 格式，硬做只会得到一份看起来像但内容错的上下文。
</details>

## 许可证

MIT，详见 [src/LICENSE](src/LICENSE)。Copyright (c) 2026 PLAUD。

## 致谢

- 终端渲染：[xterm.js](https://xtermjs.org/)（MIT）
- Fluent 界面：[WPF-UI](https://github.com/lepoco/wpfui)
- 配色方案取自 [Windows Terminal](https://github.com/microsoft/terminal) 内置的 Campbell 与 One Half Light
- Claude Code / Codex 的 transcript 格式归各自项目所有，本仓库只读它们写出的文件

---

<p align="center"><a href="README.md">English</a> | 中文</p>
