using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace freesnip.forms;

public enum UpdateChoice
{
    Dismissed,
    UpdateNow,
    NotNow,
    SkipThisVersion,
    NeverTellMeAgain
}

public partial class UpdateAvailableWindow : Window
{
    public UpdateChoice Choice { get; private set; } = UpdateChoice.Dismissed;

    public UpdateAvailableWindow()
    {
        InitializeComponent();

        Wire("UpdateNowBtn", UpdateChoice.UpdateNow);
        Wire("SkipBtn", UpdateChoice.SkipThisVersion);
        Wire("NeverBtn", UpdateChoice.NeverTellMeAgain);

        AddHandler(InputElement.KeyDownEvent, (s, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Choice = UpdateChoice.Dismissed;
                Close();
            }
        }, RoutingStrategies.Tunnel);
    }

    private void Wire(string buttonName, UpdateChoice choice)
    {
        var button = this.FindControl<Button>(buttonName);
        if (button != null)
        {
            button.Click += (s, e) => { Choice = choice; Close(); };
        }
    }

    public void SetVersions(Version local, string remoteTag, string releaseNotes = null)
    {
        string remote = remoteTag.TrimStart('v', 'V');
        var yourVersion = this.FindControl<TextBlock>("YourVersionText");
        var newVersion = this.FindControl<TextBlock>("NewVersionText");
        if (yourVersion != null) yourVersion.Text = $"Your version: v{local}";
        if (newVersion != null) newVersion.Text = $"New version: v{remote}";

        var notesExpander = this.FindControl<Expander>("ReleaseNotesExpander");
        var notesText = this.FindControl<TextBlock>("ReleaseNotesText");
        if (notesExpander != null && notesText != null)
        {
            if (!string.IsNullOrWhiteSpace(releaseNotes))
            {
                notesText.Text = releaseNotes.Trim();
                notesExpander.IsVisible = true;
                notesExpander.IsExpanded = true;
            }
            else
            {
                notesText.Text = "No release notes were provided for this release.";
                notesExpander.IsVisible = true;
                notesExpander.IsExpanded = false;
            }
        }
    }

    public static async Task<UpdateChoice> AskAsync(Window owner, Version localVersion, string remoteTag, string releaseNotes = null)
    {
        try
        {
            var dlg = new UpdateAvailableWindow();
            dlg.SetVersions(localVersion, remoteTag, releaseNotes);
            if (owner != null && owner.IsVisible)
            {
                await dlg.ShowDialog(owner);
            }
            else
            {
                dlg.Show();
                var tcs = new TaskCompletionSource<UpdateChoice>();
                dlg.Closed += (s, e) => tcs.TrySetResult(dlg.Choice);
                return await tcs.Task;
            }
            return dlg.Choice;
        }
        catch
        {
            return UpdateChoice.NotNow;
        }
    }

    private void InitializeComponent() { AvaloniaXamlLoader.Load(this); }
}
