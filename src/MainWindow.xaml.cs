using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ConCli.History;
using ConCli.Terminal;




// Only the appearance API is imported. Wpf.Ui.Controls is deliberately *not*:
// it defines Button / TextBox / MessageBox names that collide with System.Windows.Controls.
using Wpf.Ui.Appearance;

namespace ConCli
{
    /// <summary>One row in the 最近 sidebar. Flat on purpose — the list is virtualized.</summary>
    public sealed class HistoryItem
    {
        public required SessionRecord Record { get; init; }
        public required string Title    { get; init; }
        public required string Project  { get; init; }
        public required string TurnText { get; init; }  // "12 条"
        public required string Ago      { get; init; }  // "3分钟前" / "1天前" / "03-14"
        public required string Bucket   { get; init; }  // 已置顶 / 今天 / 昨天 / …

        public bool Pinned => Record.Pinned;
        public bool Favorite => Record.Favorite;
    }

    /// <summary>A top-level row in the 文件夹 view: one working directory, like an Explorer folder.</summary>
    public sealed class ProjectEntry
    {
        public required string Cwd  { get; init; }
        public required string Name { get; init; }
        public required string Info { get; init; }  // "12 个对话"
        public required string Ago  { get; init; }
    }

    /// <summary>One terminal pane plus its tab-strip state.</summary>
    public sealed class TerminalTab : INotifyPropertyChanged
    {
        public TerminalTab(TerminalView view, string title, string cwd)
        {
            View = view; _title = title; Cwd = cwd;
        }

        public TerminalView View { get; }
        public string Cwd { get; }

        private string _title;
        public string Title { get => _title; set => Set(ref _title, value); }

        private bool _isActive;
        public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

        private bool _exited;
        public bool Exited { get => _exited; set => Set(ref _exited, value); }

        // Set by ChromeTabPanel as the strip fills up; the tab template sheds parts on them.
        private bool _narrow;
        public bool Narrow { get => _narrow; set => Set(ref _narrow, value); }

        private bool _tiny;
        public bool Tiny { get => _tiny; set => Set(ref _tiny, value); }

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        private void Raise(string? name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
    {
        private const int HistoryLimit = 500;

        private readonly ObservableCollection<TerminalTab> _tabs = new();
        private TerminalTab? _active;

        // The strip's panel, so a tab switch can re-slide an overflowing strip.
        private ChromeTabPanel? _tabPanel;

        // A separate view over the same tabs: filtering the default view would filter the
        // strip too.
        private ListCollectionView? _tabSearchView;

        // A click on the ⌄ button first closes the open popup (StaysOpen=False), then lands
        // as a Click that would reopen it. Remember when it closed to swallow that click.
        private DateTime _tabSearchClosedAt;

        // Search is re-run on a timer rather than per keystroke: every run is a SQLite
        // round trip on a worker thread, and CJK IME commits fire TextChanged in bursts.
        private readonly DispatcherTimer _searchDebounce =
            new() { Interval = TimeSpan.FromMilliseconds(180) };

        // Only the newest query's results are allowed to reach the list.
        private int _queryEpoch;

        // Remembered across a sidebar collapse so it comes back the width you left it.
        private double _sidebarWidth = 292;

        private static readonly string LastDirFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ConCli", "lastdir.txt");

        public MainWindow()
        {
            InitializeComponent();

            TabStrip.ItemsSource = _tabs;
            _tabSearchView = new ListCollectionView(_tabs) { Filter = TabSearchFilter };
            TabSearchList.ItemsSource = _tabSearchView;
            WorkingDirBox.Text = LoadLastDir();
            UpdateShellLabel();

            _searchDebounce.Tick += (_, _) =>
            {
                _searchDebounce.Stop();
                _ = ReloadSidebarAsync();
            };

            InputBindings.Add(new KeyBinding(
                new SimpleCommand(OpenTerminalHere), Key.T, ModifierKeys.Control));
            InputBindings.Add(new KeyBinding(
                new SimpleCommand(() => CloseTab(_active)), Key.W, ModifierKeys.Control));
            InputBindings.Add(new KeyBinding(
                new SimpleCommand(OpenTabSearch), Key.A, ModifierKeys.Control | ModifierKeys.Shift));

            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        // ══════════════════════════════════════════════════════
        // Lifecycle
        // ══════════════════════════════════════════════════════

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Loaded can fire again when the window template is re-applied (theme swaps do
            // that), and each extra run would open another shell.
            Loaded -= OnLoaded;

            // Opens straight into a shell, the way PowerShell itself does — in the folder
            // the launch bar remembers from last time.
            OpenTerminalHere();
            await RefreshHistoryAsync();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            foreach (var t in _tabs.ToList())
            {
                try { t.View.Dispose(); } catch { /* shutting down anyway */ }
            }
            SaveLastDir(WorkingDirBox.Text);
        }

        // ══════════════════════════════════════════════════════
        // Dir bar  —  "cd into this folder"
        // ══════════════════════════════════════════════════════

        private void BrowseDir_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择工作目录",
                InitialDirectory = Directory.Exists(WorkingDirBox.Text)
                    ? WorkingDirBox.Text
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            if (dlg.ShowDialog(this) == true)
            {
                WorkingDirBox.Text = dlg.FolderName;
                OpenTerminalHere();
            }
        }

        private void WorkingDir_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            OpenTerminalHere();
        }

        private void WorkingDir_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateShellLabel();
        }

        /// <summary>The tab-strip ＋ opens in the launch bar's folder, same as Enter in the box.</summary>
        private void NewTab_Click(object sender, RoutedEventArgs e) => OpenTerminalHere();

        private void OpenTerminalHere()
        {
            string dir = WorkingDirBox.Text.Trim().Trim('"');
            if (!Directory.Exists(dir))
            {
                Status($"目录不存在：{dir}");
                return;
            }
            WorkingDirBox.Text = dir;
            SaveLastDir(dir);
            _ = OpenTerminalAsync(dir, null);
        }

        /// <summary>
        /// Sidebar footer: which shell, and where this folder's terminals get recorded.
        /// Worth showing at all times — the answer moves with the folder, and a record you
        /// cannot find is the same as no record.
        /// </summary>
        private void UpdateShellLabel()
        {
            string dir = WorkingDirBox.Text.Trim().Trim('"');
            string where = Directory.Exists(dir)
                ? HistoryPaths.ProjectHistoryDir(dir)
                : HistoryPaths.SessionsDir;

            ShellLabel.Text = $"{Path.GetFileName(Shell.ResolveExe())} · {where}";
            ShellLabel.ToolTip = $"{Shell.ResolveExe()}\n终端记录：{where}\n索引：{HistoryPaths.IndexDb}";
        }

        // ══════════════════════════════════════════════════════
        // Terminal tabs
        // ══════════════════════════════════════════════════════

        /// <summary>
        /// Opens a pane. <paramref name="initialCommand"/> is run once the shell is up — that
        /// is how a resumed session gets "claude --resume &lt;id&gt;" instead of a bare prompt.
        /// It is built into the command line rather than written to stdin, because a write
        /// right after Start() races the shell's own initialisation.
        /// </summary>
        private async Task OpenTerminalAsync(string cwd, string? initialCommand, int? index = null)
        {
            var view = new TerminalView
            {
                WorkingDirectory = cwd,
                CommandLine      = Shell.CommandLineWith(initialCommand),
                TeePath          = HistoryPaths.NewTerminalTeeFile(cwd),
            };

            var tab = new TerminalTab(view, FolderName(cwd), cwd);

            view.TitleChanged += t => Dispatcher.BeginInvoke(() => tab.Title = Compose(t, cwd));
            // `exit` closes the tab, as it does in Windows Terminal.
            view.ProcessExited += _ => Dispatcher.BeginInvoke(() =>
            {
                tab.Exited = true;
                CloseTab(tab);
            });

            if (index is int at && at >= 0 && at < _tabs.Count) _tabs.Insert(at, tab);
            else _tabs.Add(tab);
            TerminalHost.Children.Add(view);
            ActivateTab(tab);

            try
            {
                await view.InitializeAsync();
                view.FocusTerminal();
            }
            catch (Exception ex)
            {
                // The WebView2 runtime is the only realistic failure here, and it is fatal
                // for this pane — drop the tab rather than leave a blank one.
                CloseTab(tab);
                Status("无法启动终端：" + ex.Message);
            }
        }

        private void ActivateTab(TerminalTab? tab)
        {
            _active = tab;
            foreach (var t in _tabs)
            {
                t.IsActive = t == tab;
                t.View.Visibility = t == tab ? Visibility.Visible : Visibility.Collapsed;
            }
            TerminalEmptyHint.Visibility = _tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _tabPanel?.InvalidateArrange();   // keep the active tab in view when overflowing
            UpdateShellLabel();

            tab?.View.FocusTerminal();
        }

        /// <summary>
        /// Dragging and double-click-maximize for the top band. Hand-rolled because the
        /// band is ordinary content now: <c>ui:TitleBar</c> covers only the caption buttons,
        /// so nothing else in that row would move the window on its own.
        /// </summary>
        private void TitleBand_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal : WindowState.Maximized;
                return;
            }

            // Throws if the button came up between the event and here — a fast click does that.
            try { DragMove(); } catch (InvalidOperationException) { }
        }

        private void Tab_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is TerminalTab t) ActivateTab(t);
            // The strip *is* the title bar, so without this a click on a tab drags the window.
            e.Handled = true;
        }

        private void TabClose_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is TerminalTab t) CloseTab(t);
            e.Handled = true;   // don't also activate the tab we just closed
        }

        private void CloseTab(TerminalTab? tab)
        {
            if (tab == null || !_tabs.Contains(tab)) return;

            int i = _tabs.IndexOf(tab);
            _tabs.Remove(tab);
            TerminalHost.Children.Remove(tab.View);
            try { tab.View.Dispose(); } catch { /* the pty may already be gone */ }

            ActivateTab(_tabs.Count == 0 ? null : _tabs[Math.Min(i, _tabs.Count - 1)]);
        }

        // ── tab context menu (Chrome's) ───────────────────────────

        private static TerminalTab? TabOf(object sender) =>
            (sender as FrameworkElement)?.DataContext as TerminalTab;

        /// <summary>Middle-click closes, as in every browser.</summary>
        private void Tab_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            CloseTab(TabOf(sender));
            e.Handled = true;
        }

        private void TabMenu_Opened(object sender, RoutedEventArgs e)
        {
            var menu = (ContextMenu)sender;
            if (TabOf(menu) is not { } t) return;
            int i = _tabs.IndexOf(t);
            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                if (Equals(item.Tag, "others")) item.IsEnabled = _tabs.Count > 1;
                if (Equals(item.Tag, "right"))  item.IsEnabled = i >= 0 && i < _tabs.Count - 1;
            }
        }

        private void TabNewRight_Click(object sender, RoutedEventArgs e)
        {
            if (TabOf(sender) is not { } t) return;
            string dir = WorkingDirBox.Text.Trim().Trim('"');
            if (!Directory.Exists(dir)) dir = t.Cwd;
            _ = OpenTerminalAsync(dir, null, _tabs.IndexOf(t) + 1);
        }

        /// <summary>A fresh shell in the same folder — the shell's state is not copyable.</summary>
        private void TabDuplicate_Click(object sender, RoutedEventArgs e)
        {
            if (TabOf(sender) is not { } t) return;
            _ = OpenTerminalAsync(t.Cwd, null, _tabs.IndexOf(t) + 1);
        }

        private void TabReveal_Click(object sender, RoutedEventArgs e)
        {
            if (TabOf(sender) is not { } t) return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{t.Cwd}\"") { UseShellExecute = true }); }
            catch (Exception ex) { Status("打不开资源管理器：" + ex.Message); }
        }

        private void TabCopyPath_Click(object sender, RoutedEventArgs e)
        {
            if (TabOf(sender) is not { } t) return;
            try { Clipboard.SetText(t.Cwd); Status("已复制路径：" + t.Cwd); }
            catch (Exception ex) { Status("复制失败：" + ex.Message); }
        }

        /// <summary>
        /// Saves what the pane shows (screen + scrollback, VT already resolved) as a .txt in
        /// the tab's own history folder, where the 文件夹 view will list it.
        /// </summary>
        private async void TabSaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (TabOf(sender) is not { } t) return;

            string dir = HistoryPaths.ProjectHistoryDir(t.Cwd);
            string suggested = SafeFileName($"{t.Title}_{DateTime.Now:yyyyMMdd-HHmm}");
            var dlg = new RenameDialog(suggested, "另存为", $"保存到 {dir}") { Owner = this };
            if (dlg.ShowDialog() != true) return;

            string name = dlg.Value.Trim();
            if (name.Length == 0) return;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                Status("文件名不能包含 \\ / : * ? \" < > |");
                return;
            }
            if (!name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) name += ".txt";

            string path = Path.Combine(dir, name);
            if (File.Exists(path) &&
                MessageBox.Show(this, $"{path}\n\n已存在，要覆盖吗？", "另存为", MessageBoxButton.OKCancel,
                                MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
                return;

            try
            {
                string text = await t.View.DumpAsync();
                Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(path, text.TrimEnd() + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Status("另存为失败：" + ex.Message);
                return;
            }
            Status("已另存为：" + path);
        }

        /// <summary>A tab title as a file name: invalid characters become "_".</summary>
        private static string SafeFileName(string s)
        {
            var bad = Path.GetInvalidFileNameChars();
            var chars = s.Select(c => Array.IndexOf(bad, c) >= 0 ? '_' : c).ToArray();
            return new string(chars).Trim().TrimEnd('.');
        }

        private void TabCloseOthers_Click(object sender, RoutedEventArgs e)
        {
            if (TabOf(sender) is not { } keep) return;
            foreach (var t in _tabs.Where(t => t != keep).ToList()) CloseTab(t);
            ActivateTab(keep);
        }

        private void TabCloseRight_Click(object sender, RoutedEventArgs e)
        {
            if (TabOf(sender) is not { } t) return;
            int i = _tabs.IndexOf(t);
            foreach (var r in _tabs.Skip(i + 1).ToList()) CloseTab(r);
            if (_active == null || !_tabs.Contains(_active)) ActivateTab(t);
        }

        private void TabPanel_Loaded(object sender, RoutedEventArgs e) =>
            _tabPanel = sender as ChromeTabPanel;

        // ── tab search (Chrome's ⌄ drop-down) ─────────────────────

        private void TabSearch_Click(object sender, RoutedEventArgs e)
        {
            if ((DateTime.Now - _tabSearchClosedAt).TotalMilliseconds < 250) return;
            OpenTabSearch();
        }

        private void OpenTabSearch()
        {
            TabSearchBox.Text = "";
            TabSearchPopup.IsOpen = true;
        }

        private void TabSearchPopup_Opened(object? sender, EventArgs e)
        {
            RefreshTabSearch();
            TabSearchList.SelectedItem = _active;
            Dispatcher.BeginInvoke(() => TabSearchBox.Focus(), DispatcherPriority.Input);
        }

        private void TabSearchPopup_Closed(object? sender, EventArgs e) =>
            _tabSearchClosedAt = DateTime.Now;

        private bool TabSearchFilter(object o)
        {
            string q = TabSearchBox.Text.Trim();
            return q.Length == 0 || o is TerminalTab t &&
                   (t.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    t.Cwd.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        private void RefreshTabSearch()
        {
            _tabSearchView?.Refresh();
            int shown = _tabSearchView?.Count ?? 0;
            TabSearchHeader.Text = TabSearchBox.Text.Trim().Length == 0
                ? $"打开的标签页（{_tabs.Count}）"
                : $"匹配的标签页（{shown}/{_tabs.Count}）";
            if (TabSearchList.SelectedItem == null && shown > 0) TabSearchList.SelectedIndex = 0;
        }

        private void TabSearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
            RefreshTabSearch();

        private void TabSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down:
                case Key.Up:
                    int n = TabSearchList.Items.Count;
                    if (n == 0) break;
                    int i = TabSearchList.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
                    TabSearchList.SelectedIndex = (i + n) % n;
                    TabSearchList.ScrollIntoView(TabSearchList.SelectedItem);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    PickTabFromSearch(TabSearchList.SelectedItem as TerminalTab);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    TabSearchPopup.IsOpen = false;
                    _active?.View.FocusTerminal();
                    e.Handled = true;
                    break;
            }
        }

        private void TabSearchList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            PickTabFromSearch(TabSearchList.SelectedItem as TerminalTab);
            e.Handled = true;
        }

        private void TabSearchList_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) != null) return;
            if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext
                is TerminalTab t)
                PickTabFromSearch(t);
        }

        private void PickTabFromSearch(TerminalTab? t)
        {
            if (t == null) return;
            TabSearchPopup.IsOpen = false;
            ActivateTab(t);
        }

        private void TabSearchClose_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.DataContext is TerminalTab t) CloseTab(t);
            RefreshTabSearch();
            if (_tabs.Count == 0) TabSearchPopup.IsOpen = false;
        }

        /// <summary>
        /// Shells set the OSC title to their own exe path, which is noise. Keep it only when a
        /// program (claude, vim, git) has set something meaningful.
        /// </summary>
        private static string Compose(string oscTitle, string cwd)
        {
            string t = (oscTitle ?? "").Trim();
            string folder = FolderName(cwd);
            if (t.Length == 0) return folder;

            bool isShellNoise =
                t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                t.Contains(Path.DirectorySeparatorChar) ||
                t.Equals(cwd, StringComparison.OrdinalIgnoreCase);

            return isShellNoise ? folder : $"{t} — {folder}";
        }

        // ══════════════════════════════════════════════════════
        // History sidebar
        // ══════════════════════════════════════════════════════

        private void RefreshHistory_Click(object sender, RoutedEventArgs e)
        {
            _ = RefreshHistoryAsync();
        }

        /// <summary>Rescan the CLIs' transcripts, then repaint the list.</summary>
        private async Task RefreshHistoryAsync()
        {
            Status("正在扫描历史…");
            try
            {
                // Every CLI in TranscriptAdapters.All (claude, codex, …).
                var stats = await Task.Run(() =>
                {
                    using var db = new HistoryDb();
                    return HistoryIngest.Run(db);
                });
                Status(stats.TurnsAdded > 0
                    ? $"已索引 {stats.FilesRead}/{stats.FilesSeen} 个记录文件，新增 {stats.TurnsAdded} 条对话"
                    : "历史已是最新");
            }
            catch (Exception ex)
            {
                Status("扫描历史失败：" + ex.Message);
            }
            await ReloadSidebarAsync();
        }

        /// <summary>Query the index and rebuild the list. Never touches SQLite on the UI thread.</summary>
        private async Task ReloadHistoryAsync(string query)
        {
            int epoch = ++_queryEpoch;
            string q = (query ?? "").Trim();
            bool favOnly = FavoritesToggle.IsChecked == true;
            List<HistoryItem> items;
            try
            {
                items = await Task.Run(() =>
                {
                    using var db = new HistoryDb();
                    var rows = q.Length == 0
                        ? db.RecentSessions(HistoryLimit, favoritesOnly: favOnly)
                        : db.Search(q, HistoryLimit, favOnly);

                    var now = DateTimeOffset.Now;
                    return rows.Select(r => ToItem(r, now)).ToList();
                });
            }
            catch (Exception ex)
            {
                if (epoch == _queryEpoch) Status("读取历史失败：" + ex.Message);
                return;
            }

            if (epoch != _queryEpoch) return;   // a newer query already landed

            HistoryList.ItemsSource = items;
            var view = CollectionViewSource.GetDefaultView(items);
            view.GroupDescriptions.Clear();
            // Rows arrive newest-first, so buckets come out in order without an explicit sort.
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HistoryItem.Bucket)));

            HistoryCountLabel.Text = items.Count == 0 ? "" : items.Count.ToString();
            HistoryHeader.Text = favOnly ? "收藏" : "最近";
            HistoryEmptyLabel.Text = (q.Length, favOnly) switch
            {
                (0, false) => "没有历史记录",
                (0, true)  => "还没有收藏 — 在记录上点 ☆ 即可收藏",
                (_, false) => $"没有匹配 “{q}” 的记录",
                (_, true)  => $"收藏里没有匹配 “{q}” 的记录",
            };
            HistoryEmptyLabel.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <param name="project">Replaces the folder name on the second line.</param>
        private static HistoryItem ToItem(SessionRecord r, DateTimeOffset now, string? project = null)
        {
            var local = r.UpdatedAt.ToLocalTime();
            return new HistoryItem
            {
                Record   = r,
                Title    = string.IsNullOrWhiteSpace(r.Title) ? "(无标题)" : r.Title!.Replace('\n', ' ').Trim(),
                Project  = project ?? FolderName(r.Cwd),
                TurnText = $"{r.TurnCount} 条",
                Ago      = AgoOf(local, now),
                Bucket   = r.Pinned ? "已置顶" : BucketOf(local, now),
            };
        }

        /// <summary>
        /// Right-hand timestamp. Coarse on purpose: the bucket header already says which day,
        /// so this only has to answer "how long ago" at a glance.
        /// </summary>
        private static string AgoOf(DateTimeOffset at, DateTimeOffset now)
        {
            var span = now - at;
            if (span < TimeSpan.Zero)      return at.ToString("HH:mm");   // clock skew
            if (span.TotalMinutes < 1)     return "刚刚";
            if (span.TotalHours   < 1)     return $"{(int)span.TotalMinutes} 分钟前";
            if (span.TotalHours   < 24)    return $"{(int)span.TotalHours} 小时前";
            if (span.TotalDays    < 30)    return $"{(int)span.TotalDays} 天前";
            return at.Year == now.Year ? at.ToString("MM-dd") : at.ToString("yyyy-MM");
        }

        private static string BucketOf(DateTimeOffset at, DateTimeOffset now)
        {
            int days = (now.Date - at.Date).Days;
            if (days <= 0)  return "今天";
            if (days == 1)  return "昨天";
            if (days <= 7)  return "过去 7 天";
            if (days <= 30) return "过去 30 天";
            return at.Year == now.Year ? $"{at.Month} 月" : $"{at.Year} 年 {at.Month} 月";
        }

        private void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            // ui:TextBox draws its own placeholder, so there is nothing to toggle here.
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }

        private void HistoryList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            // The ListBox keeps its selection, so a double-click on empty space below the
            // last row would otherwise re-open whatever was selected earlier.
            if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) == null) return;
            if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) != null) return;
            Resume(HistoryList.SelectedItem as HistoryItem);
        }

        private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
        {
            while (d != null && d is not T)
                d = d is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            return d as T;
        }

        private void HistoryList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Resume(HistoryList.SelectedItem as HistoryItem);
        }

        /// <summary>Open a terminal in that session's folder and hand it back to the CLI.</summary>
        private void Resume(HistoryItem? item)
        {
            if (item is null) return;

            var r = item.Record;
            string cwd = Directory.Exists(r.Cwd)
                ? r.Cwd
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (!Directory.Exists(r.Cwd))
                Status($"原目录已不存在：{r.Cwd} — 在用户目录打开");

            // The resume line comes from the adapter that indexed the record, so a session
            // written by claude is resumed with claude's own syntax. If the CLI is not on
            // PATH the shell says so — which is more use than this app refusing to open.
            WorkingDirBox.Text = cwd;
            _ = OpenTerminalAsync(cwd, r.ResumeCommand);
        }

        // ══════════════════════════════════════════════════════
        // Sidebar row actions
        // ══════════════════════════════════════════════════════

        /// <summary>The pin button and the context-menu entry share this.</summary>
        private async void PinItem_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (ItemOf(sender) is not { } it) return;

            bool pin = !it.Record.Pinned;
            await WithDb(db => db.SetPinned(it.Record.Cli, it.Record.Id, pin));
            Status(pin ? $"已置顶：{it.Title}" : $"已取消置顶：{it.Title}");
            await ReloadSidebarAsync();
        }

        /// <summary>The star button and the context-menu entry share this.</summary>
        private async void FavoriteItem_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (ItemOf(sender) is not { } it) return;
            bool fav = !it.Record.Favorite;
            await WithDb(db => db.SetFavorite(it.Record.Cli, it.Record.Id, fav));
            Status(fav ? $"已收藏：{it.Title}" : $"已取消收藏：{it.Title}");
            await ReloadSidebarAsync();
        }

        /// <summary>Stored beside the CLI's title, so a later ai-title does not undo it.</summary>
        private void RenameItem_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (ItemOf(sender) is { } it) RenameSession(it);
        }

        private async void RenameSession(HistoryItem it)
        {
            var dlg = new RenameDialog(it.Record.Title ?? "") { Owner = this };
            if (dlg.ShowDialog() != true) return;

            string name = dlg.Value.Trim();
            await WithDb(db => db.SetCustomTitle(it.Record.Cli, it.Record.Id, name));
            Status(name.Length == 0 ? $"已恢复原标题：{it.Title}" : $"已重命名为：{name}");
            if (FolderModeOn) await ReloadFolderAsync(select: it);
            else await ReloadHistoryAsync(SearchBoxInput.Text);
        }

        private void FavoritesToggle_Changed(object sender, RoutedEventArgs e)
        {
            // Fires once mid-parse if IsChecked is ever set in XAML; the list is not up yet.
            if (HistoryList is null) return;
            FavoritesGlyph.Filled = FavoritesToggle.IsChecked == true;
            _ = ReloadHistoryAsync(SearchBoxInput.Text);
        }

        /// <summary>Removes the row but not the transcript. Undo lives in the ⋯ menu.</summary>
        private async void HideItem_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (ItemOf(sender) is not { } it) return;

            await WithDb(db => db.HideSession(it.Record.Cli, it.Record.Id, it.Record.NativePath));
            Status($"已从列表移除：{it.Title}（原始记录文件保留）");
            await ReloadSidebarAsync();
        }

        /// <summary>Deletes the CLI's own transcript file too. Always confirms first.</summary>
        private async void PurgeItem_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (ItemOf(sender) is not { } it) return;

            string? file = it.Record.NativePath;
            string body = file is null
                ? $"从索引中彻底删除「{it.Title}」？"
                : $"从索引中彻底删除「{it.Title}」，并删除原始记录文件：\n\n{file}\n\n此操作不可撤销。";

            if (MessageBox.Show(this, body, "彻底删除", MessageBoxButton.OKCancel,
                                MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
                return;

            await WithDb(db => db.HideSession(it.Record.Cli, it.Record.Id, file));

            if (file != null)
            {
                try { File.Delete(file); }
                catch (Exception ex)
                {
                    // The index entry is already gone, so say exactly what survived.
                    Status($"已从索引删除，但记录文件删不掉：{ex.Message}");
                    await ReloadSidebarAsync();
                    return;
                }
            }
            Status($"已彻底删除：{it.Title}");
            await ReloadSidebarAsync();
        }

        private void OpenFolderTerminal_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is not { } it || !Directory.Exists(it.Record.Cwd)) return;
            WorkingDirBox.Text = it.Record.Cwd;
            _ = OpenTerminalAsync(it.Record.Cwd, null);
        }

        private void RevealFolder_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is not { } it) return;
            string target = it.Record.NativePath is { } f && File.Exists(f)
                ? $"/select,\"{f}\""
                : $"\"{it.Record.Cwd}\"";
            try { Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true }); }
            catch (Exception ex) { Status("打不开资源管理器：" + ex.Message); }
        }

        private void CopyId_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is not { } it) return;
            try { Clipboard.SetText(it.Record.Id); Status("已复制会话 ID：" + it.Record.Id); }
            catch (Exception ex) { Status("复制失败：" + ex.Message); }
        }

        private async void More_Click(object sender, RoutedEventArgs e)
        {
            var btn = (Button)sender;
            int hidden = 0;
            try { hidden = await Task.Run(() => { using var db = new HistoryDb(); return db.HiddenCount; }); }
            catch (Exception ex) { Status("读取历史失败：" + ex.Message); return; }

            RestoreHiddenItem.Header = $"恢复已移除的记录（{hidden}）";
            RestoreHiddenItem.IsEnabled = hidden > 0;

            btn.ContextMenu!.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }

        private async void RestoreHidden_Click(object sender, RoutedEventArgs e)
        {
            int n = 0;
            try { n = await Task.Run(() => { using var db = new HistoryDb(); return db.UnhideAll(); }); }
            catch (Exception ex) { Status("恢复失败：" + ex.Message); return; }

            Status($"已解除 {n} 条记录的移除标记，正在重新索引…");
            await RefreshHistoryAsync();   // tombstones are gone; the rescan re-reads those files
        }

        private void OpenHistoryRoot_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(HistoryPaths.Root);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{HistoryPaths.Root}\"")
                { UseShellExecute = true });
            }
            catch (Exception ex) { Status("打不开历史目录：" + ex.Message); }
        }

        /// <summary>Opens the history folder of the directory currently in the dir bar.</summary>
        private void OpenProjectHistory_Click(object sender, RoutedEventArgs e)
        {
            string dir = WorkingDirBox.Text.Trim().Trim('"');
            if (!Directory.Exists(dir)) { Status($"目录不存在：{dir}"); return; }

            string hist = HistoryPaths.ProjectHistoryDir(dir);
            if (!Directory.Exists(hist))
            {
                // Not created here on purpose: an empty folder in someone's repo, made by a
                // menu click that was only meant to look, is litter.
                Status($"还没有记录：{hist}（在此目录开一个终端后才会生成）");
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{hist}\"")
                { UseShellExecute = true });
            }
            catch (Exception ex) { Status("打不开记录目录：" + ex.Message); }
        }

        private static HistoryItem? ItemOf(object sender) =>
            (sender as FrameworkElement)?.DataContext as HistoryItem;

        private static async Task WithDb(Action<HistoryDb> work) =>
            await Task.Run(() => { using var db = new HistoryDb(); work(db); });

        // ══════════════════════════════════════════════════════
        // Folder view  —  the index grouped by working directory, browsed like Explorer.
        // Virtual: nothing here is on disk, so a rename is a session title, not a file.
        // ══════════════════════════════════════════════════════

        // The project being browsed; null = the top level, one row per project.
        private string? _folderProject;
        private int _folderEpoch;

        // Null-safe: the mode buttons fire Checked mid-parse, before the fields are assigned.
        private bool FolderModeOn => FolderMode?.IsChecked == true;

        private void SidebarMode_Changed(object sender, RoutedEventArgs e)
        {
            if (SessionsPanel is null || FolderPanel is null) return;

            bool folder = FolderModeOn;
            SessionsPanel.Visibility     = folder ? Visibility.Collapsed : Visibility.Visible;
            FolderPanel.Visibility       = folder ? Visibility.Visible   : Visibility.Collapsed;
            FavoritesToggle.Visibility   = folder ? Visibility.Collapsed : Visibility.Visible;
            HistoryCountLabel.Visibility = folder ? Visibility.Collapsed : Visibility.Visible;
            SearchBoxInput.PlaceholderText = folder ? "筛选…" : "搜索会话…";

            _ = ReloadSidebarAsync();
        }

        /// <summary>Repaints whichever view is showing — row actions work in both.</summary>
        private Task ReloadSidebarAsync() =>
            FolderModeOn ? ReloadFolderAsync() : ReloadHistoryAsync(SearchBoxInput.Text);

        private async Task ReloadFolderAsync(object? select = null)
        {
            int epoch = ++_folderEpoch;
            string? project = _folderProject;
            string q = SearchBoxInput.Text.Trim();
            List<object> items;
            try
            {
                items = await Task.Run(() =>
                {
                    using var db = new HistoryDb();
                    var now = DateTimeOffset.Now;
                    // The filter is a plain name match, not FTS: at this size a substring
                    // test is instant, and it matches what the rows actually show.
                    bool Hit(string s) => q.Length == 0 || s.Contains(q, StringComparison.OrdinalIgnoreCase);

                    if (project is null)
                        return db.Projects()
                            .Where(p => Hit(p.Cwd))
                            .Select(p => (object)new ProjectEntry
                            {
                                Cwd  = p.Cwd,
                                Name = FolderName(p.Cwd),
                                Info = $"{p.SessionCount} 个对话",
                                Ago  = AgoOf(p.UpdatedAt.ToLocalTime(), now),
                            })
                            .ToList();

                    return db.RecentSessions(HistoryLimit, cwd: project)
                        // Inside a project the folder name is redundant; say which CLI instead.
                        .Select(r => ToItem(r, now, project: r.Cli))
                        .Where(i => Hit(i.Title))
                        .Cast<object>()
                        .ToList();
                });
            }
            catch (Exception ex)
            {
                if (epoch == _folderEpoch) Status("读取历史失败：" + ex.Message);
                return;
            }
            if (epoch != _folderEpoch) return;

            FolderList.ItemTemplate = (DataTemplate)FindResource(
                project is null ? "ProjectEntryTemplate" : "HistoryItemTemplate");
            FolderList.ItemsSource = items;

            object? hit = select switch
            {
                string cwd => items.OfType<ProjectEntry>().FirstOrDefault(p => SamePath(p.Cwd, cwd)),
                HistoryItem h => items.OfType<HistoryItem>().FirstOrDefault(i =>
                    i.Record.Cli == h.Record.Cli && i.Record.Id == h.Record.Id),
                _ => null,
            };
            if (hit != null) { FolderList.SelectedItem = hit; FolderList.ScrollIntoView(hit); }

            FolderUpBtn.IsEnabled = project != null;
            FolderPathLabel.Text = project is null ? "全部项目" : FolderName(project);
            FolderPathLabel.ToolTip = project;

            FolderEmptyLabel.Text = (project, q.Length) switch
            {
                (null, 0) => "没有历史记录",
                (_, 0)    => "这个项目下没有对话",
                _         => $"没有名称包含 “{q}” 的项",
            };
            FolderEmptyLabel.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static bool SamePath(string? a, string? b) =>
            HistoryPaths.CwdKey(a) == HistoryPaths.CwdKey(b);

        /// <summary>Switches level. A filter typed on one level does not carry to the other.</summary>
        private async void EnterProject(string? project, object? select = null)
        {
            _folderProject = project;
            _folderEpoch++;   // drop a listing still in flight for the old level
            if (SearchBoxInput.Text.Length > 0) SearchBoxInput.Text = "";
            await ReloadFolderAsync(select);
            FolderList.Focus();
        }

        private void FolderUp_Click(object sender, RoutedEventArgs e) => FolderUp();

        private void FolderUp()
        {
            if (_folderProject is { } from) EnterProject(null, select: from);
        }

        private void FolderList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Same guards as the session list: not below the last row, not on a row button.
            if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) == null) return;
            if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) != null) return;
            OpenFolderRow(FolderList.SelectedItem);
        }

        private void FolderList_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter: OpenFolderRow(FolderList.SelectedItem); break;
                case Key.Back:  FolderUp(); break;
                case Key.F2 when FolderList.SelectedItem is HistoryItem it: RenameSession(it); break;
                default: return;
            }
            e.Handled = true;
        }

        /// <summary>A project is entered; a conversation is resumed, same as in 最近.</summary>
        private void OpenFolderRow(object? row)
        {
            switch (row)
            {
                case ProjectEntry p: EnterProject(p.Cwd); break;
                case HistoryItem it: Resume(it); break;
            }
        }

        private static ProjectEntry? ProjectOf(object sender) =>
            (sender as FrameworkElement)?.DataContext as ProjectEntry;

        private void ProjectOpen_Click(object sender, RoutedEventArgs e)
        {
            if (ProjectOf(sender) is { } p) EnterProject(p.Cwd);
        }

        private void ProjectTerminal_Click(object sender, RoutedEventArgs e)
        {
            if (ProjectOf(sender) is not { } p) return;
            if (!Directory.Exists(p.Cwd)) { Status($"目录已不存在：{p.Cwd}"); return; }
            WorkingDirBox.Text = p.Cwd;
            _ = OpenTerminalAsync(p.Cwd, null);
        }

        private void ProjectReveal_Click(object sender, RoutedEventArgs e)
        {
            if (ProjectOf(sender) is not { } p) return;
            if (!Directory.Exists(p.Cwd)) { Status($"目录已不存在：{p.Cwd}"); return; }
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{p.Cwd}\"") { UseShellExecute = true }); }
            catch (Exception ex) { Status("打不开资源管理器：" + ex.Message); }
        }

        // ══════════════════════════════════════════════════════
        // Rail actions
        // ══════════════════════════════════════════════════════

        private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
        {
            // The toggle lives in the title bar row, i.e. earlier in the XAML than the
            // sidebar it controls. IsChecked="True" therefore fires this handler mid-parse,
            // before those fields are assigned — ignore that one call.
            if (SidebarPanel is null) return;

            bool show = HistoryToggle.IsChecked == true;
            if (!show) _sidebarWidth = SidebarCol.ActualWidth;   // remember it for the way back
            SidebarPanel.Visibility     = show ? Visibility.Visible : Visibility.Collapsed;
            SidebarSplitter.Visibility  = show ? Visibility.Visible : Visibility.Collapsed;
            SidebarCol.Width            = show ? new GridLength(_sidebarWidth) : new GridLength(0);
        }

        /// <summary>
        /// Flips the whole app, terminal included. The WPF side is just a theme dictionary
        /// swap; the terminal lives in a WebView2 that knows nothing about WPF resources,
        /// so its palette has to be pushed across explicitly.
        /// </summary>
        private void ToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            bool dark = !App.IsDark;
            App.ApplyTheme(dark);
            ThemeGlyph.Symbol = dark
                ? Wpf.Ui.Controls.SymbolRegular.WeatherMoon24
                : Wpf.Ui.Controls.SymbolRegular.WeatherSunny24;

            foreach (var t in _tabs) t.View.ApplyTheme(dark);
            Status(dark ? "已切换到深色" : "已切换到浅色");
        }

        // ══════════════════════════════════════════════════════
        // Small helpers
        // ══════════════════════════════════════════════════════

        private void Status(string text) => StatusText.Text = text;

        private static string FolderName(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "(未知目录)";
            string p = path.TrimEnd('\\', '/');
            string name = Path.GetFileName(p);
            return name.Length == 0 ? p : name;   // a drive root has no file name
        }

        private static string LoadLastDir()
        {
            try
            {
                if (File.Exists(LastDirFile))
                {
                    string d = File.ReadAllText(LastDirFile).Trim();
                    if (Directory.Exists(d)) return d;
                }
            }
            catch (IOException) { }
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        private static void SaveLastDir(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(LastDirFile)!);
                File.WriteAllText(LastDirFile, dir);
            }
            catch (IOException) { }
        }

        /// <summary>Minimal ICommand so keyboard shortcuts can call a plain method.</summary>
        private sealed class SimpleCommand : ICommand
        {
            private readonly Action _run;
            public SimpleCommand(Action run) => _run = run;
            public event EventHandler? CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) => _run();
        }
    }
}
