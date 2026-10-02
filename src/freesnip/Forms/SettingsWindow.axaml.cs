using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using freesnip.foundation.core;
using freesnip.foundation.IniFile;
using freesnip.helpers;
using freesnip.foundation.interfaces.Ocr;
using freesnip.foundation.core.AvaloniaShims;
using freesnip.editor.helpers;
using freesnip.Services;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using AvaloniaColor = Avalonia.Media.Color;

namespace freesnip.forms
{
    public partial class SettingsWindow : Window
    {
        private readonly ISettingsService _settingsService;
        private CoreConfiguration _config;
        private string _loadedFingerprint = string.Empty;
        private bool _savedAndClosing;
        private bool _saveInProgress;

        private const string FormatPng  = "PNG (Lossless, Transparency Preserved - Recommended)";
        private const string FormatJpeg = "JPEG (100% Quality)";

        public SettingsWindow() : this(SimpleServiceProvider.Current.GetInstance<ISettingsService>(isOptional: true) ?? new SettingsService())
        {
        }

        public SettingsWindow(ISettingsService settingsService)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            InitializeComponent();
            UiLayoutDirection.Apply(this);
            _config = IniConfig.GetIniSection<CoreConfiguration>();
            LoadSettings();
            _loadedFingerprint = BuildFingerprint();
        }

        private string BuildFingerprint()
        {
            var values = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, object>>
            {
                new("ChkKeepBackup", this.FindControl<CheckBox>("ChkKeepBackup")?.IsChecked),
                new("ChkAutoUpdateChecks", this.FindControl<CheckBox>("ChkAutoUpdateChecks")?.IsChecked),
                new("ChkCloseEditor", this.FindControl<CheckBox>("ChkCloseEditor")?.IsChecked),
                new("ChkWarnClose", this.FindControl<CheckBox>("ChkWarnClose")?.IsChecked),
                new("ChkAddBorder", this.FindControl<CheckBox>("ChkAddBorder")?.IsChecked),
                new("NumFrameBorderThickness", this.FindControl<NumericUpDown>("NumFrameBorderThickness")?.Value),
                new("TxtFrameBorderColorHex", this.FindControl<TextBox>("TxtFrameBorderColorHex")?.Text?.Trim()),
                new("ChkLeavePictureAsIs", this.FindControl<CheckBox>("ChkLeavePictureAsIs")?.IsChecked),
                new("CboOverlayDuration", this.FindControl<ComboBox>("CboOverlayDuration")?.SelectedItem),
                new("CboOcrEngine", this.FindControl<ComboBox>("CboOcrEngine")?.SelectedItem),
                new("ChkOcrAdaptiveThreshold", this.FindControl<CheckBox>("ChkOcrAdaptiveThreshold")?.IsChecked),
                new("CboOutputFormat", this.FindControl<ComboBox>("CboOutputFormat")?.SelectedItem)
            };

            foreach (var keyName in _settingsService.DefaultHotkeys.Keys)
            {
                values.Add(new(keyName, this.FindControl<TextBox>(keyName)?.Text?.Trim()));
            }

            return _settingsService.BuildFingerprint(values);
        }

        private bool HasUnsavedChanges()
        {
            return !string.Equals(_loadedFingerprint, BuildFingerprint(), System.StringComparison.Ordinal);
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void LoadSettings()
        {
            var chkKeepBackup = this.FindControl<CheckBox>("ChkKeepBackup");
            if (chkKeepBackup != null) chkKeepBackup.IsChecked = _config.KeepBackup;

            var chkAutoUpdate = this.FindControl<CheckBox>("ChkAutoUpdateChecks");
            if (chkAutoUpdate != null) chkAutoUpdate.IsChecked = _config.AutoUpdateChecks;

            var txtAboutVersion = this.FindControl<TextBlock>("TxtAboutVersion");
            if (txtAboutVersion != null) txtAboutVersion.Text = $"Version {RuntimePathHelper.ProductVersion}";

            var txtAboutFlavor = this.FindControl<TextBlock>("TxtAboutFlavor");
            var txtAboutPlatform = this.FindControl<TextBlock>("TxtAboutPlatform");
            bool isTess = services.UpdateService.GetExpectedAssetName().Contains("tesseract", StringComparison.OrdinalIgnoreCase);
            if (txtAboutFlavor != null)
                txtAboutFlavor.Text = isTess ? "Standard Deployment (Bundled OCR Engine)" : "Native AOT (Blazing fast capture)";
            if (txtAboutPlatform != null)
                txtAboutPlatform.Text = isTess ? ".NET 9.0 (win-x64, Self-Contained)" : ".NET 9.0 (win-x64, Native AOT)";

            var btnCheckUpdates = this.FindControl<Button>("BtnCheckUpdatesNow");
            var txtUpdateStatus = this.FindControl<TextBlock>("TxtUpdateStatus");
            if (btnCheckUpdates != null)
            {
                btnCheckUpdates.Click += async (s, e) =>
                {
                    btnCheckUpdates.IsEnabled = false;
                    if (txtUpdateStatus != null) txtUpdateStatus.Text = "Checking for updates...";
                    try
                    {
                        await services.UpdateService.CheckManualAsync(this, msg =>
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (txtUpdateStatus != null) txtUpdateStatus.Text = msg;
                            });
                        });
                    }
                    finally
                    {
                        btnCheckUpdates.IsEnabled = true;
                    }
                };
            }

            var chkCloseEditor = this.FindControl<CheckBox>("ChkCloseEditor");
            if (chkCloseEditor != null) chkCloseEditor.IsChecked = _config.CloseEditorOnAction;

            var chkAddBorder = this.FindControl<CheckBox>("ChkAddBorder");
            var frameBorderPanel = this.FindControl<StackPanel>("FrameBorderOptionsPanel");
            var numThickness = this.FindControl<NumericUpDown>("NumFrameBorderThickness");
            var txtBorderHex = this.FindControl<TextBox>("TxtFrameBorderColorHex");
            var previewBorder = this.FindControl<Border>("FrameBorderColorPreview");

            if (chkAddBorder != null)
            {
                chkAddBorder.IsChecked = _config.AddFrameBorders;
                if (frameBorderPanel != null) frameBorderPanel.IsEnabled = _config.AddFrameBorders;
                chkAddBorder.IsCheckedChanged += (_, _) =>
                {
                    if (frameBorderPanel != null) frameBorderPanel.IsEnabled = chkAddBorder.IsChecked ?? true;
                };
            }
            if (numThickness != null) numThickness.Value = _config.FrameBorderThickness > 0 ? _config.FrameBorderThickness : 4;
            if (txtBorderHex != null)
            {
                txtBorderHex.Text = string.IsNullOrWhiteSpace(_config.FrameBorderColor) ? "#434343" : _config.FrameBorderColor;
                txtBorderHex.TextChanged += (_, _) =>
                {
                    if (previewBorder != null && AvaloniaColor.TryParse(txtBorderHex.Text?.Trim() ?? "", out var parsed))
                    {
                        previewBorder.Background = new SolidColorBrush(parsed);
                    }
                };
            }
            if (previewBorder != null && AvaloniaColor.TryParse(_config.FrameBorderColor, out var initialColor))
            {
                previewBorder.Background = new SolidColorBrush(initialColor);
            }

            var chkLeavePictureAsIs = this.FindControl<CheckBox>("ChkLeavePictureAsIs");
            if (chkLeavePictureAsIs != null) chkLeavePictureAsIs.IsChecked = _config.LeavePictureAsIsDuringOcr;

            var cboOutputFormat = this.FindControl<ComboBox>("CboOutputFormat");
            if (cboOutputFormat != null)
            {
                cboOutputFormat.ItemsSource = new[] { FormatPng, FormatJpeg };
                cboOutputFormat.SelectedItem = _config.OutputFileFormat == freesnip.foundation.core.Enums.OutputFormat.jpg
                    ? FormatJpeg
                    : FormatPng;
            }

            var chkWarnClose = this.FindControl<CheckBox>("ChkWarnClose");
            if (chkWarnClose != null) chkWarnClose.IsChecked = _config.WarnBeforeClosingEditor;

            var cboOverlayDuration = this.FindControl<ComboBox>("CboOverlayDuration");
            if (cboOverlayDuration != null)
            {
                cboOverlayDuration.ItemsSource = _settingsService.OverlayDurationChoices;
                cboOverlayDuration.SelectedItem = _settingsService.GetOverlayChoiceForDuration(_config.NotificationOverlayDurationMs);
            }

            var ocrPanel = this.FindControl<StackPanel>("OcrEnginePanel");
            var cboOcrEngine = this.FindControl<ComboBox>("CboOcrEngine");
            var ocrEmptyState = this.FindControl<Border>("OcrEngineEmptyState");
            var ocrEmptyText = this.FindControl<TextBlock>("OcrEngineEmptyText");
            if (cboOcrEngine != null)
            {
                var providers = SimpleServiceProvider.Current.GetAllInstances<IOcrProvider>().ToList();
                var providerNames = providers.Select(provider => provider.DisplayName).Distinct().ToList();
                cboOcrEngine.ItemsSource = providerNames;
                cboOcrEngine.SelectedItem = providerNames.Contains(_config.OcrEngine) ? _config.OcrEngine : providerNames.FirstOrDefault();

                bool hasProviders = providerNames.Count > 0;
                bool hasUsableProvider = providers.Any(provider => _settingsService.HasRequiredLanguagesSafe(provider));

                var chkAdaptive = this.FindControl<CheckBox>("ChkOcrAdaptiveThreshold");
                if (chkAdaptive != null)
                {
#if USE_TESSERACT
                    chkAdaptive.IsVisible = providers.Any(provider =>
                        provider != null && provider.EngineId != null &&
                        provider.EngineId.IndexOf("tesseract", System.StringComparison.OrdinalIgnoreCase) >= 0);
                    chkAdaptive.IsChecked = _config.OcrAdaptiveThreshold;
#else
                    chkAdaptive.IsVisible = false;
#endif
                }
                cboOcrEngine.IsVisible = hasProviders;
                cboOcrEngine.IsEnabled = hasProviders;

                if (ocrEmptyState != null)
                {
                    ocrEmptyState.IsVisible = !hasProviders || !hasUsableProvider;
                    if (ocrEmptyState.IsVisible && ocrEmptyText != null)
                    {
                        ocrEmptyText.Text = !hasProviders
                            ? "No text-recognition engine is registered, so OCR is unavailable. Restart FreeSnip; if it persists, reinstall the application."
                            : "The installed engine is missing its English or Hebrew language pack, so OCR will fail. Add both languages in Windows Settings > Time & language > Language & region, then restart FreeSnip.";
                    }
                }
            }
            else if (ocrEmptyState != null)
            {
                ocrEmptyState.IsVisible = false;
            }

            _ = UpdateAdminButtonStateAsync();

            SetHotkeyTextBox("TxtArrowKey", _config.ArrowHotkey);
            SetHotkeyTextBox("TxtLineKey", _config.LineHotkey);
            SetHotkeyTextBox("TxtTextKey", _config.TextHotkey);
            SetHotkeyTextBox("TxtResizeKey", _config.ResizeHotkey);
            SetHotkeyTextBox("TxtFreehandKey", _config.FreehandHotkey);
            SetHotkeyTextBox("TxtEmojiKey", _config.EmojiHotkey);
            SetHotkeyTextBox("TxtCounterKey", _config.CounterHotkey);
            SetHotkeyTextBox("TxtHighlightKey", _config.HighlightHotkey);
            SetHotkeyTextBox("TxtPixelate1Key", _config.PixelateHotkey1);
            SetHotkeyTextBox("TxtPixelate2Key", _config.PixelateHotkey2);
            SetHotkeyTextBox("TxtCropKey", _config.CropHotkey);
            SetHotkeyTextBox("TxtRotateCwKey", _config.RotateCwHotkey);
            SetHotkeyTextBox("TxtRotateCcwKey", _config.RotateCcwHotkey);
            SetHotkeyTextBox("TxtDuplicateObjectKey", _config.DuplicateObjectHotkey);
            SetHotkeyTextBox("TxtDeleteObjectKey", _config.DeleteObjectHotkey);
            
            SetHotkeyTextBox("TxtRegionKey", _config.RegionHotkey);
            SetHotkeyTextBox("TxtWindowKey", _config.WindowHotkey);
            SetHotkeyTextBox("TxtFullscreenKey", _config.FullscreenHotkey);
            SetHotkeyTextBox("TxtLastRegionKey", _config.LastregionHotkey);
            SetHotkeyTextBox("TxtClipboardKey", _config.ClipboardHotkey);
            SetHotkeyTextBox("TxtScrollCaptureDelimiterKey", _config.ScrollCaptureDelimiterHotkey);

            UpdatePrintScreenConflictWarning();
            _ = Task.Run(async () =>
            {
                await PrintScreenConflictHelper.WaitForHotkeyRegistrationAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                if (!PrintScreenConflictHelper.IsPrintScreenBlocked(out _)) return;
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(UpdatePrintScreenConflictWarning);
            });
        }

        private void SetHotkeyTextBox(string name, string value)
        {
            var txt = this.FindControl<TextBox>(name);
            if (txt != null) txt.Text = value;
        }

        private void UpdatePrintScreenConflictWarning()
        {
            var panel = this.FindControl<Border>("PrintScreenConflictPanel");
            var detail = this.FindControl<TextBlock>("TxtPrintScreenConflictDetail");
            if (panel == null || detail == null) return;

            if (PrintScreenConflictHelper.IsPrintScreenBlocked(out string reason))
            {
                detail.Text = PrintScreenConflictHelper.BuildSettingsWarning(reason);
                panel.IsVisible = true;
            }
            else
            {
                panel.IsVisible = false;
            }
        }

        private async void OnResetHotkeysClick(object sender, RoutedEventArgs e)
        {
            bool confirmed = await ConfirmDialog.ShowAsync(
                this,
                "Reset all hotkeys?",
                "Every shortcut on this tab goes back to the FreeSnip factory default. Any key combinations you set yourself are replaced and cannot be recovered.",
                "Reset All Hotkeys",
                "Keep My Hotkeys",
                true).ConfigureAwait(true);

            if (!confirmed) return;

            foreach (var (name, defaultValue) in _settingsService.DefaultHotkeys)
            {
                SetHotkeyTextBox(name, defaultValue);
            }

            foreach (var textBox in this.GetVisualDescendants().OfType<TextBox>())
            {
                textBox.Background = Brushes.Transparent;
            }

            ClearHotkeyConflictStyles();
            var warning = this.FindControl<TextBlock>("TxtHotkeyWarning");
            if (warning != null) { warning.Text = string.Empty; warning.IsVisible = false; }
            OverlayHelper.ShowNotification("Hotkeys reset. Save to apply.", this);
        }

        private void OnHotkeyTextBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                if (e.Key != Key.None && e.Key != Key.LWin && e.Key != Key.RWin && e.Key != Key.LeftShift && e.Key != Key.RightShift && e.Key != Key.LeftCtrl && e.Key != Key.RightCtrl && e.Key != Key.LeftAlt && e.Key != Key.RightAlt)
                {
                    var modifiers = new System.Collections.Generic.List<string>();
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) modifiers.Add("Ctrl");
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) modifiers.Add("Alt");
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) modifiers.Add("Shift");
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) modifiers.Add("Win");
                    
                    string keyName = e.Key.ToString();
                    string combined = modifiers.Count > 0 ? string.Join(" + ", modifiers) + " + " + keyName : keyName;
                    textBox.Text = combined;
                    e.Handled = true;

                    if (_settingsService.GlobalHotkeyBoxNames.Contains(textBox.Name, StringComparer.Ordinal))
                    {
                        ValidateGlobalHotkeys();
                    }
                    else
                    {
                        textBox.Background = Brushes.Transparent;
                    }
                }
            }
        }

        private void ValidateGlobalHotkeys()
        {
            var warning = this.FindControl<TextBlock>("TxtHotkeyWarning");
            ClearHotkeyConflictStyles();

            var hotkeyDict = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in _settingsService.GlobalHotkeyBoxNames)
            {
                hotkeyDict[name] = this.FindControl<TextBox>(name)?.Text;
            }

            var result = _settingsService.ValidateGlobalHotkeys(hotkeyDict);

            foreach (var name in result.ConflictedBoxNames)
            {
                var box = this.FindControl<TextBox>(name);
                if (box != null)
                {
                    box.Classes.Add("hotkey-conflict");
                    box.Tag = "conflict";
                }
            }

            if (warning != null)
            {
                warning.Text = result.FirstErrorMessage ?? string.Empty;
                warning.IsVisible = !result.IsValid;
            }
        }

        private void ClearHotkeyConflictStyles()
        {
            foreach (var tb in this.GetVisualDescendants().OfType<TextBox>())
            {
                if (tb.Name != null && tb.Name.StartsWith("Txt", StringComparison.Ordinal))
                {
                    tb.Classes.Remove("hotkey-conflict");
                    if (tb.Tag is string tag && tag == "conflict") tb.Tag = null;
                }
            }
        }

        private async void OnCancelClick(object sender, RoutedEventArgs e)
        {
            if (HasUnsavedChanges())
            {
                bool discard = await ConfirmDialog.ShowAsync(
                    this,
                    "Discard your changes?",
                    "You changed settings but did not save them. Closing now throws those changes away.",
                    "Discard Changes",
                    "Keep Editing",
                    true).ConfigureAwait(true);

                if (!discard) return;
            }

            _savedAndClosing = true;
            Close();
        }

        private async void OnSettingsClosing(object sender, WindowClosingEventArgs e)
        {
            if (_savedAndClosing || !HasUnsavedChanges()) return;

            e.Cancel = true;
            bool discard = await ConfirmDialog.ShowAsync(
                this,
                "Discard your changes?",
                "You changed settings but did not save them. Closing now throws those changes away.",
                "Discard Changes",
                "Keep Editing",
                true).ConfigureAwait(true);

            if (!discard) return;

            _savedAndClosing = true;
            Close();
        }

        private async Task UpdateAdminButtonStateAsync()
        {
            var btn = this.FindControl<Button>("BtnToggleAdmin");
            var stateLabel = this.FindControl<TextBlock>("AdminStateLabel");
            bool isAdmin = await _settingsService.IsAdminStartupConfiguredAsync().ConfigureAwait(true);

            if (btn != null)
            {
                if (isAdmin)
                {
                    btn.Content = "Remove Administrator Permissions";
                    btn.Background = this.TryFindResource("FreeSnipDestructiveBrush", out var destBrush) && destBrush is IBrush db ? db : new SolidColorBrush(Avalonia.Media.Color.Parse("#A51D2D"));
                }
                else
                {
                    btn.Content = "Run This App As an Administrator (Highest Privileges)";
                    btn.Background = this.TryFindResource("FreeSnipActionButtonBrush", out var actionBrush) && actionBrush is IBrush ab ? ab : new SolidColorBrush(Avalonia.Media.Color.Parse("#3E3E42"));
                }
            }

            if (stateLabel != null)
            {
                stateLabel.Text = isAdmin ? "Status: Administrator startup is ENABLED" : "Status: Not configured (standard privileges)";
            }
        }

        private async void OnToggleAdminClick(object sender, RoutedEventArgs e)
        {
            bool wasAdmin = await _settingsService.IsAdminStartupConfiguredAsync().ConfigureAwait(true);
            try
            {
                bool success = await _settingsService.ConfigureAdminStartupAsync(!wasAdmin, _config).ConfigureAwait(true);
                if (success)
                {
                    OverlayHelper.ShowNotification(wasAdmin ? "Admin Startup Removed" : "Admin Startup Configured", this);
                }
                else
                {
                    OverlayHelper.ShowNotification(wasAdmin ? "Failed to Remove Admin" : "Failed to Configure Admin", this);
                }
            }
            catch
            {
                OverlayHelper.ShowNotification("Permission Error", this);
            }
            await UpdateAdminButtonStateAsync().ConfigureAwait(true);
        }

        private async void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (_saveInProgress) return;
            _saveInProgress = true;
            try
            {
                ValidateGlobalHotkeys();
                foreach (var name in _settingsService.GlobalHotkeyBoxNames)
                {
                    var tb = this.FindControl<TextBox>(name);
                    if (tb != null && tb.Tag is string tag && tag == "conflict")
                    {
                        var warningText = this.FindControl<TextBlock>("TxtHotkeyWarning")?.Text;
                        await ConfirmDialog.ShowAlertAsync(
                            this,
                            "Hotkey Conflict",
                            string.IsNullOrWhiteSpace(warningText)
                                ? $"The shortcut {tb.Text} cannot be used. Pick a different key and try again."
                                : warningText + " Pick a different key and try again.",
                            "OK",
                            true).ConfigureAwait(true);
                        tb.Focus();
                        return;
                    }
                }

                var chkKeepBackup = this.FindControl<CheckBox>("ChkKeepBackup");
                if (chkKeepBackup != null) _config.KeepBackup = chkKeepBackup.IsChecked ?? true;

                var chkAutoUpdate = this.FindControl<CheckBox>("ChkAutoUpdateChecks");
                if (chkAutoUpdate != null) _config.AutoUpdateChecks = chkAutoUpdate.IsChecked ?? true;

                var chkCloseEditor = this.FindControl<CheckBox>("ChkCloseEditor");
                if (chkCloseEditor != null) _config.CloseEditorOnAction = chkCloseEditor.IsChecked ?? false;

                var chkWarnClose = this.FindControl<CheckBox>("ChkWarnClose");
                if (chkWarnClose != null) _config.WarnBeforeClosingEditor = chkWarnClose.IsChecked ?? false;

                var chkAddBorder = this.FindControl<CheckBox>("ChkAddBorder");
                if (chkAddBorder != null) _config.AddFrameBorders = chkAddBorder.IsChecked ?? true;

                var numThickness = this.FindControl<NumericUpDown>("NumFrameBorderThickness");
                if (numThickness != null && numThickness.Value.HasValue)
                {
                    _config.FrameBorderThickness = Math.Clamp((int)numThickness.Value.Value, 1, 50);
                }

                var txtBorderHex = this.FindControl<TextBox>("TxtFrameBorderColorHex");
                if (txtBorderHex != null)
                {
                    _config.FrameBorderColor = _settingsService.NormalizeHexColor(txtBorderHex.Text, _config.FrameBorderColor);
                }

                var chkLeavePictureAsIs = this.FindControl<CheckBox>("ChkLeavePictureAsIs");
                if (chkLeavePictureAsIs != null) _config.LeavePictureAsIsDuringOcr = chkLeavePictureAsIs.IsChecked ?? false;

                var cboOutputFormatSave = this.FindControl<ComboBox>("CboOutputFormat");
                if (cboOutputFormatSave?.SelectedItem is string formatChoice)
                {
                    bool isJpeg = string.Equals(formatChoice, FormatJpeg, System.StringComparison.Ordinal);
                    _config.OutputFileFormat      = isJpeg ? freesnip.foundation.core.Enums.OutputFormat.jpg
                                                           : freesnip.foundation.core.Enums.OutputFormat.png;
                    _config.OutputFileAllowPng    = !isJpeg;
                    _config.OutputFileJpegQuality = 100;
                }

#if USE_TESSERACT
                var chkAdaptiveSave = this.FindControl<CheckBox>("ChkOcrAdaptiveThreshold");
                if (chkAdaptiveSave != null && chkAdaptiveSave.IsVisible) _config.OcrAdaptiveThreshold = chkAdaptiveSave.IsChecked ?? false;
#endif

                var cboOverlayDuration = this.FindControl<ComboBox>("CboOverlayDuration");
                if (cboOverlayDuration?.SelectedItem is string overlayChoice)
                {
                    _config.NotificationOverlayDurationMs = _settingsService.ParseOverlayDurationMs(overlayChoice);
                }

                var cboOcrEngine = this.FindControl<ComboBox>("CboOcrEngine");
                if (cboOcrEngine != null && cboOcrEngine.IsVisible && cboOcrEngine.SelectedItem != null)
                {
                    _config.OcrEngine = cboOcrEngine.SelectedItem.ToString();
                }

                _config.ArrowHotkey = this.FindControl<TextBox>("TxtArrowKey")?.Text ?? _config.ArrowHotkey;
                _config.LineHotkey = this.FindControl<TextBox>("TxtLineKey")?.Text ?? _config.LineHotkey;
                _config.TextHotkey = this.FindControl<TextBox>("TxtTextKey")?.Text ?? _config.TextHotkey;
                _config.ResizeHotkey = this.FindControl<TextBox>("TxtResizeKey")?.Text ?? _config.ResizeHotkey;
                _config.FreehandHotkey = this.FindControl<TextBox>("TxtFreehandKey")?.Text ?? _config.FreehandHotkey;
                _config.EmojiHotkey = this.FindControl<TextBox>("TxtEmojiKey")?.Text ?? _config.EmojiHotkey;
                _config.CounterHotkey = this.FindControl<TextBox>("TxtCounterKey")?.Text ?? _config.CounterHotkey;
                _config.HighlightHotkey = this.FindControl<TextBox>("TxtHighlightKey")?.Text ?? _config.HighlightHotkey;
                _config.PixelateHotkey1 = this.FindControl<TextBox>("TxtPixelate1Key")?.Text ?? _config.PixelateHotkey1;
                _config.PixelateHotkey2 = this.FindControl<TextBox>("TxtPixelate2Key")?.Text ?? _config.PixelateHotkey2;
                _config.CropHotkey = this.FindControl<TextBox>("TxtCropKey")?.Text ?? _config.CropHotkey;
                _config.RotateCwHotkey = this.FindControl<TextBox>("TxtRotateCwKey")?.Text ?? _config.RotateCwHotkey;
                _config.RotateCcwHotkey = this.FindControl<TextBox>("TxtRotateCcwKey")?.Text ?? _config.RotateCcwHotkey;
                _config.DuplicateObjectHotkey = this.FindControl<TextBox>("TxtDuplicateObjectKey")?.Text ?? _config.DuplicateObjectHotkey;
                _config.DeleteObjectHotkey = this.FindControl<TextBox>("TxtDeleteObjectKey")?.Text ?? _config.DeleteObjectHotkey;
                
                string oldRegion = _config.RegionHotkey;
                string oldWindow = _config.WindowHotkey;
                string oldFull = _config.FullscreenHotkey;
                string oldLast = _config.LastregionHotkey;
                string oldClip = _config.ClipboardHotkey;
                
                _config.RegionHotkey = this.FindControl<TextBox>("TxtRegionKey")?.Text ?? _config.RegionHotkey;
                _config.WindowHotkey = this.FindControl<TextBox>("TxtWindowKey")?.Text ?? _config.WindowHotkey;
                _config.FullscreenHotkey = this.FindControl<TextBox>("TxtFullscreenKey")?.Text ?? _config.FullscreenHotkey;
                _config.LastregionHotkey = this.FindControl<TextBox>("TxtLastRegionKey")?.Text ?? _config.LastregionHotkey;
                _config.ClipboardHotkey = this.FindControl<TextBox>("TxtClipboardKey")?.Text ?? _config.ClipboardHotkey;
                _config.ScrollCaptureDelimiterHotkey = this.FindControl<TextBox>("TxtScrollCaptureDelimiterKey")?.Text ?? _config.ScrollCaptureDelimiterHotkey;

                bool hotkeysChanged = oldRegion != _config.RegionHotkey ||
                                      oldWindow != _config.WindowHotkey ||
                                      oldFull != _config.FullscreenHotkey ||
                                      oldLast != _config.LastregionHotkey ||
                                      oldClip != _config.ClipboardHotkey;

                await _settingsService.SaveSettingsAsync(_config, hotkeysChanged).ConfigureAwait(true);
                
                _loadedFingerprint = BuildFingerprint();
                _savedAndClosing = true;
                OverlayHelper.ShowNotification("Settings Saved Successfully", this);
                Close();
            }
            catch (System.Exception ex)
            {
                await ConfirmDialog.ShowAlertAsync(this, "Error", $"Error saving settings: {ex.Message}", "OK", true).ConfigureAwait(true);
            }
            finally
            {
                _saveInProgress = false;
            }
        }

        private void OnFrameBorderColorPresetClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string hex)
            {
                var txtBorderHex = this.FindControl<TextBox>("TxtFrameBorderColorHex");
                var previewBorder = this.FindControl<Border>("FrameBorderColorPreview");
                if (txtBorderHex != null) txtBorderHex.Text = hex;
                if (previewBorder != null && AvaloniaColor.TryParse(hex, out var color))
                {
                    previewBorder.Background = new SolidColorBrush(color);
                }
                var btnColor = this.FindControl<Button>("FrameBorderColorBtn");
                btnColor?.Flyout?.Hide();
            }
        }

        public void SelectTab(string tabHeader)
        {
            var tabControl = this.FindControl<TabControl>("SettingsTabControl");
            if (tabControl != null)
            {
                foreach (var item in tabControl.Items)
                {
                    if (item is TabItem tab && string.Equals(tab.Header?.ToString(), tabHeader, StringComparison.OrdinalIgnoreCase))
                    {
                        tabControl.SelectedItem = tab;
                        break;
                    }
                }
            }
        }

        public static async Task<bool> ShowAboutAsync(Window owner = null)
        {
            var settingsWin = new SettingsWindow();
            settingsWin.SelectTab("About");
            if (owner != null && owner.IsVisible)
            {
                await settingsWin.ShowDialog(owner);
            }
            else
            {
                settingsWin.Show();
                settingsWin.Activate();
            }
            return true;
        }
    }
}