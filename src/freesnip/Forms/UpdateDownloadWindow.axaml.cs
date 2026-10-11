using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace freesnip.forms;

public enum UpdateReadyAction
{
    RestartNow,
    ContinueWorking
}

public partial class UpdateDownloadWindow : Window
{
    public event Action CancelRequested;
    private readonly TaskCompletionSource<UpdateReadyAction> _decisionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public UpdateDownloadWindow()
    {
        InitializeComponent();
        freesnip.foundation.core.UiLayoutDirection.Apply(this);
        var cancelBtn = this.FindControl<Button>("CancelBtn");
        if (cancelBtn != null)
        {
            cancelBtn.Click += (s, e) => CancelRequested?.Invoke();
        }

        var restartBtn = this.FindControl<Button>("RestartNowBtn");
        if (restartBtn != null)
        {
            restartBtn.Click += (s, e) => _decisionTcs.TrySetResult(UpdateReadyAction.RestartNow);
        }

        var continueBtn = this.FindControl<Button>("ContinueWorkingBtn");
        if (continueBtn != null)
        {
            continueBtn.Click += (s, e) => _decisionTcs.TrySetResult(UpdateReadyAction.ContinueWorking);
        }

        Closing += (s, e) =>
        {
            _decisionTcs.TrySetResult(UpdateReadyAction.ContinueWorking);
        };
    }

    public void SetProgress(double fraction, string status)
    {
        var bar = this.FindControl<ProgressBar>("Progress");
        var text = this.FindControl<TextBlock>("StatusText");
        if (bar != null) bar.Value = Math.Clamp(fraction, 0, 1) * 100;
        if (text != null) text.Text = status;
    }

    public Task<UpdateReadyAction> PromptReadyActionAsync()
    {
        var headline = this.FindControl<TextBlock>("HeadlineText");
        var status = this.FindControl<TextBlock>("StatusText");
        var bar = this.FindControl<ProgressBar>("Progress");
        var downloadingPanel = this.FindControl<StackPanel>("DownloadingButtonsPanel");
        var readyPanel = this.FindControl<StackPanel>("ReadyButtonsPanel");
        var restartBtn = this.FindControl<Button>("RestartNowBtn");

        if (headline != null) headline.Text = "Update Download Complete!";
        if (status != null) status.Text = "The update is verified and ready to be installed. Please choose an option:";
        if (bar != null) bar.Value = 100;
        if (downloadingPanel != null) downloadingPanel.IsVisible = false;
        if (readyPanel != null) readyPanel.IsVisible = true;
        restartBtn?.Focus();

        return _decisionTcs.Task;
    }

    public void MarkHandoffToInstaller()
    {
        var headline = this.FindControl<TextBlock>("HeadlineText");
        var status = this.FindControl<TextBlock>("StatusText");
        var bar = this.FindControl<ProgressBar>("Progress");
        var downloadingPanel = this.FindControl<StackPanel>("DownloadingButtonsPanel");
        var readyPanel = this.FindControl<StackPanel>("ReadyButtonsPanel");
        if (headline != null) headline.Text = "Starting installer…";
        if (status != null) status.Text = "FreeSnip is closing to apply the update. Your settings are preserved automatically.";
        if (bar != null) bar.Value = 100;
        if (downloadingPanel != null) downloadingPanel.IsVisible = false;
        if (readyPanel != null) readyPanel.IsEnabled = false;
    }

    private void InitializeComponent() { AvaloniaXamlLoader.Load(this); }
}
