using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ConCli
{
    /// <summary>
    /// One-field name prompt (history rows, folder entries, 另存为). Built in code: it is a
    /// text box and two buttons, and the WPF-UI theme styles them without any XAML of its own.
    /// </summary>
    public sealed class RenameDialog : Window
    {
        private readonly TextBox _box;

        /// <summary>The entered name. What blank means is up to the caller's hint.</summary>
        public string Value => _box.Text;

        public RenameDialog(string current, string title = "重命名", string hintText = "留空则恢复原标题")
        {
            Title = title;
            Width = 420;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
            SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");

            var hint = new TextBlock
            {
                Text = hintText,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 0),
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");

            _box = new TextBox { Text = current };

            var ok = new Button { Content = "确定", IsDefault = true, MinWidth = 80 };
            ok.Click += (_, _) => DialogResult = true;
            var cancel = new Button
            {
                Content = "取消", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0),
            };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
                Children = { ok, cancel },
            };

            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children = { _box, hint, buttons },
            };

            Loaded += (_, _) =>
            {
                _box.SelectAll();
                Keyboard.Focus(_box);
            };
        }
    }
}
