namespace ConCli.Terminal
{
    /// <summary>
    /// The terminal's colours, in one place.
    ///
    /// The terminal is the one part of the UI WPF does not paint — it is xterm.js inside a
    /// WebView2, which cannot see WPF theme resources. So the palette lives here and gets
    /// pushed across as JSON, instead of being duplicated in boot.js, index.html and the
    /// WebView2 background. Those three used to drift apart every time the theme changed.
    ///
    /// These are Windows Terminal's own built-in schemes, value for value: Campbell (what
    /// PowerShell opens in by default) for dark, One Half Light for light. The pane is meant
    /// to be indistinguishable from PowerShell in Windows Terminal, so do not "improve" them.
    /// </summary>
    internal static class TerminalTheme
    {
        /// <summary>What index.html is hardcoded to load with.</summary>
        public const bool PageDefaultIsDark = true;

        /// <summary>
        /// The theme the app is currently in. Set once when the user flips the theme;
        /// panes created afterwards read it so they do not load in the old palette.
        /// </summary>
        public static bool IsDark { get; set; } = PageDefaultIsDark;

        public const string DarkBackground  = "#0C0C0C";
        public const string LightBackground = "#FAFAFA";

        public static string Background(bool dark) => dark ? DarkBackground : LightBackground;

        /// <summary>An xterm.js ITheme literal, ready to drop into a script call.</summary>
        public static string Json(bool dark) => dark ? Dark : Light;

        // Campbell
        private const string Dark = """
            {
              "background": "#0C0C0C", "foreground": "#CCCCCC",
              "cursor": "#FFFFFF", "cursorAccent": "#0C0C0C",
              "selectionBackground": "#FFFFFF66",
              "black": "#0C0C0C", "brightBlack": "#767676",
              "red": "#C50F1F", "brightRed": "#E74856",
              "green": "#13A10E", "brightGreen": "#16C60C",
              "yellow": "#C19C00", "brightYellow": "#F9F1A5",
              "blue": "#0037DA", "brightBlue": "#3B78FF",
              "magenta": "#881798", "brightMagenta": "#B4009E",
              "cyan": "#3A96DD", "brightCyan": "#61D6D6",
              "white": "#CCCCCC", "brightWhite": "#F2F2F2"
            }
            """;

        // One Half Light
        private const string Light = """
            {
              "background": "#FAFAFA", "foreground": "#383A42",
              "cursor": "#4F525D", "cursorAccent": "#FAFAFA",
              "selectionBackground": "#4F525D40",
              "black": "#383A42", "brightBlack": "#4F525D",
              "red": "#E45649", "brightRed": "#DF6C75",
              "green": "#50A14F", "brightGreen": "#98C379",
              "yellow": "#C18301", "brightYellow": "#E4C07A",
              "blue": "#0184BC", "brightBlue": "#61AFEF",
              "magenta": "#A626A4", "brightMagenta": "#C577DD",
              "cyan": "#0997B3", "brightCyan": "#56B5C1",
              "white": "#FAFAFA", "brightWhite": "#FFFFFF"
            }
            """;
    }
}
