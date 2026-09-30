using ConCli.Terminal;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace ConCli
{
    public partial class App : Application
    {
        /// <summary>
        /// The accent, pinned instead of inherited. WPF-UI defaults to the Windows
        /// personalisation accent, which means the app is a different colour on every
        /// machine and can land somewhere that fights the terminal palette in
        /// TerminalTheme.cs. This is the WinUI default blue; it is the one line to
        /// change to re-brand the app.
        /// </summary>
        public static readonly Color Accent = Color.FromRgb(0x00, 0x78, 0xD4);

        /// <summary>
        /// Which theme the app is in. Tracked here rather than read back from
        /// <c>ApplicationThemeManager.GetAppTheme()</c>, which reports Unknown until the
        /// first Apply — the startup theme comes from the dictionary merged in App.xaml,
        /// so without this the first click on 主题 would appear to do nothing.
        /// </summary>
        public static bool IsDark { get; private set; } = true;

        /// <summary>
        /// Switches the app between dark and light. The accent has to be applied
        /// *after* the theme: ApplicationThemeManager swaps the whole theme dictionary
        /// and takes the accent resources with it.
        /// </summary>
        public static void ApplyTheme(bool dark)
        {
            var theme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
            ApplicationThemeManager.Apply(
                theme, Wpf.Ui.Controls.WindowBackdropType.Mica, updateAccent: false);
            ApplicationAccentColorManager.Apply(Accent, theme);

            IsDark = dark;
            // The terminal is a WebView2 and cannot see WPF resources; set this before the
            // panes repaint so ones opened later also start in the right palette.
            TerminalTheme.IsDark = dark;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            // App.xaml already merges the dark dictionary, so only the accent needs
            // overriding here — and no window exists yet to apply a backdrop to.
            ApplicationAccentColorManager.Apply(Accent, ApplicationTheme.Dark);
        }
    }
}
