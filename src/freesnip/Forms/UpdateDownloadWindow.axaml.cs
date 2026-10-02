using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace freesnip.forms;

public partial class UpdateDownloadWindow : Window
{
    public event Action CancelRequested;

    public UpdateDownloadWindow()
    {
        InitializeComponent();
        var cancelBtn = this.FindControl<Button>("CancelBtn");
        if (cancelBtn != null)
        {
            cancelBtn.Click += (s, e) => CancelRequested?.Invoke();
        }
    }

    public void SetProgress(double fraction, string status)
    {
        var bar = this.FindControl<ProgressBar>("Progress");
        var text = this.FindControl<TextBlock>("StatusText");
        if (bar != null) bar.Value = Math.Clamp(fraction, 0, 1) * 100;
        if (text != null) text.Text = status;
    }

    public void MarkHandoffToInstaller()
    {
        var headline = this.FindControl<TextBlock>("HeadlineText");
        var status = this.FindControl<TextBlock>("StatusText");
        var bar = this.FindControl<ProgressBar>("Progress");
        var cancelBtn = this.FindControl<Button>("CancelBtn");
        if (headline != null) headline.Text = "Update ready — starting installer…";
        if (status != null) status.Text = "Windows may ask for Administrator permission to finish. Your settings are preserved automatically.";
        if (bar != null) bar.Value = 100;
        if (cancelBtn != null) cancelBtn.IsEnabled = false;
    }

    private void InitializeComponent() { AvaloniaXamlLoader.Load(this); }
}
