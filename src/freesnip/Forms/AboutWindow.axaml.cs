using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using freesnip.foundation.core;
using freesnip.services;

namespace freesnip.forms;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var versionText = this.FindControl<TextBlock>("VersionText");
        if (versionText != null)
        {
            versionText.Text = $"Version {RuntimePathHelper.ProductVersion}";
        }

        var flavorText = this.FindControl<TextBlock>("FlavorText");
        var platformText = this.FindControl<TextBlock>("PlatformText");

        bool isTesseract = UpdateService.GetExpectedAssetName().Contains("tesseract", StringComparison.OrdinalIgnoreCase);
        if (flavorText != null)
        {
            flavorText.Text = isTesseract
                ? "Standard Deployment (Bundled OCR Engine)"
                : "Native AOT (Blazing fast capture)";
        }
        if (platformText != null)
        {
            platformText.Text = isTesseract
                ? ".NET 9.0 (win-x64, Self-Contained)"
                : ".NET 9.0 (win-x64, Native AOT)";
        }

        var githubLink = this.FindControl<Button>("GitHubLinkBtn");
        if (githubLink != null)
        {
            githubLink.Click += (s, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://github.com/alonreich/FreeSnip",
                        UseShellExecute = true
                    });
                }
                catch { }
            };
        }

        var checkUpdatesBtn = this.FindControl<Button>("CheckUpdatesBtn");
        var updateStatusText = this.FindControl<TextBlock>("UpdateStatusText");
        if (checkUpdatesBtn != null)
        {
            checkUpdatesBtn.Click += async (s, e) =>
            {
                checkUpdatesBtn.IsEnabled = false;
                if (updateStatusText != null) updateStatusText.Text = "Checking...";
                try
                {
                    await UpdateService.CheckManualAsync(this, msg =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (updateStatusText != null) updateStatusText.Text = msg;
                        });
                    });
                }
                finally
                {
                    checkUpdatesBtn.IsEnabled = true;
                }
            };
        }

        var closeBtn = this.FindControl<Button>("CloseBtn");
        if (closeBtn != null)
        {
            closeBtn.Click += (s, e) => Close();
        }
    }

    private void InitializeComponent() { AvaloniaXamlLoader.Load(this); }
}
