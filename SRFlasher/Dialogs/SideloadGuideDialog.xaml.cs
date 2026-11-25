using System.Windows;

namespace SRFlasher.Dialogs
{
    /// <summary>
    /// Non-modal guide shown while the app waits for the user to put the
    /// phone into sideload mode from the recovery menu ("Apply update" →
    /// "Apply update via ADB"). The ViewModel drives the status line and
    /// closes the window when the transfer starts, the wait times out, or
    /// the user cancels.
    /// </summary>
    public partial class SideloadGuideDialog : Window
    {
        /// <summary>Set by the cancel button; the ViewModel polls it.</summary>
        public volatile bool Cancelled;

        private SideloadGuideDialog()
        {
            InitializeComponent();
        }

        /// <summary>Show the guide without blocking the calling flow.</summary>
        public static SideloadGuideDialog Show(Window owner)
        {
            var dialog = new SideloadGuideDialog();
            if (owner != null && owner.IsLoaded)
                dialog.Owner = owner;
            else
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Show();
            return dialog;
        }

        /// <summary>Update the live status line (called from the UI thread).</summary>
        public void SetStatus(string text)
        {
            StatusText.Text = text;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Cancelled = true;
            Close();
        }
    }
}
