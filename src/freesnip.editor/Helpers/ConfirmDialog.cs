using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using freesnip.foundation.core;

namespace freesnip.editor.helpers
{
    public static class ConfirmDialog
    {
        private static readonly Cursor HandCursor = new Cursor(StandardCursorType.Hand);

        public static async Task ShowAlertAsync(Window owner, string title, string message, string dismissText, bool destructive)
        {
            if (owner == null) return;

            var dismissButton = BuildButton(owner, dismissText,
                destructive ? "SnapVoxDestructiveBrush" : "SnapVoxAccentBrush",
                Brushes.Firebrick);
            dismissButton.IsCancel = true;
            dismissButton.IsDefault = true;

            var dialog = new Window
            {
                Title = title,
                SizeToContent = SizeToContent.WidthAndHeight,
                MinWidth = 360,
                CanResize = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                FlowDirection = UiLayoutDirection.Current,
                Background = Lookup<IBrush>(owner, "SnapVoxPanelDarkBrush", Brushes.Black),
                Content = new StackPanel
                {
                    Spacing = 18,
                    Margin = new Thickness(24),
                    MaxWidth = 480,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = title,
                            FontWeight = FontWeight.Bold,
                            FontSize = 18,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = Lookup<IBrush>(owner, "SnapVoxPrimaryTextBrush", Brushes.White)
                        },
                        new TextBlock
                        {
                            Text = message,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = Lookup<IBrush>(owner, "SnapVoxSecondaryTextBrush", Brushes.LightGray)
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 10,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Children = { dismissButton }
                        }
                    }
                }
            };

            AutomationProperties.SetName(dialog, title);
            dismissButton.Click += (s, e) => dialog.Close();
            dialog.Opened += (s, e) => dismissButton.Focus();

            await dialog.ShowDialog(owner);
        }

        public static async Task<bool> ShowAsync(Window owner, string title, string message, string confirmText, string cancelText, bool destructive)
        {
            if (owner == null) return false;

            var confirmButton = BuildButton(owner, confirmText,
                destructive ? "SnapVoxDestructiveBrush" : "SnapVoxAccentBrush",
                Brushes.Firebrick);
            var cancelButton = BuildButton(owner, cancelText, "SnapVoxSecondaryButtonBrush", Brushes.DimGray);
            cancelButton.IsCancel = true;

            var dialog = new Window
            {
                Title = title,
                SizeToContent = SizeToContent.WidthAndHeight,
                MinWidth = 360,
                CanResize = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                FlowDirection = UiLayoutDirection.Current,
                Background = Lookup<IBrush>(owner, "SnapVoxPanelDarkBrush", Brushes.Black),
                Content = new StackPanel
                {
                    Spacing = 18,
                    Margin = new Thickness(24),
                    MaxWidth = 480,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = title,
                            FontWeight = FontWeight.Bold,
                            FontSize = 18,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = Lookup<IBrush>(owner, "SnapVoxPrimaryTextBrush", Brushes.White)
                        },
                        new TextBlock
                        {
                            Text = message,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = Lookup<IBrush>(owner, "SnapVoxSecondaryTextBrush", Brushes.LightGray)
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 10,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Children = { cancelButton, confirmButton }
                        }
                    }
                }
            };

            AutomationProperties.SetName(dialog, title);

            bool confirmed = false;
            confirmButton.Click += (s, e) => { confirmed = true; dialog.Close(); };
            cancelButton.Click += (s, e) => dialog.Close();
            dialog.Opened += (s, e) => cancelButton.Focus();

            await dialog.ShowDialog(owner);
            return confirmed;
        }

        private static Button BuildButton(Window owner, string text, string brushKey, IBrush fallback)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 100,
                MinHeight = 32,
                Cursor = HandCursor,
                Padding = new Thickness(12, 6),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Background = Lookup<IBrush>(owner, brushKey, fallback),
                Foreground = Lookup<IBrush>(owner, "FreeSnipPrimaryTextBrush", Lookup<IBrush>(owner, "SnapVoxPrimaryTextBrush", Brushes.White))
            };
            AutomationProperties.SetName(button, text);
            return button;
        }

        private static T Lookup<T>(Window owner, string key, T fallback) where T : class
        {
            return owner.TryFindResource(key, out object value) && value is T typed ? typed : fallback;
        }
    }
}
