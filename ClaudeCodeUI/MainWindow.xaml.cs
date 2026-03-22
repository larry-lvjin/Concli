using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Linq;
using System.Windows.Threading;

namespace ClaudeCodeGUI
{
    // Data model bound to each card in the skills list
    public class SkillItem
    {
        public string FilePath  { get; set; } = "";
        public string CmdDisplay { get; set; } = "";   // /skill-name
        public string DescSnippet { get; set; } = "";  // first sentence
        public string GroupTag  { get; set; } = "";    // folder name if nested
    }

    public partial class MainWindow : Window
    {
        // ── Process state ──────────────────────────────────────
        private Process?                 _job;
        private CancellationTokenSource? _cts;
        private bool                     _busy;
        private bool                     _ready;
        private bool                     _hasContinue;
        private string                   _workDir = "";
        private int                      _turns;
        private string?                  _claudeExe;
        private string                   _lastLine = "";
        private double                   _progressWidth = 0;
        private readonly Queue<string>   _activityLog = new(4);
        private string?                  _attachedImagePath;
        private DateTime                 _lastOutputTime = DateTime.Now;


        // ── Skills root ────────────────────────────────────────
        private string _skillsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "skills");
        private string?    _selectedSkillPath;
        private SkillItem? _selectedSkillItem;

        // ── Animation ──────────────────────────────────────────
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(90) };
        private readonly Stopwatch       _sw    = new();
        private int                      _fi;
        private static readonly string[] Frames = { "⠋","⠙","⠹","⠸","⠼","⠴","⠦","⠧","⠇","⠏" };

        // ── ANSI ───────────────────────────────────────────────
        private static readonly Regex Ansi = new(
            @"\x1B(?:[@-Z\\-_]|\[[0-9;]*[A-Za-z]|\][^\x07]*\x07)", RegexOptions.Compiled);

        // ── Brushes ────────────────────────────────────────────
        // Apple system color palette
        private static readonly SolidColorBrush
            BrDefault = H("#E5E5EA"), BrDim    = H("#98989D"),
            BrMuted   = H("#636366"), BrBlue   = H("#0A84FF"),
            BrGreen   = H("#30D158"), BrYellow = H("#FFD60A"),
            BrRed     = H("#FF453A"), BrOrange = H("#FF9F0A"),
            BrBdrH    = H("#0A3060"), BrBdrA   = H("#2C2C2E"),
            BrBgUser  = H("#0A1F38"), BrBgAst  = H("#1C1C1E"),
            BrUser    = H("#0A84FF"), BrAst    = H("#FF6B35"),
            BrBrowseGray  = H("#2C2C2E"),
            BrBrowseGreen = H("#1A3A1A");

        // ══════════════════════════════════════════════════════
        public MainWindow()
        {
            InitializeComponent();
            _timer.Tick += OnTick;
            Loaded += (_, _) =>
            {
                _workDir = WorkingDirBox.Text;
                InputBox.Focus();
                LoadSkillsTree();
            };
            Closed += (_, _) => KillJob();
            ShowWelcome();
        }

        // ── Animation tick ─────────────────────────────────────
        private void OnTick(object? s, EventArgs e)
        {
            _fi = (_fi + 1) % Frames.Length;
            SpinnerText.Text = Frames[_fi];
            if (_busy) SendButton.Content = Frames[_fi] + "  Running";

            var ts = _sw.Elapsed;
            ElapsedLabel.Text = ts.TotalSeconds < 60
                ? $"{(int)ts.TotalSeconds}s"
                : $"{(int)ts.TotalMinutes}m{ts.Seconds:D2}s";
            ElapsedLabel.Foreground =
                ts.TotalSeconds < 15 ? BrBlue :
                ts.TotalSeconds < 45 ? BrYellow : BrRed;
            if (!string.IsNullOrEmpty(_lastLine))
            {
                ThinkingLabel.Text = "Running";
                LiveOutputLabel.Text = _lastLine;
            }
            else
            {
                // Show elapsed since last output, warn if silent too long
                double silentSec = (DateTime.Now - _lastOutputTime).TotalSeconds;
                if (silentSec > 30)
                {
                    int silentMin = (int)(silentSec / 60);
                    string silentStr = silentMin > 0 ? $"{silentMin}m{(int)(silentSec % 60)}s" : $"{(int)silentSec}s";
                    LiveOutputLabel.Text = $"⚠  No output for {silentStr} — may be processing a large task or stuck";
                    LiveOutputLabel.Foreground = silentSec > 120
                        ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A))   // red >2min
                        : new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A));  // yellow >30s
                }
                else
                {
                    string[] dots = { "⏳ Working", "⏳ Working.", "⏳ Working..", "⏳ Working..." };
                    LiveOutputLabel.Text = dots[_fi % dots.Length];
                    LiveOutputLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x63, 0x63, 0x66));
                }
            }

            // Progress bar: fill based on elapsed time (0→full in ~90s, then pulses)
            double elapsed = _sw.Elapsed.TotalSeconds;
            double parentWidth = ProgressBarFill.ActualWidth > 0
                ? ProgressBarFill.ActualWidth
                : (ProgressBarFill.Parent as Border)?.ActualWidth ?? 300;
            double trackWidth = ((ProgressBarFill.Parent as Border)?.ActualWidth ?? 400);
            // Logarithmic fill: fast at start, slows down, never quite reaches 100%
            double pct = 1.0 - Math.Exp(-elapsed / 45.0);
            ProgressBarFill.Width = Math.Max(4, trackWidth * pct);
        }

        // ── Browse & Session ──────────────────────────────────
        private void BrowseDir_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select working directory — pick any file inside it",
                CheckFileExists = false,
                FileName = "Select Folder",
                Filter = "Folder|*.none",
                InitialDirectory = Directory.Exists(WorkingDirBox.Text)
                    ? WorkingDirBox.Text
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            if (dlg.ShowDialog() == true)
            {
                WorkingDirBox.Text = Path.GetDirectoryName(dlg.FileName) ?? dlg.FileName;
                DoStartSession();
            }
        }

        private void DoStartSession()
        {
            string dir = WorkingDirBox.Text.Trim();
            if (!Directory.Exists(dir))
            { SysLine($"Directory not found: {dir}", BrYellow); return; }

            _workDir = dir;
            _claudeExe = FindClaude();
            AddDivider();

            if (_claudeExe == null)
            {
                SysLine("Cannot find claude executable.", BrRed);
                foreach (var p in Candidates()) SysLine($"  {p}", BrMuted);
                SysLine("Run  claude install  or ensure claude is on PATH.", BrYellow);
                SetReady(false);
            }
            else
            {
                SysLine($"Session ready  ·  {_claudeExe}", BrGreen);
                SysLine($"Working dir    ·  {_workDir}", BrDim);
                // Auto-grant permissions to working dir and skills dir
                bool permOk = WritePermissions(_workDir, _skillsRoot);
                SysLine(permOk
                    ? "Permissions    ·  granted (settings.json updated)"
                    : "Permissions    ·  already set or skipped", BrDim);
                SetReady(true);
                string projSkills = Path.Combine(_workDir, ".claude", "skills");
                if (Directory.Exists(projSkills))
                    SysLine($"Project skills ·  {projSkills}", BrDim);
                LoadSkillsTree();
            }
        }

        private void StopSession_Click(object sender, RoutedEventArgs e)
        {
            KillJob(); SetBusy(false);
            SysLine("Stopped.", BrYellow);
        }

        private void ClearOutput_Click(object sender, RoutedEventArgs e)
        {
            ChatPanel.Children.Clear(); ShowWelcome();
            SaveSkillBtn.IsEnabled = false;
        }

        private void TitleBar_DragMove(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
                DragMove();
        }

        private void WinMinimize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void WinClose_Click(object sender, RoutedEventArgs e)
        {
            KillJob();
            Close();
        }

        private void NewTask_Click(object sender, RoutedEventArgs e)
        {
            // Launch a fresh instance of this app
            try
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                // For .NET 8+ single-file or normal exe
                string exePath = exe.EndsWith(".dll")
                    ? exe.Replace(".dll", ".exe")
                    : exe;
                if (File.Exists(exePath))
                    Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
                else
                {
                    // Fallback: re-launch via dotnet or the process itself
                    string? proc = Process.GetCurrentProcess().MainModule?.FileName;
                    if (proc != null)
                        Process.Start(new ProcessStartInfo(proc) { UseShellExecute = true });
                }
            }
            catch (Exception ex) { SysLine("Cannot open new window: " + ex.Message, BrRed); }
        }

        private async void SendMessage_Click(object sender, RoutedEventArgs e) => await RunAsync();
        private async void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    // Ctrl+Enter = insert newline
                    e.Handled = true;
                    int pos = InputBox.CaretIndex;
                    InputBox.Text = InputBox.Text.Insert(pos, "\n");
                    InputBox.CaretIndex = pos + 1;
                }
                else if (Keyboard.Modifiers == ModifierKeys.None)
                {
                    // Enter = send
                    e.Handled = true;
                    await RunAsync();
                }
            }
        }

        private void OutputScroller_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Gather all text from chat panel
                var sb = new System.Text.StringBuilder();
                foreach (UIElement el in ChatPanel.Children)
                {
                    if (el is Border b && b.Child is StackPanel sp)
                    {
                        foreach (UIElement child in sp.Children)
                        {
                            if (child is TextBlock tb)
                                sb.AppendLine(tb.Text);
                        }
                        sb.AppendLine();
                    }
                    else if (el is TextBlock stb)
                    {
                        sb.AppendLine(stb.Text);
                    }
                }
                string text = sb.ToString().Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    Clipboard.SetText(text);
                    StatusBar.Text = "Chat copied to clipboard";
                }
                e.Handled = true;
            }
        }

        private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (InputPlaceholder != null)
                InputPlaceholder.Visibility = string.IsNullOrEmpty(InputBox.Text)
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Core run ──────────────────────────────────────────
        private async Task RunAsync()
        {
            if (_busy || !_ready || _claudeExe == null) return;
            string msg = InputBox.Text.Trim();
            if (string.IsNullOrEmpty(msg)) return;

            InputBox.Clear();
            AddUserBubble(msg);
            // If image attached, show it in the chat bubble
            if (!string.IsNullOrEmpty(_attachedImagePath) && File.Exists(_attachedImagePath))
            {
                AddImageBubble(_attachedImagePath);
                // Clear after sending
                string sentPath = _attachedImagePath;
                _attachedImagePath = null;
                Dispatcher.InvokeAsync(() => {
                    ImagePreviewBar.Visibility = Visibility.Collapsed;
                    ImagePreviewThumb.Source   = null;
                    AttachImageBtn.Background  = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
                });
            }
            SetBusy(true);

            _cts = new CancellationTokenSource();
            var tok = _cts.Token;
            var (_, tb) = AddAssistantBubble();

            try
            {
                // Include image path in the message text (claude reads files natively)
                string fullMsg = msg;
                if (!string.IsNullOrEmpty(_attachedImagePath) && File.Exists(_attachedImagePath))
                    fullMsg = "[Image: " + _attachedImagePath + "]\n" + msg;

                string safe = fullMsg.Replace("\r", "").Replace("\n", " ").Replace("\"", "'");
                string cont = _hasContinue ? "--continue " : "";
                string args = $"/c \"\"{_claudeExe}\" {cont}--dangerously-skip-permissions -p \"{safe}\"\"";

                _job = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe", Arguments = args,
                        WorkingDirectory = _workDir,
                        UseShellExecute = false,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding  = Encoding.UTF8,
                    }
                };
                _job.StartInfo.Environment["NO_COLOR"] = "1";
                _job.StartInfo.Environment["TERM"] = "dumb";
                _job.StartInfo.Environment["FORCE_COLOR"] = "0";
                _job.Start();

                await Task.WhenAll(
                    StreamTo(_job.StandardOutput, tb, false, tok),
                    StreamTo(_job.StandardError,  tb, true,  tok));
                await Task.Run(() => _job.WaitForExit(), tok);

                int exitCode = _job?.ExitCode ?? 0;
                if (exitCode != 0 && string.IsNullOrWhiteSpace(tb.Text))
                {
                    tb.Text = $"Process exited with code {exitCode}.\n";
                    tb.Text += "Check: claude --version in a terminal to verify it works.";
                    tb.Foreground = BrYellow;
                }
                _hasContinue = true; _turns++;
                TurnCounter.Text = $"{_turns} turn{(_turns == 1 ? "" : "s")}";
                SaveSkillBtn.IsEnabled = true;  // conversation exists, can now save as skill
            }
            catch (OperationCanceledException) { tb.Text += " [stopped]"; }
            catch (Exception ex) { tb.Text += $"\nError: {ex.Message}"; tb.Foreground = BrRed; }
            finally
            {
                _job?.Dispose(); _job = null;
                SetBusy(false);
                OutputScroller.ScrollToBottom();
                InputBox.Focus();
            }
        }

        private async Task StreamTo(StreamReader r, TextBlock tb, bool isErr, CancellationToken tok)
        {
            var buf = new char[128];
            try
            {
                while (!tok.IsCancellationRequested)
                {
                    int n = await r.ReadAsync(buf, 0, buf.Length);
                    if (n == 0) break;
                    string clean = Strip(new string(buf, 0, n));
                    if (string.IsNullOrEmpty(clean)) continue;
                    foreach (var ln in clean.Split('\n'))
                    {
                        string t = ln.TrimEnd('\r').Trim();
                        if (t.Length > 0)
                        {
                            _lastLine = t.Length > 90 ? t[..90] + "…" : t;
                            _lastOutputTime = DateTime.Now;
                            if (_activityLog.Count >= 3) _activityLog.Dequeue();
                            _activityLog.Enqueue(_lastLine);
                        }
                    }
                    await Dispatcher.InvokeAsync(() =>
                    {
                        tb.Text += clean;
                        OutputScroller.ScrollToBottom();
                    });
                }
            }
            catch (OperationCanceledException) { }
        }

        private void KillJob()
        {
            _cts?.Cancel();
            try { _job?.Kill(entireProcessTree: true); } catch { }
        }


        // ══════════════════════════════════════════════════════
        //  SKILLS SIDEBAR  — flat .md file model
        //  ~/.claude/skills/[group/]skill-name.md
        //  Folders are optional groups only.
        // ══════════════════════════════════════════════════════

        private string _skillSearchText = "";

        private static (string name, string description) ParseFrontmatter(string mdPath)
        {
            try
            {
                var lines = File.ReadAllLines(mdPath);
                if (lines.Length < 2 || lines[0].Trim() != "---") return ("", "");
                string name = "", desc = ""; bool inDesc = false;
                var dp = new List<string>();
                for (int i = 1; i < lines.Length; i++)
                {
                    if (lines[i].Trim() == "---") break;
                    if (lines[i].StartsWith("name:"))
                        name = lines[i]["name:".Length..].Trim().Trim('"');
                    else if (lines[i].StartsWith("description:"))
                    {
                        string d = lines[i]["description:".Length..].Trim().Trim('"');
                        if (d == ">") inDesc = true; else desc = d;
                    }
                    else if (inDesc && (lines[i].StartsWith("  ") || lines[i].StartsWith("\t")))
                        dp.Add(lines[i].Trim());
                    else if (inDesc) inDesc = false;
                }
                if (dp.Count > 0) desc = string.Join(" ", dp);
                return (name, desc);
            }
            catch { return ("", ""); }
        }

        private static string SkillCmd(string mdPath)
        {
            var (n, _) = ParseFrontmatter(mdPath);
            return string.IsNullOrEmpty(n) ? Path.GetFileNameWithoutExtension(mdPath) : n;
        }

        private void LoadSkillsTree()
        {
            var items = new List<SkillItem>();
            int total = 0;

            CollectSkills(_skillsRoot, "", items, ref total);

            // Project-level skills
            if (!string.IsNullOrEmpty(_workDir))
            {
                string proj = Path.Combine(_workDir, ".claude", "skills");
                if (Directory.Exists(proj) && proj != _skillsRoot)
                    CollectSkills(proj, "project/", items, ref total);
            }

            // Apply search filter
            string q = _skillSearchText.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(q))
                items = items.Where(i =>
                    i.CmdDisplay.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    i.DescSnippet.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    i.GroupTag.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

            SkillsList.ItemsSource = items;
            SkillsCountLabel.Text = total > 0 ? $"  {total}" : "";
        }

        private static void CollectSkills(string dir, string groupPrefix,
            List<SkillItem> items, ref int total)
        {
            if (!Directory.Exists(dir))
                try { Directory.CreateDirectory(dir); } catch { return; }

            // Recurse into sub-folders first
            foreach (var sub in Directory.GetDirectories(dir).OrderBy(d => d))
            {
                string groupName = groupPrefix + Path.GetFileName(sub);
                CollectSkills(sub, groupName + "/", items, ref total);
            }

            // .md files in this dir
            foreach (var f in Directory.GetFiles(dir, "*.md").OrderBy(x => x))
            {
                var (_, desc) = ParseFrontmatter(f);
                string cmd = SkillCmd(f);
                string snip = desc.Length > 55 ? desc[..55] + "…" : desc;
                int dot = snip.IndexOf(". ", StringComparison.Ordinal);
                if (dot > 6 && dot < 50) snip = snip[..(dot + 1)];

                items.Add(new SkillItem
                {
                    FilePath   = f,
                    CmdDisplay = "/" + cmd,
                    DescSnippet = snip,
                    GroupTag    = groupPrefix.TrimEnd('/')
                });
                total++;
            }
        }

        // ── Search ────────────────────────────────────────────
        private void SkillSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _skillSearchText = SkillSearchBox.Text;
            LoadSkillsTree();
        }

        // ── Card events ───────────────────────────────────────
        private void SkillCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is SkillItem item)
                SelectSkillItem(item);
        }

        private void SkillCardInsert_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is SkillItem item)
            {
                SelectSkillItem(item);
                InsertCommand(item.FilePath);
                e.Handled = true;
            }
        }

        private void SelectSkillItem(SkillItem item)
        {
            _selectedSkillItem = item;
            _selectedSkillPath = item.FilePath;
            SkillPreviewCmd.Text = item.CmdDisplay;
            SkillPreviewCmd.Foreground = H("#0A84FF");
            SkillPreviewDesc.Text = item.DescSnippet.Length > 0 ? item.DescSnippet : "(no description)";
            SkillPreviewDesc.Foreground = H("#636366");
            SkillInsertBtn.IsEnabled = true;
        }

        private void ClearPreview()
        {
            _selectedSkillItem = null;
            SkillPreviewCmd.Text = "No skill selected";
            SkillPreviewCmd.Foreground = H("#48484A");
            SkillPreviewDesc.Text = "";
            SkillInsertBtn.IsEnabled = false;
        }

        // ── Keep TreeView stubs so old event names still compile ──
        private void SkillsTree_SelectedItemChanged(object s, RoutedPropertyChangedEventArgs<object> e) { }
        private void SkillsTree_DoubleClick(object s, System.Windows.Input.MouseButtonEventArgs e) { }

        // ── Actions ───────────────────────────────────────────
        private void SkillUse_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedSkillPath != null && File.Exists(_selectedSkillPath))
                InsertCommand(_selectedSkillPath);
        }

        private void InsertCommand(string mdPath)
        {
            string insert = "/" + SkillCmd(mdPath);
            InputBox.Text = string.IsNullOrEmpty(InputBox.Text)
                ? insert + " "
                : InputBox.Text.TrimEnd() + " " + insert + " ";
            InputBox.CaretIndex = InputBox.Text.Length;
            InputBox.Focus();
            StatusBar.Text = "Inserted " + insert + "  —  add arguments after it";
        }

        private void SkillOpenEditor_Click(object sender, RoutedEventArgs e)
        {
            string? target = _selectedSkillPath;
            if (target == null || !File.Exists(target)) return;
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { SysLine("Cannot open: " + ex.Message, BrRed); }
        }

        private void SkillNewSkill_Click(object sender, RoutedEventArgs e)
        {
            string parentDir = GetSelectedDirectory();
            string mdPath = GetUniquePath(parentDir, "new-skill", ".md");
            string skillName = Path.GetFileNameWithoutExtension(mdPath);
            try
            {
                string template =
                    "---\n" +
                    "name: " + skillName + "\n" +
                    "description: >\n" +
                    "  Describe what this skill does and when to use it.\n" +
                    "---\n\n" +
                    "# " + skillName + "\n\n" +
                    "## What to do\n" +
                    "Describe the steps Claude should follow.\n\n" +
                    "## Example\n" +
                    "User: [example request]\n" +
                    "Action: [what Claude does]\n";
                File.WriteAllText(mdPath, template);
                LoadSkillsTree();
                StatusBar.Text = "Created: " + skillName + ".md  —  edit it now";
                Process.Start(new ProcessStartInfo(mdPath) { UseShellExecute = true });
            }
            catch (Exception ex) { SysLine("Cannot create skill: " + ex.Message, BrRed); }
        }

        private void SkillNewFolder_Click(object sender, RoutedEventArgs e)
        {
            string parentDir = GetSelectedDirectory();
            string newDir = GetUniquePath(parentDir, "group", "");
            try
            {
                Directory.CreateDirectory(newDir);
                LoadSkillsTree();
                StatusBar.Text = "Created group: " + Path.GetFileName(newDir);
            }
            catch (Exception ex) { SysLine("Cannot create folder: " + ex.Message, BrRed); }
        }

        private void SkillRename_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedSkillPath == null || !File.Exists(_selectedSkillPath)) return;

            var (curName, curDesc) = ParseFrontmatter(_selectedSkillPath);
            if (string.IsNullOrEmpty(curName))
                curName = Path.GetFileNameWithoutExtension(_selectedSkillPath);

            // ── Build a small popup window ──
            var win = new Window
            {
                Title          = "Edit Skill",
                Width          = 440,
                Height         = 240,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner          = this,
                ResizeMode     = ResizeMode.NoResize,
                Background     = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)),
                FontFamily     = new FontFamily("Segoe UI"),
            };

            var root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Name label + box
            root.Children.Add(Label(0, "Skill name  (used as /command)"));
            var nameBox = InputBox2(curName);
            Grid.SetRow(nameBox, 1);
            root.Children.Add(nameBox);

            // Description label + box
            root.Children.Add(Label(2, "Description  (when to use this skill)"));
            var descBox = InputBox2(curDesc);
            descBox.Height = 60;
            descBox.AcceptsReturn = true;
            descBox.TextWrapping = TextWrapping.Wrap;
            Grid.SetRow(descBox, 3);
            root.Children.Add(descBox);

            // Buttons
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            Grid.SetRow(btnRow, 5);

            var cancelBtn = MakeBtn("Cancel", "#2C2C2E", "#98989D");
            cancelBtn.Click += (_, _) => win.DialogResult = false;
            var saveBtn   = MakeBtn("Save", "#0A2540", "#0A84FF");
            saveBtn.FontWeight = FontWeights.Bold;
            saveBtn.Click += (_, _) => win.DialogResult = true;

            btnRow.Children.Add(cancelBtn);
            btnRow.Children.Add(new Border { Width = 8 });
            btnRow.Children.Add(saveBtn);
            root.Children.Add(btnRow);

            win.Content = root;
            win.Loaded += (_, _) => nameBox.Focus();

            if (win.ShowDialog() != true) return;

            // ── Apply changes ──
            string newName = nameBox.Text.Trim()
                .Replace(" ", "-").Replace("/", "-").Replace("\\", "-");
            string newDesc = descBox.Text.Trim();

            if (string.IsNullOrEmpty(newName)) return;

            try
            {
                // Rewrite frontmatter in the file
                string[] fileLines = File.ReadAllLines(_selectedSkillPath);
                var output = new List<string>();
                bool inFm = false; bool nameWritten = false; bool descWritten = false;

                for (int i = 0; i < fileLines.Length; i++)
                {
                    if (i == 0 && fileLines[i].Trim() == "---") { inFm = true; output.Add(fileLines[i]); continue; }
                    if (inFm && fileLines[i].Trim() == "---")
                    {
                        // Inject any missing fields before closing ---
                        if (!nameWritten) output.Add("name: " + newName);
                        if (!descWritten && !string.IsNullOrEmpty(newDesc))
                        { output.Add("description: >"); output.Add("  " + newDesc); }
                        inFm = false; output.Add(fileLines[i]); continue;
                    }
                    if (inFm && fileLines[i].StartsWith("name:"))
                    { output.Add("name: " + newName); nameWritten = true; continue; }
                    if (inFm && fileLines[i].StartsWith("description:"))
                    {
                        // Skip old description lines (may be multi-line)
                        descWritten = true;
                        if (!string.IsNullOrEmpty(newDesc))
                        { output.Add("description: >"); output.Add("  " + newDesc); }
                        // Skip continuation indent lines
                        while (i + 1 < fileLines.Length &&
                               (fileLines[i + 1].StartsWith("  ") || fileLines[i + 1].StartsWith("\t")))
                            i++;
                        continue;
                    }
                    output.Add(fileLines[i]);
                }

                // If no frontmatter existed, prepend it
                if (!inFm && output.Count > 0 && output[0].Trim() != "---")
                {
                    var header = new List<string> { "---", "name: " + newName };
                    if (!string.IsNullOrEmpty(newDesc)) { header.Add("description: >"); header.Add("  " + newDesc); }
                    header.Add("---");
                    output.InsertRange(0, header);
                }

                File.WriteAllLines(_selectedSkillPath, output);

                // Rename file if name changed
                string dir = Path.GetDirectoryName(_selectedSkillPath)!;
                string newFile = Path.Combine(dir, newName + ".md");
                if (!string.Equals(_selectedSkillPath, newFile, StringComparison.OrdinalIgnoreCase)
                    && !File.Exists(newFile))
                {
                    File.Move(_selectedSkillPath, newFile);
                    _selectedSkillPath = newFile;
                }

                LoadSkillsTree();
                StatusBar.Text = "Saved: /" + newName;
            }
            catch (Exception ex) { SysLine("Rename failed: " + ex.Message, BrRed); }
        }

        // ── Popup helpers ─────────────────────────────────────
        private static UIElement Label(int row, string text)
        {
            var tb = new TextBlock
            {
                Text = text, Foreground = new SolidColorBrush(Color.FromRgb(0x98, 0x98, 0x9D)),
                FontSize = 11, Margin = new Thickness(0, row == 0 ? 0 : 10, 0, 4)
            };
            Grid.SetRow(tb, row);
            return tb;
        }

        private static TextBox InputBox2(string text)
        {
            return new TextBox
            {
                Text = text,
                Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xEB, 0xEB, 0xF0)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)),
                BorderThickness = new Thickness(1),
                CaretBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)),
                Padding = new Thickness(8, 6, 8, 6),
                FontSize = 13,
                FontFamily = new FontFamily("Consolas, Courier New"),
                Height = 34,
            };
        }

        private static Button MakeBtn(string label, string bg, string fg)
        {
            return new Button
            {
                Content = label,
                Padding = new Thickness(16, 7, 16, 7),
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI"),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg)),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg)),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
        }

        private void SkillDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedSkillPath == null) return;
            string name = Path.GetFileName(_selectedSkillPath);
            bool isDir = Directory.Exists(_selectedSkillPath);
            string msg = isDir
                ? "Delete folder [" + name + "] and all its contents?"
                : "Delete [" + name + "]?";
            if (MessageBox.Show(msg, "Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                if (isDir)
                {
                    foreach (var f in Directory.GetFiles(_selectedSkillPath, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(_selectedSkillPath, recursive: true);
                }
                else if (File.Exists(_selectedSkillPath))
                {
                    File.SetAttributes(_selectedSkillPath, FileAttributes.Normal);
                    File.Delete(_selectedSkillPath);
                }
                _selectedSkillPath = null;
                ClearPreview();
                LoadSkillsTree();
                StatusBar.Text = "Deleted: " + name;
            }
            catch (Exception ex) { SysLine("Cannot delete: " + ex.Message, BrRed); }
        }

        private async void SkillGenerate_Click(object sender, RoutedEventArgs e)
        {
            if (_claudeExe == null) { SysLine("Start a session first.", BrYellow); return; }

            AddDivider();
            var b = Bubble(BrBgAst, BrBdrA);
            var sp = (StackPanel)b.Child;
            sp.Children.Add(new TextBlock
            {
                Text = "✨  Generate a Skill",
                Foreground = H("#0A84FF"), FontSize = 13, FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 0, 0, 8)
            });
            foreach (var (t, c) in new (string, SolidColorBrush)[]
            {
                ("Edit the text in the input box, describe what you want,", BrDim),
                ("then press Enter. Claude will create a .md skill file.", BrDim),
                ("", BrMuted),
                ("Example:", BrDim),
                ("  Convert every .ogg in this folder to .wav using ffmpeg", H("#0A84FF")),
            })
                sp.Children.Add(new TextBlock
                {
                    Text = t, Foreground = c, FontSize = 12,
                    FontFamily = new FontFamily("Consolas, Courier New"),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1)
                });
            ChatPanel.Children.Add(b);
            OutputScroller.ScrollToBottom();

            InputBox.Text =
                "Create a skill .md file for me. The skill should do: [DESCRIBE HERE]. " +
                "Save it as a single .md file in: " + _skillsRoot + ". " +
                "Start with ---  name: skill-name  description: > ...  --- then markdown instructions.";
            InputBox.SelectAll();
            InputBox.Focus();
            await Task.CompletedTask;
        }

        private void SkillRefresh_Click(object sender, RoutedEventArgs e) => LoadSkillsTree();

        private string GetSelectedDirectory()
        {
            if (_selectedSkillPath != null)
            {
                if (Directory.Exists(_selectedSkillPath)) return _selectedSkillPath;
                if (File.Exists(_selectedSkillPath))
                    return Path.GetDirectoryName(_selectedSkillPath) ?? _skillsRoot;
            }
            return _skillsRoot;
        }

        private static string GetUniquePath(string dir, string baseName, string ext)
        {
            string candidate = Path.Combine(dir, baseName + ext);
            int i = 1;
            while (File.Exists(candidate) || Directory.Exists(candidate))
                candidate = Path.Combine(dir, baseName + "-" + i++ + ext);
            return candidate;
        }


        // ── Chat bubble builders ───────────────────────────────
        private void ShowWelcome()
        {
            var b = Bubble(BrBgAst, BrBdrA);
            var p = (StackPanel)b.Child;
            p.Children.Add(new TextBlock { Text = "Claude Code  ·  GUI Shell",
                Foreground = BrOrange, FontSize = 15, FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0,0,0,8) });
            foreach (var (t, c) in new (string, SolidColorBrush)[]
            {
                ("Browse to select a folder — session starts automatically.", BrDim),
                ("", BrMuted),
                ("Enter  →  send   ·   Shift+Enter  →  new line", BrDim),
                ("Skills sidebar  →  click + File / + Dir to create", BrDim),
                ("Right-click a skill  →  Use skill  to insert into input", BrDim),
            })
                p.Children.Add(new TextBlock { Text = t, Foreground = c, FontSize = 13,
                    FontFamily = new FontFamily("Consolas, Courier New"),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,1,0,1) });
            ChatPanel.Children.Add(b);
        }

        private void AddUserBubble(string text)
        {
            var b = Bubble(BrBgUser, BrBdrH); var p = (StackPanel)b.Child;
            var hdr = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,6) };
            hdr.Children.Add(new TextBlock { Text = "You", Foreground = BrUser,
                FontSize = 12, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.Bold });
            hdr.Children.Add(new TextBlock { Text = $"  {DateTime.Now:HH:mm}", Foreground = BrMuted,
                FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0,0,0,1) });
            p.Children.Add(hdr);
            foreach (var ln in text.Split('\n'))
                p.Children.Add(new TextBlock { Text = ln.TrimEnd('\r'), Foreground = BrDefault,
                    FontSize = 14, FontFamily = new FontFamily("Consolas, Courier New"),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,1,0,0) });
            ChatPanel.Children.Add(b); OutputScroller.ScrollToBottom();
        }

        private void AddImageBubble(string path)
        {
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.UriSource         = new Uri(path);
                bmp.DecodePixelWidth  = 320;
                bmp.CacheOption       = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit(); bmp.Freeze();

                var img = new Image
                {
                    Source  = bmp,
                    MaxWidth = 320,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    Margin  = new Thickness(16, 4, 16, 4),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                // Rounded border wrap
                var border = new Border
                {
                    CornerRadius    = new CornerRadius(8),
                    ClipToBounds    = true,
                    Margin          = new Thickness(16, 2, 16, 6),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child           = img
                };
                ChatPanel.Children.Add(border);
                OutputScroller.ScrollToBottom();
            }
            catch { /* image display failed, no-op */ }
        }

        private (Border, TextBlock) AddAssistantBubble()
        {
            var b = Bubble(BrBgAst, BrBdrA); var p = (StackPanel)b.Child;
            var hdr = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,6) };
            hdr.Children.Add(new TextBlock { Text = "Claude", Foreground = BrAst,
                FontSize = 12, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.Bold });
            hdr.Children.Add(new TextBlock { Text = $"  {DateTime.Now:HH:mm}", Foreground = BrMuted,
                FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0,0,0,1) });
            if (_hasContinue)
                hdr.Children.Add(new TextBlock { Text = "  ·  continuing", Foreground = BrMuted,
                    FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
                    VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0,0,0,1) });
            p.Children.Add(hdr);
            var tb = new TextBlock { Foreground = BrDefault, FontSize = 14,
                FontFamily = new FontFamily("Consolas, Courier New"),
                TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
            p.Children.Add(tb);
            ChatPanel.Children.Add(b); OutputScroller.ScrollToBottom();
            return (b, tb);
        }

        private static Border Bubble(SolidColorBrush bg, SolidColorBrush bdr) =>
            new Border { Background = bg, BorderBrush = bdr, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10), Margin = new Thickness(12,4,12,4),
                Padding = new Thickness(14,10,14,10), Child = new StackPanel() };

        private void AddDivider() =>
            ChatPanel.Children.Add(new Border { BorderBrush = H("#21262D"),
                BorderThickness = new Thickness(0,1,0,0), Margin = new Thickness(12,6,12,6) });

        private void SysLine(string text, SolidColorBrush fg)
        {
            ChatPanel.Children.Add(new TextBlock { Text = text, Foreground = fg, FontSize = 12,
                FontFamily = new FontFamily("Consolas, Courier New"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16,1,16,1) });
            OutputScroller.ScrollToBottom();
        }

        // ── Permissions ───────────────────────────────────────
        private static bool WritePermissions(string workDir, string skillsDir)
        {
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string settingsDir  = System.IO.Path.Combine(home, ".claude");
                string settingsPath = System.IO.Path.Combine(settingsDir, "settings.json");
                System.IO.Directory.CreateDirectory(settingsDir);

                var newAllows = new List<string>
                {
                    workDir.TrimEnd('\\') + "\\*",
                    skillsDir.TrimEnd('\\') + "\\*",
                };

                string existing = System.IO.File.Exists(settingsPath)
                    ? System.IO.File.ReadAllText(settingsPath).Trim()
                    : "{}";

                var existingAllows = new List<string>();
                var mAllow = System.Text.RegularExpressions.Regex.Match(
                    existing, "\"allow\"\\s*:\\s*\\[(.*?)\\]",
                    System.Text.RegularExpressions.RegexOptions.Singleline);
                if (mAllow.Success)
                {
                    foreach (System.Text.RegularExpressions.Match m in
                        System.Text.RegularExpressions.Regex.Matches(
                            mAllow.Groups[1].Value, "\"([^\"]+)\""))
                        existingAllows.Add(m.Groups[1].Value);
                }

                bool changed = false;
                foreach (var a in newAllows)
                    if (!existingAllows.Contains(a))
                    { existingAllows.Add(a); changed = true; }

                if (!changed) return false;

                var lines = existingAllows.Select(a => "      \"" + a + "\"");
                string allowBlock = string.Join(",\n", lines);
                string newJson =
                    "{\n" +
                    "  \"permissions\": {\n" +
                    "    \"allow\": [\n" +
                    allowBlock + "\n" +
                    "    ]\n" +
                    "  }\n" +
                    "}";

                System.IO.File.WriteAllText(settingsPath, newJson);
                return true;
            }
            catch { return false; }
        }

        // ── Save as Skill ─────────────────────────────────────
        // Sends a prompt to Claude asking it to summarize the conversation
        // into a proper SKILL.md file. Uses --continue so Claude has full context.
        private async void SaveAsSkill_Click(object sender, RoutedEventArgs e)
        {
            if (_claudeExe == null || !_ready)
            { SysLine("Start a session first.", BrYellow); return; }

            // Ask user for a skill name
            var win = new Window
            {
                Title = "Save conversation as Skill",
                Width = 420, Height = 190,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)),
            };
            var root = new StackPanel { Margin = new Thickness(20) };

            root.Children.Add(new TextBlock
            {
                Text = "Skill name  (becomes the /command)",
                Foreground = new SolidColorBrush(Color.FromRgb(0x98, 0x98, 0x9D)),
                FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 6)
            });

            // Auto-suggest name from working dir
            string suggestion = string.IsNullOrEmpty(_workDir) ? "my-skill"
                : System.Text.RegularExpressions.Regex.Replace(
                    Path.GetFileName(_workDir).TrimEnd().ToLowerInvariant(),
                    @"[^a-z0-9\-]", "-").Trim('-');
            if (string.IsNullOrEmpty(suggestion)) suggestion = "my-skill";

            var nameBox = InputBox2(suggestion);
            root.Children.Add(nameBox);

            root.Children.Add(new TextBlock
            {
                Text = "Claude will summarize this conversation into a reusable skill.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x4A)),
                FontSize = 10, FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap
            });

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            var cancelBtn = MakeBtn("Cancel", "#2C2C2E", "#98989D");
            cancelBtn.Click += (_, _) => win.DialogResult = false;
            var goBtn = MakeBtn("Generate Skill", "#0A2540", "#0A84FF");
            goBtn.FontWeight = FontWeights.Bold;
            goBtn.Click += (_, _) => win.DialogResult = true;
            btnRow.Children.Add(cancelBtn);
            btnRow.Children.Add(new Border { Width = 8 });
            btnRow.Children.Add(goBtn);
            root.Children.Add(btnRow);

            win.Content = root;
            win.Loaded += (_, _) => { nameBox.SelectAll(); nameBox.Focus(); };

            if (win.ShowDialog() != true) return;

            string skillName = nameBox.Text.Trim()
                .Replace(" ", "-").Replace("/", "-").ToLowerInvariant();
            if (string.IsNullOrEmpty(skillName)) skillName = "my-skill";

            string mdPath = Path.Combine(_skillsRoot, skillName + ".md");

            // Build the prompt — sent via --continue so Claude has full context
            string nl = "\n";
            string prompt =
                "Please summarize our entire conversation into a reusable Claude Code skill file. " +
                "Write a single markdown file with this exact structure:" + nl +
                "---" + nl +
                "name: " + skillName + nl +
                "description: >" + nl +
                "  [what it does and when to use it]" + nl +
                "---" + nl + nl +
                "# " + skillName + nl + nl +
                "## What this skill does" + nl +
                "[brief overview]" + nl + nl +
                "## Steps" + nl +
                "[numbered steps Claude should follow]" + nl + nl +
                "## Key details learned" + nl +
                "[specific facts, paths, formats discovered this session]" + nl + nl +
                "## Example usage" + nl +
                "[how to invoke this skill]" + nl + nl +
                "Save the file to: " + mdPath + nl +
                "Use the Write tool. Do not ask for permission.";

            // Show in chat first
            AddUserBubble("[Save as Skill: /" + skillName + "]");
            SetBusy(true);

            _cts = new CancellationTokenSource();
            var tok = _cts.Token;
            var (_, tb) = AddAssistantBubble();

            try
            {
                // Flatten newlines, escape quotes for cmd.exe
                // pass prompt safely - strip any quotes
                string safe = prompt.Replace("\n", " ").Replace("\r", " ");
                string args = "/c \"\"" + _claudeExe + "\" --continue --dangerously-skip-permissions -p \"" + safe + "\"\"";

                _job = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe", Arguments = args,
                        WorkingDirectory = _workDir,
                        UseShellExecute = false,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        StandardErrorEncoding  = System.Text.Encoding.UTF8,
                    }
                };
                _job.StartInfo.Environment["NO_COLOR"] = "1";
                _job.StartInfo.Environment["TERM"] = "dumb";
                _job.Start();

                await Task.WhenAll(
                    StreamTo(_job.StandardOutput, tb, false, tok),
                    StreamTo(_job.StandardError,  tb, true,  tok));
                await Task.Run(() => _job.WaitForExit(), tok);

                // Refresh skills panel
                LoadSkillsTree();
                StatusBar.Text = "Skill saved: /" + skillName + "  —  visible in Skills panel";
            }
            catch (OperationCanceledException) { tb.Text += " [cancelled]"; }
            catch (Exception ex) { tb.Text += "\nError: " + ex.Message; tb.Foreground = BrRed; }
            finally
            {
                _job?.Dispose(); _job = null;
                SetBusy(false);
                OutputScroller.ScrollToBottom();
                InputBox.Focus();
            }
        }

        // ── Image attach ──────────────────────────────────────

        private void AttachImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select an image to send",
                Filter = "Images (*.png;*.jpg;*.jpeg;*.webp;*.gif)|*.png;*.jpg;*.jpeg;*.webp;*.gif",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() != true) return;
            _attachedImagePath = dlg.FileName;
            ShowImagePreview(_attachedImagePath);
        }

        private void ClearImage_Click(object sender, RoutedEventArgs e)
        {
            _attachedImagePath = null;
            ImagePreviewBar.Visibility = Visibility.Collapsed;
            ImagePreviewThumb.Source   = null;
            AttachImageBtn.Background  = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
        }

        private void ShowImagePreview(string path)
        {
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.UriSource         = new Uri(path);
                bmp.DecodePixelHeight = 80;
                bmp.CacheOption       = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();

                ImagePreviewThumb.Source = bmp;
                ImagePreviewName.Text    = System.IO.Path.GetFileName(path);

                var info = new System.IO.FileInfo(path);
                ImagePreviewSize.Text    = info.Length < 1024 * 1024
                    ? $"{info.Length / 1024} KB"
                    : $"{info.Length / (1024 * 1024.0):F1} MB";

                ImagePreviewBar.Visibility = Visibility.Visible;
                // Tint the attach button to indicate image is loaded
                AttachImageBtn.Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x1F, 0x35));
            }
            catch (Exception ex)
            {
                SysLine("Cannot load image: " + ex.Message, BrRed);
                _attachedImagePath = null;
            }
        }

        // ── State ──────────────────────────────────────────────
        private void SetReady(bool ok)
        {
            _ready = ok;
            BrowseButton.Background = ok ? BrBrowseGreen : BrBrowseGray;
            SendButton.IsEnabled = ok;
            AttachImageBtn.IsEnabled = ok;
            StopButton.IsEnabled = false;
            StatusDot.Fill  = ok ? BrGreen : BrMuted;
            StatusText.Text = ok ? "Ready" : "Idle";
            SubtitleLabel.Text = ok && _claudeExe != null ? Path.GetFileName(_claudeExe) : "Not started";
            StatusBar.Text = ok ? "Ready — type a message and press Enter"
                                : "claude not found — see messages above";
        }

        private void SetBusy(bool on)
        {
            _busy = on;
            StopButton.IsEnabled = on;
            StatusDot.Fill  = on ? BrOrange : BrGreen;
            StatusText.Text = on ? "Running" : "Ready";
            ThinkingBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

            if (on)
            {
                _fi = 0; _lastLine = ""; _progressWidth = 0;
                _activityLog.Clear(); _lastOutputTime = DateTime.Now;
                SpinnerText.Text = Frames[0];
                ThinkingLabel.Text = "Sending";
                LiveOutputLabel.Text = "Waiting for response\u2026";
                LiveOutputLabel.Foreground = BrMuted;
                ElapsedLabel.Text = "0s";
                ElapsedLabel.Foreground = BrBlue;
                ThinkingSubLabel.Text = "";
                _sw.Restart();
                _timer.Start();
                StatusBar.Text = "Sent \u2014 waiting for Claude\u2026";
                SendButton.Content = "\u280b  Running";
                SendButton.Background = H("#5C3000");
                SendButton.Foreground = H("#FF9F0A");
                SendButton.IsEnabled = false;
            }
            else
            {
                _timer.Stop(); _sw.Stop();
                string secs = $"{_sw.Elapsed.TotalSeconds:F1}s";
                _progressWidth = 0;
                ProgressBarFill.Width = 0;
                _activityLog.Clear();
                ThinkingLabel.Text = "Done";
                LiveOutputLabel.Text = "Response received.";
                LiveOutputLabel.Foreground = BrGreen;
                ElapsedLabel.Text = secs;
                ElapsedLabel.Foreground = BrGreen;
                ThinkingSubLabel.Text = "";
                StatusBar.Text = $"Ready  \u00b7  {_turns} turn{(_turns == 1 ? "" : "s")}  \u00b7  response in {secs}";
                SendButton.Content = "Send";
                SendButton.Background = H("#0A84FF");
                SendButton.Foreground = H("#FFFFFF");
                SendButton.IsEnabled = _ready;
            }
        }

        // ── Finder ────────────────────────────────────────────
        private static IEnumerable<string> Candidates()
        {
            string local   = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string home    = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new[]
            {
                Path.Combine(local,   "AnthropicClaude", "claude.exe"),
                Path.Combine(local,   "Programs", "claude", "claude.exe"),
                Path.Combine(home,    ".claude", "local", "claude.exe"),
                Path.Combine(local,   "Claude", "claude.exe"),
                Path.Combine(roaming, "npm", "claude.cmd"),
                Path.Combine(local,   "npm", "claude.cmd"),
            };
        }

        private static string? FindClaude()
        {
            foreach (var p in Candidates()) if (File.Exists(p)) return p;
            try
            {
                using var w = Process.Start(new ProcessStartInfo {
                    FileName = "cmd.exe", Arguments = "/c where claude",
                    UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
                string? r = w?.StandardOutput.ReadLine(); w?.WaitForExit();
                if (!string.IsNullOrWhiteSpace(r) && File.Exists(r.Trim())) return r.Trim();
            }
            catch { }
            return null;
        }

        private static string Strip(string s)
        {
            s = Ansi.Replace(s, ""); s = s.Replace("\x1B", "");
            s = s.Replace("\r\n", "\n").Replace("\r", "\n");
            return s;
        }

        private static SolidColorBrush H(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze(); return b;
        }
    }
}
