using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ConCli.Terminal
{
    /// <summary>
    /// A terminal pane: xterm.js inside WebView2 for rendering, ConPTY for the real shell.
    /// The VT stream is never parsed on our side — xterm.js owns that.
    /// </summary>
    public sealed class TerminalView : UserControl, IDisposable
    {
        private const string VirtualHost = "concli-term.invalid";

        private readonly WebView2 _web = new();
        private ConPty? _pty;

        // ConPTY fires on a background thread and can fire very often (spinners emit
        // a few bytes at a time). Coalesce into one web message per frame instead of
        // paying IPC per chunk.
        private readonly StringBuilder _pending = new();
        private readonly object _pendingLock = new();
        private readonly DispatcherTimer _flush = new() { Interval = TimeSpan.FromMilliseconds(16) };

        private bool _ready;
        private bool _disposed;

        public string? WorkingDirectory { get; init; }
        public string? TeePath { get; init; }
        public string? CommandLine { get; init; }

        /// <summary>
        /// Per-profile environment for the shell. Not called "Environment": that would shadow
        /// System.Environment for the whole class.
        /// </summary>
        public IReadOnlyDictionary<string, string?>? ShellEnvironment { get; init; }

        /// <summary>Raised when the shell reports a new title via OSC 0/2.</summary>
        public event Action<string>? TitleChanged;

        public event Action<int>? ProcessExited;

        public TerminalView()
        {
            Content = _web;
            Background = System.Windows.Media.Brushes.Transparent;
            // Painted before the page loads, so it must match index.html's body colour —
            // otherwise every new tab flashes a rectangle of the wrong shade.
            _web.DefaultBackgroundColor = System.Drawing.ColorTranslator.FromHtml(
                TerminalTheme.Background(TerminalTheme.IsDark));
            _flush.Tick += (_, _) => FlushPending();
        }

        public async Task InitializeAsync()
        {
            string userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ConCli", "WebView2");
            Directory.CreateDirectory(userData);

            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            var s = core.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDevToolsEnabled = true;                 // keep: the only way to debug xterm
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;  // Ctrl+R etc. belong to the shell

            string assets = Path.Combine(AppContext.BaseDirectory, "Terminal", "xterm");
            if (!File.Exists(Path.Combine(assets, "index.html")))
                throw new FileNotFoundException("xterm assets missing from output directory", assets);

            core.SetVirtualHostNameToFolderMapping(
                VirtualHost, assets, CoreWebView2HostResourceAccessKind.Allow);

            core.WebMessageReceived += OnWebMessage;

            var loaded = new TaskCompletionSource();
            void OnNav(object? _, CoreWebView2NavigationCompletedEventArgs e)
            {
                core.NavigationCompleted -= OnNav;
                if (e.IsSuccess) loaded.TrySetResult();
                else loaded.TrySetException(
                    new InvalidOperationException($"terminal page failed to load: {e.WebErrorStatus}"));
            }
            core.NavigationCompleted += OnNav;
            core.Navigate($"https://{VirtualHost}/index.html");
            await loaded.Task;

            // The page is hardcoded to one palette, so a pane opened while the app is in
            // the other theme has to be repainted before it is shown.
            if (TerminalTheme.IsDark != TerminalTheme.PageDefaultIsDark)
                ApplyTheme(TerminalTheme.IsDark);
        }

        /// <summary>
        /// Repaints the terminal to match the app theme. Safe to call before the page is up —
        /// it is a no-op then, and <see cref="InitializeAsync"/> applies the current theme.
        /// </summary>
        public void ApplyTheme(bool dark)
        {
            var core = _web.CoreWebView2;
            if (core == null) return;
            _ = core.ExecuteScriptAsync($"ptyHost.setTheme({TerminalTheme.Json(dark)})");
        }

        private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            JsonElement msg;
            try { msg = JsonDocument.Parse(e.WebMessageAsJson).RootElement; }
            catch (JsonException) { return; }

            if (!msg.TryGetProperty("type", out var t)) return;

            switch (t.GetString())
            {
                case "ready":
                    if (_ready) break;
                    _ready = true;
                    StartPty(ShortOf(msg, "cols", 120), ShortOf(msg, "rows", 30));
                    break;

                case "input":
                    if (msg.TryGetProperty("data", out var d))
                        _pty?.Write(d.GetString() ?? "");
                    break;

                case "resize":
                    _pty?.Resize(ShortOf(msg, "cols", 120), ShortOf(msg, "rows", 30));
                    break;

                case "copy":
                    if (msg.TryGetProperty("data", out var sel) && sel.GetString() is { Length: > 0 } text)
                    {
                        try { Clipboard.SetText(text); }
                        catch (System.Runtime.InteropServices.COMException) { }   // clipboard held by another app
                    }
                    break;

                case "paste":
                    Paste();
                    break;

                case "title":
                    if (msg.TryGetProperty("data", out var ti))
                        TitleChanged?.Invoke(ti.GetString() ?? "");
                    break;
            }
        }

        /// <summary>
        /// Text goes through xterm (for bracketed paste). An image cannot travel as text, so
        /// the CLI is told to fetch it itself: Alt+V is Claude Code's image-paste key on
        /// Windows, where Ctrl+V belongs to the terminal. Files copied in Explorer paste as
        /// quoted paths, which Claude Code also attaches when they are images.
        /// </summary>
        private void Paste()
        {
            string clip = "";
            try
            {
                if (Clipboard.ContainsText())
                    clip = Clipboard.GetText();
                else if (Clipboard.ContainsImage())
                {
                    _pty?.Write("\u001bv");
                    return;
                }
                else if (Clipboard.ContainsFileDropList())
                {
                    var paths = new List<string>();
                    foreach (string? p in Clipboard.GetFileDropList())
                        if (!string.IsNullOrEmpty(p)) paths.Add(p.Contains(' ') ? $"\"{p}\"" : p);
                    clip = string.Join(' ', paths);
                }
            }
            catch (System.Runtime.InteropServices.COMException) { }   // clipboard held by another app

            if (clip.Length > 0)
                _ = _web.CoreWebView2?.ExecuteScriptAsync(
                    $"ptyHost.paste({JsonSerializer.Serialize(clip)})");
        }

        private static short ShortOf(JsonElement o, string name, short fallback) =>
            o.TryGetProperty(name, out var v) && v.TryGetInt32(out int i) && i is > 0 and < short.MaxValue
                ? (short)i : fallback;

        private void StartPty(short cols, short rows)
        {
            _pty = new ConPty(new ConPtyOptions
            {
                CommandLine      = CommandLine ?? Shell.DefaultCommandLine(),
                WorkingDirectory = WorkingDirectory,
                Cols             = cols,
                Rows             = rows,
                TeePath          = TeePath,
                Environment      = ShellEnvironment,
            });

            _pty.Output += chunk =>
            {
                lock (_pendingLock) _pending.Append(chunk);
            };

            _pty.Exited += code => Dispatcher.BeginInvoke(() =>
            {
                FlushPending();
                _flush.Stop();
                ProcessExited?.Invoke(code);
            });

            _pty.Start();
            _flush.Start();
        }

        private void FlushPending()
        {
            string chunk;
            lock (_pendingLock)
            {
                if (_pending.Length == 0) return;
                chunk = _pending.ToString();
                _pending.Clear();
            }
            try { _web.CoreWebView2?.PostWebMessageAsString(chunk); }
            catch (InvalidOperationException) { }   // webview torn down mid-flush
        }

        public void FocusTerminal() => _ = _web.CoreWebView2?.ExecuteScriptAsync("ptyHost.focus()");

        /// <summary>Types text into the shell, exactly as if the user had typed it.</summary>
        public void SendText(string text) => _pty?.Write(text);

        /// <summary>Screen + scrollback as VT-resolved plain text (for copy-all / diagnostics).</summary>
        public async Task<string> DumpAsync()
        {
            var core = _web.CoreWebView2;
            if (core == null) return "";
            string json = await core.ExecuteScriptAsync("ptyHost.dump()");
            return JsonSerializer.Deserialize<string>(json) ?? "";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _flush.Stop();
            _pty?.Dispose();
            _web.Dispose();
        }
    }
}
