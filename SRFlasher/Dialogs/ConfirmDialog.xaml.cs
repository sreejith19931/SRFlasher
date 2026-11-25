using System.Windows;
using System.Windows.Input;

namespace SRFlasher.Dialogs
{
    /// <summary>
    /// Themed replacement for MessageBox.Show(..., YesNo, Warning): follows the
    /// active theme, is owned by (and centered on) the calling window, and
    /// defaults to the safe "No" button.
    /// </summary>
    public partial class ConfirmDialog : Window
    {
        private ConfirmDialog(string title, string message, string confirmText, string cancelText, bool danger)
        {
            InitializeComponent();

            TitleText.Text = title;
            Title = title;
            // Message with a light inline dialect: a line starting with
            // "!!" is rendered bold and larger (critical instructions).
            var secondaryBrush = (System.Windows.Media.Brush)FindResource("TextSecondary");
            var primaryBrush = (System.Windows.Media.Brush)FindResource("TextPrimary");
            MessageText.Inlines.Clear();
            foreach (var line in message.Split('\n'))
            {
                if (MessageText.Inlines.Count > 0)
                    MessageText.Inlines.Add(new System.Windows.Documents.LineBreak());
                if (line.Length == 0)
                {
                    // Blank lines become a thin spacer — a full-height empty
                    // run made dialogs read as airy as web pages.
                    MessageText.Inlines.Add(new System.Windows.Documents.Run(" ")
                    {
                        FontSize = 6,
                        Foreground = secondaryBrush
                    });
                }
                else if (line.StartsWith("!!"))
                {
                    MessageText.Inlines.Add(new System.Windows.Documents.Run(line.Substring(2))
                    {
                        FontWeight = FontWeights.Bold,
                        FontSize = 15,
                        Foreground = primaryBrush
                    });
                }
                else
                {
                    MessageText.Inlines.Add(new System.Windows.Documents.Run(line) { Foreground = secondaryBrush });
                }
            }
            ConfirmButton.Content = confirmText;
            CancelButton.Content = cancelText;
            ConfirmButton.Style = (Style)FindResource(danger ? "DialogDangerButton" : "DialogButton");
            if (string.IsNullOrEmpty(cancelText))
            {
                // OK-only popup: hide Cancel, Enter and Esc both confirm.
                CancelButton.Visibility = Visibility.Collapsed;
                ConfirmButton.IsDefault = true;
                ConfirmButton.IsCancel = true;
            }

            // Safe default: focus sits on the non-destructive action, so Enter
            // confirms only after the user deliberately moves to that button.
            // OK-only popups focus the single button instead.
            Loaded += (s, e) => (string.IsNullOrEmpty(cancelText) ? ConfirmButton : CancelButton).Focus();
        }

        /// <summary>
        /// Show a modal confirmation and return true only when the user confirms.
        /// Closing the window any other way counts as cancel.
        /// </summary>
        public static bool Show(Window owner, string title, string message,
            string confirmText = "Yes", string cancelText = "No", bool danger = true)
        {
            var dialog = new ConfirmDialog(title, message, confirmText, cancelText, danger);
            if (owner != null && owner.IsLoaded)
                dialog.Owner = owner;
            else
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            return dialog.ShowDialog() == true;
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }
    }
}
