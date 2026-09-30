using System.Windows;

namespace ClaudeCodeGUI
{
    public enum Choice { Cancel, Primary, Secondary }

    /// <summary>
    /// A three-button modal. Hand-rolled rather than using <c>Wpf.Ui.Controls.MessageBox</c>:
    /// that control's <c>ShowDialogAsync</c> was verified here to never put an HWND on screen —
    /// in a minimal WPF-UI app it simply blocks, and inside this app it returned
    /// <c>MessageBoxResult.None</c> without rendering. A dialog that silently answers "cancel"
    /// is worse than no dialog, since it turns a deliberate prompt back into the silent no-op
    /// it was meant to replace. This is a plain <c>ShowDialog()</c>, so it either shows or throws.
    /// </summary>
    public partial class ChoiceDialog : Wpf.Ui.Controls.FluentWindow
    {
        public Choice Choice { get; private set; } = Choice.Cancel;

        /// <param name="secondary">
        /// The destructive choice. It is rendered in the far-right slot, away from the
        /// default button, so a stray click cannot land on it.
        /// </param>
        public ChoiceDialog(Window owner, string title, string message,
                            string primary, string secondary, string cancel = "取消")
        {
            InitializeComponent();
            Owner            = owner;
            Title            = title;
            MessageText.Text = message;
            PrimaryBtn.Content   = primary;
            SecondaryBtn.Content = secondary;
            CancelBtn.Content    = cancel;
        }

        /// <summary>Shows the dialog and returns what was picked. Closing it counts as cancel.</summary>
        public static Choice Ask(Window owner, string title, string message,
                                 string primary, string secondary, string cancel = "取消")
        {
            var d = new ChoiceDialog(owner, title, message, primary, secondary, cancel);
            d.ShowDialog();
            return d.Choice;
        }

        private void Primary_Click(object sender, RoutedEventArgs e)   => Finish(Choice.Primary);
        private void Secondary_Click(object sender, RoutedEventArgs e) => Finish(Choice.Secondary);
        private void Cancel_Click(object sender, RoutedEventArgs e)    => Finish(Choice.Cancel);

        private void Finish(Choice c)
        {
            Choice = c;
            DialogResult = c != Choice.Cancel;   // also closes the window
        }
    }
}
