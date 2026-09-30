using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ConCli.Terminal
{
    /// <summary>
    /// One terminal session in its own window. P4 folds this into a tab host;
    /// the TerminalView inside is what actually gets reused.
    /// </summary>
    public sealed class TerminalWindow : Window
    {
        private readonly TerminalView _view;

        /// <summary>The pane itself — P4's tab host reparents this rather than the window.</summary>
        public TerminalView View => _view;

        public TerminalWindow(string? workingDirectory, string? teePath = null)
        {
            string cwd = Directory.Exists(workingDirectory)
                ? workingDirectory!
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            Title      = cwd;
            Width      = 1000;
            Height     = 640;
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x13));
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            _view = new TerminalView
            {
                WorkingDirectory = cwd,
                TeePath          = teePath,
            };
            _view.TitleChanged += t => Dispatcher.BeginInvoke(() => Title = Compose(t, cwd));
            _view.ProcessExited += _ => Dispatcher.BeginInvoke(Close);

            Content = _view;

            Loaded  += async (_, _) =>
            {
                try
                {
                    await _view.InitializeAsync();
                    _view.FocusTerminal();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.ToString(), "Terminal failed to start",
                                    MessageBoxButton.OK, MessageBoxImage.Error);
                    Close();
                }
            };

            // The shell owns Ctrl+C / Ctrl+R / Ctrl+L, so app shortcuts live on Ctrl+Shift+*.
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.W &&
                    (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift))
                        == (ModifierKeys.Control | ModifierKeys.Shift))
                { Close(); e.Handled = true; }
            };

            Closed += (_, _) => _view.Dispose();
        }

        /// <summary>
        /// Shells set the OSC title to their own exe path, which is noise. Keep it only when a
        /// program (claude, vim, git) has set something meaningful.
        /// </summary>
        private static string Compose(string oscTitle, string cwd)
        {
            string t = (oscTitle ?? "").Trim();
            if (t.Length == 0) return cwd;

            bool isShellNoise =
                t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                t.Contains(Path.DirectorySeparatorChar) ||
                t.Equals(cwd, StringComparison.OrdinalIgnoreCase);

            return isShellNoise ? cwd : $"{t}  —  {cwd}";
        }
    }
}
