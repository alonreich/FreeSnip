using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System;
using System.Threading.Tasks;

namespace freesnip.forms
{
    public partial class DeploymentProgressWindow : Window
    {
        private ProgressBar _progressBar;
        private TextBlock _phaseText;
        private TextBlock _percentageText;
        private TextBlock _titleText;
        private TextBlock _logPathText;
        private ListBox _logList;
        private Button _finishButton;
        private Button _cancelButton;
        private Border _errorBanner;
        private TextBlock _errorText;
        private readonly TaskCompletionSource<bool> _acknowledged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Acknowledged => _acknowledged.Task;

        public DeploymentProgressWindow()
        {
            InitializeComponent();
        }

        public DeploymentProgressWindow(string title) : this(title, null)
        {
        }

        public DeploymentProgressWindow(string title, string logPath) : this()
        {
            if (_titleText != null)
            {
                _titleText.Text = string.IsNullOrWhiteSpace(title) ? "Installing SnapVox" : title;
            }

            if (_logPathText != null)
            {
                _logPathText.Text = string.IsNullOrWhiteSpace(logPath) ? string.Empty : "Log: " + logPath;
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
            freesnip.foundation.core.UiLayoutDirection.Apply(this);
            _progressBar = this.FindControl<ProgressBar>("ProgressBar");
            _phaseText = this.FindControl<TextBlock>("PhaseText");
            _percentageText = this.FindControl<TextBlock>("PercentageText");
            _titleText = this.FindControl<TextBlock>("TitleText");
            _logPathText = this.FindControl<TextBlock>("LogPathText");
            _logList = this.FindControl<ListBox>("LogList");
            _finishButton = this.FindControl<Button>("FinishButton");
            _cancelButton = this.FindControl<Button>("CancelButton");
            _errorBanner = this.FindControl<Border>("ErrorBanner");
            _errorText = this.FindControl<TextBlock>("ErrorText");
            KeyDown += (s, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Escape)
                {
                    if (_finishButton != null && _finishButton.IsVisible && _finishButton.IsEnabled)
                    {
                        Close();
                    }
                    else if (_cancelButton != null && _cancelButton.IsVisible && _cancelButton.IsEnabled)
                    {
                        OnCancelClick(this, null);
                    }
                }
            };
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            Activate();
            Topmost = true;
        }

        protected override void OnClosed(EventArgs e)
        {
            _acknowledged.TrySetResult(true);
            base.OnClosed(e);
        }

        public void EnableFinish(string finalStatus)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!string.IsNullOrWhiteSpace(finalStatus) && _phaseText != null) _phaseText.Text = finalStatus;
                if (_progressBar != null) _progressBar.IsVisible = false;
                if (_cancelButton != null) _cancelButton.IsVisible = false;
                if (_finishButton != null)
                {
                    _finishButton.IsVisible = true;
                    _finishButton.IsEnabled = true;
                    _finishButton.Focus();
                }

                Topmost = true;
                Activate();
            });
        }

        private void OnCancelClick(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            UpdateStatus("Installation cancelled by user.");
            ShowError("Installation was cancelled. You can safely close this window and retry.");
        }

        public void ShowError(string message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_errorText != null && !string.IsNullOrWhiteSpace(message))
                {
                    _errorText.Text = message;
                }

                if (_errorBanner != null)
                {
                    _errorBanner.IsVisible = true;
                }

                if (_progressBar != null)
                {
                    _progressBar.IsVisible = false;
                }

                if (_cancelButton != null)
                {
                    _cancelButton.IsVisible = false;
                }

                if (_finishButton != null)
                {
                    _finishButton.Content = "Close";
                    _finishButton.IsVisible = true;
                    _finishButton.IsEnabled = true;
                    _finishButton.Focus();
                }
            });
        }

        public void UpdateProgress(int value)
        {
            int clamped = Math.Clamp(value, 0, 100);
            Dispatcher.UIThread.Post(() =>
            {
                if (_progressBar != null)
                {
                    _progressBar.Value = clamped;
                    if (clamped >= 100)
                    {
                        _progressBar.IsVisible = false;
                        if (_finishButton != null) _finishButton.IsVisible = true;
                    }
                }

                if (_percentageText != null)
                {
                    _percentageText.Text = clamped + "%";
                }
            });
        }

        private void OnFinishClick(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            Close();
        }

        public void UpdateStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_phaseText != null)
                {
                    _phaseText.Text = status;
                }

                AppendLogLine(status);
                Activate();
            });
        }

        public void AppendLogLine(string message)
        {
            if (_logList == null || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
            _logList.Items.Add(line);
            if (_logList.ItemCount > 0)
            {
                _logList.SelectedIndex = _logList.ItemCount - 1;
                _logList.ScrollIntoView(_logList.ItemCount - 1);
            }
        }
    }
}
