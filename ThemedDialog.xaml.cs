using System;
using System.Windows;
using System.Windows.Input;
using AdvancedSnip.Services;

namespace AdvancedSnip
{
    /// <summary>
    /// A message box that follows the app's theme.
    ///
    /// <c>MessageBox</c> is drawn by Windows, so in a dark or Cyberpunk theme every
    /// confirmation and error arrived as a light-titled system box that looked like it
    /// belonged to a different application. That was the last thing in the app which could
    /// not follow the theme, and there is no setting for it — the only fix is not to use it.
    ///
    /// **The signature deliberately mirrors <c>MessageBox.Show</c>**, down to returning
    /// <see cref="MessageBoxResult"/>. Fourteen call sites needed changing, and a
    /// same-shaped replacement makes each of them a one-word edit rather than a rewrite
    /// with its own chance of inverting a condition.
    /// </summary>
    public partial class ThemedDialog : Window
    {
        private MessageBoxResult _result = MessageBoxResult.None;

        private MessageBoxResult _first, _second, _third;

        private ThemedDialog()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Drop-in for <c>MessageBox.Show</c>. Falls back to the system box if anything
        /// here fails — an app that cannot show an error message because the error dialog
        /// itself broke is worse than an unthemed one.
        /// </summary>
        public static MessageBoxResult Show(Window? owner, string text, string title,
                                            MessageBoxButton buttons = MessageBoxButton.OK,
                                            MessageBoxImage icon = MessageBoxImage.None)
        {
            try
            {
                var dialog = new ThemedDialog();

                if (owner != null && owner.IsVisible && !ReferenceEquals(owner, dialog))
                    dialog.Owner = owner;
                else
                    dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

                dialog.TitleText.Text = title;
                dialog.MessageText.Text = text;
                dialog.ApplyIcon(icon);
                dialog.ApplyButtons(buttons);

                ThemeManager.ApplyToWindow(dialog);
                dialog.ShowDialog();

                return dialog._result;
            }
            catch
            {
                return MessageBox.Show(owner, text, title, buttons, icon);
            }
        }

        /// <summary>
        /// Shows a themed input prompt dialog, returning the entered text if OK was clicked, or null if cancelled.
        /// </summary>
        public static string? Prompt(Window? owner, string prompt, string title, string defaultText = "")
        {
            try
            {
                var dialog = new ThemedDialog();
                dialog.Width = 620;

                if (owner != null && owner.IsVisible && !ReferenceEquals(owner, dialog))
                    dialog.Owner = owner;
                else
                    dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

                dialog.TitleText.Text = title;
                dialog.MessageText.Text = prompt;
                dialog.InputTextBox.Text = defaultText;
                dialog.InputTextBox.Visibility = Visibility.Visible;
                dialog.ApplyIcon(MessageBoxImage.None);
                dialog.ApplyButtons(MessageBoxButton.OKCancel);

                ThemeManager.ApplyToWindow(dialog);
                dialog.Loaded += (s, e) =>
                {
                    dialog.InputTextBox.Focus();
                    dialog.InputTextBox.SelectAll();
                };

                dialog.ShowDialog();

                return dialog._result == MessageBoxResult.OK ? dialog.InputTextBox.Text.Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The severity mark. A glyph tinted from the palette rather than the system icon
        /// set, so it changes with the theme like everything else on the dialog.
        /// </summary>
        private void ApplyIcon(MessageBoxImage icon)
        {
            (string glyph, string brush) = icon switch
            {
                MessageBoxImage.Error       => ("\u2716", "Brush.Danger"),
                MessageBoxImage.Warning     => ("\u26A0", "Brush.Warning"),
                MessageBoxImage.Question    => ("\uFF1F", "Brush.Accent"),
                MessageBoxImage.Information => ("\u2139", "Brush.Accent"),
                _                           => ("", "Brush.Accent")
            };

            IconGlyph.Text = glyph;
            IconGlyph.Visibility = glyph.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

            // SetResourceReference, not FindResource — see ledger #15. A brush resolved
            // here would be frozen at whichever theme was loaded when the dialog opened.
            IconGlyph.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                                           brush);
        }

        /// <summary>
        /// Lays the buttons out right-to-left in the markup so the default is rightmost and
        /// primary-styled, which is where Windows puts it and where the eye goes last.
        /// </summary>
        private void ApplyButtons(MessageBoxButton buttons)
        {
            switch (buttons)
            {
                case MessageBoxButton.OKCancel:
                    Set(FirstBtn, "OK", MessageBoxResult.OK, ref _first);
                    Set(SecondBtn, "Cancel", MessageBoxResult.Cancel, ref _second);
                    break;

                case MessageBoxButton.YesNo:
                    Set(FirstBtn, "Yes", MessageBoxResult.Yes, ref _first);
                    Set(SecondBtn, "No", MessageBoxResult.No, ref _second);
                    break;

                case MessageBoxButton.YesNoCancel:
                    Set(FirstBtn, "Yes", MessageBoxResult.Yes, ref _first);
                    Set(SecondBtn, "No", MessageBoxResult.No, ref _second);
                    Set(ThirdBtn, "Cancel", MessageBoxResult.Cancel, ref _third);
                    break;

                default:
                    Set(FirstBtn, "OK", MessageBoxResult.OK, ref _first);
                    break;
            }

            // Escape and the close-by-other-means path answer with the least destructive
            // option available, matching what a system box does.
            _result = buttons switch
            {
                MessageBoxButton.OK => MessageBoxResult.OK,
                MessageBoxButton.YesNo => MessageBoxResult.No,
                _ => MessageBoxResult.Cancel
            };
        }

        private static void Set(System.Windows.Controls.Button button, string label,
                                MessageBoxResult result, ref MessageBoxResult slot)
        {
            button.Content = label;
            button.Visibility = Visibility.Visible;
            slot = result;
        }

        private void Answer(MessageBoxResult result)
        {
            _result = result;
            DialogResult = true;      // closes the modal
        }

        private void First_Click(object sender, RoutedEventArgs e) => Answer(_first);

        private void Second_Click(object sender, RoutedEventArgs e) => Answer(_second);

        private void Third_Click(object sender, RoutedEventArgs e) => Answer(_third);

        private void Dialog_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Answer(_first);
            }
            else if (e.Key == Key.Escape)
            {
                // _result already holds the safe answer chosen in ApplyButtons.
                e.Handled = true;
                DialogResult = true;
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            if (InputTextBox.Visibility == Visibility.Visible)
            {
                InputTextBox.Focus();
                InputTextBox.SelectAll();
            }
            else
            {
                FirstBtn.Focus();
            }
        }
    }
}
