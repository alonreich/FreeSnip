
using freesnip.native;
using freesnip.native.foundation;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using freesnip.foundation.core.AvaloniaShims;
using System.Reflection;
using freesnip.foundation.core.Enums;
using freesnip.foundation.IniFile;
using freesnip.foundation.Interfaces;
using System.Diagnostics.CodeAnalysis;

namespace freesnip.foundation.core
{
    [IniSection("Core", Description = "snapvox core configuration")]
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    public class CoreConfiguration : IniSection, INotifyPropertyChanged
    {
#pragma warning disable CS0067
        public event PropertyChangedEventHandler PropertyChanged;
#pragma warning restore CS0067

        [IniProperty("Language", Description = "The language in IETF format (e.g. en-US)")]
        public string Language { get; set; }

        [IniProperty("RegionHotkey", Description = "Hotkey for starting the region capture", DefaultValue = "PrintScreen")]
        public string RegionHotkey { get; set; } = "PrintScreen";

        [IniProperty("WindowHotkey", Description = "Hotkey for starting the window capture", DefaultValue = "Alt + PrintScreen")]
        public string WindowHotkey { get; set; } = "Alt + PrintScreen";

        [IniProperty("FullscreenHotkey", Description = "Hotkey for starting the fullscreen capture", DefaultValue = "Ctrl + PrintScreen")]
        public string FullscreenHotkey { get; set; } = "Ctrl + PrintScreen";
        
        [IniProperty("LastregionHotkey", Description = "Hotkey for starting the last region capture", DefaultValue = "")]
        public string LastregionHotkey { get; set; } = "";

        [IniProperty("ClipboardHotkey", Description = "Hotkey for starting capture from clipboard", DefaultValue = "")]
        public string ClipboardHotkey { get; set; } = "";

        public HotkeyBinding GetRegionBinding() => HotkeyBinding.Parse(RegionHotkey);
        public HotkeyBinding GetWindowBinding() => HotkeyBinding.Parse(WindowHotkey);
        public HotkeyBinding GetFullscreenBinding() => HotkeyBinding.Parse(FullscreenHotkey);

        [IniProperty("TextToolThickness", Description = "Default thickness for Text tool", DefaultValue = "2.0")]
        public double TextToolThickness { get; set; } = 2.0;

        [IniProperty("IsFirstLaunch", Description = "Is this the first time launch?", DefaultValue = "true")]
        public bool IsFirstLaunch { get; set; } = true;

        [IniProperty("ClipboardFormats", Separator = ",", Description = "Specify which formats we copy on the clipboard? Options are: PNG, HTML, HTMLDATAURL and DIB", DefaultValue = "PNG,DIB")]
        public List<ClipboardFormat> ClipboardFormats { get; set; } = new List<ClipboardFormat>();

        [IniProperty("CaptureMousepointer", Description = "Should the mouse be captured?", DefaultValue = "false")]
        public bool CaptureMousepointer { get; set; } = false;

        [IniProperty("CaptureDelay", Description = "Capture delay in milliseconds.", DefaultValue = "0")]
        public int CaptureDelay { get; set; } = 0;

        [IniProperty("ScreenCaptureMode", Description = "The capture mode used to capture a screen. (Auto, FullScreen, Fixed)", DefaultValue = "Auto")]
        public ScreenCaptureMode ScreenCaptureMode { get; set; }

        [IniProperty("RunAsAdministratorOnStartup", Description = "Run snapvox elevated when it starts automatically.", DefaultValue = "false")]
        public bool RunAsAdministratorOnStartup { get; set; } = false;

        [IniProperty("OutputFileFormat", Description = "Default file type for writing screenshots. Options: png (lossless, default) or jpg (100% quality).", DefaultValue = "png")]
        public OutputFormat OutputFileFormat { get; set; } = OutputFormat.png;

        [IniProperty("OutputFileJpegQuality", Description = "JPEG file save quality in %. Always kept at 100 (maximum quality). Not user-configurable.", DefaultValue = "100")]
        public int OutputFileJpegQuality { get; set; } = 100;

        [IniProperty("OutputFileAllowPng", Description = "Enable lossless PNG output (full alpha transparency preserved). true by default; set false only when OutputFileFormat=jpg.", DefaultValue = "true")]
        public bool OutputFileAllowPng { get; set; } = true;

        [IniProperty("DisableHotkeys", Description = "Disable hotkeys.", DefaultValue = "False")]
        public bool DisableHotkeys { get; set; } = false;

        [IniProperty("UseTextFrame", Description = "Show a rectangular frame around text annotations.", DefaultValue = "true")]
        public bool UseTextFrame { get; set; } = true;

        [IniProperty("KeepBackup", Description = "Keep 24 Hours Backup of Snips/Text.", DefaultValue = "true")]
        public bool KeepBackup { get; set; } = true;

        [IniProperty("NotificationOverlayDurationMs", Description = "Total duration of the notification overlay blink cycle in milliseconds (250-10000).", DefaultValue = "1000")]
        public int NotificationOverlayDurationMs { get; set; } = 1000;

        [IniProperty("MagneticSnappingEnabled", Description = "Enable magnetic snapping in the editor.", DefaultValue = "true")]
        public bool MagneticSnappingEnabled { get; set; } = true;

        [IniProperty("CloseEditorOnAction", Description = "Close Image Editor on Copy/Download/OCR.", DefaultValue = "true")]
        public bool CloseEditorOnAction { get; set; } = true;

        [IniProperty("UserDownloadPath", Description = "Custom download path if default Downloads folder is missing.")]
        public string UserDownloadPath { get; set; }

        [IniProperty("LastPixelateStrength", DefaultValue = "25")]
        public int LastPixelateStrength { get; set; } = 25;

        [IniProperty("LastCropMode", Description = "Crop shape last picked in the editor: Regular, KeepRatio, Square, Wide, Tall, VerticalStrip or HorizontalStrip.", DefaultValue = "Regular")]
        public string LastCropMode { get; set; } = "Regular";

#if USE_TESSERACT
        [IniProperty("OcrEngine", Description = "Default OCR Engine", DefaultValue = "Tesseract + Windows (Mixed EN/HE)")]
        public string OcrEngine { get; set; } = "Tesseract + Windows (Mixed EN/HE)";
#else
        [IniProperty("OcrEngine", Description = "Default OCR Engine", DefaultValue = "Windows Native OCR Engine")]
        public string OcrEngine { get; set; } = "Windows Native OCR Engine";
#endif

#if USE_TESSERACT
        [IniProperty("OcrAdaptiveThreshold", Description = "Use adaptive (Sauvola) black-and-white conversion for the external OCR engine. Better on gradient or dark-mode backgrounds, sometimes worse on flat ones.", DefaultValue = "false")]
        public bool OcrAdaptiveThreshold { get; set; } = false;
#endif

        [IniProperty("LeavePictureAsIsDuringOcr", Description = "Should the picture remain as is during OCR?", DefaultValue = "false")]
        public bool LeavePictureAsIsDuringOcr { get; set; } = false;

        [IniProperty("AddFrameBorders", Description = "Add Frame Borders to Snips.", DefaultValue = "true")]
        public bool AddFrameBorders { get; set; } = true;

        [IniProperty("FrameBorderColor", Description = "Color of the frame border added to snips.", DefaultValue = "#434343")]
        public string FrameBorderColor { get; set; } = "#434343";

        [IniProperty("FrameBorderThickness", Description = "Thickness in pixels of the frame border added to snips.", DefaultValue = "4")]
        public int FrameBorderThickness { get; set; } = 4;

        [IniProperty("BypassScreenTintOverlays", Description = "Capture true colours by excluding full-screen night-mode / warm-light tinting overlays (f.lux software mode, Iris, SunsetScreen and similar) from the shot.", DefaultValue = "true")]
        public bool BypassScreenTintOverlays { get; set; } = true;

        [IniProperty("WarnBeforeClosingEditor", Description = "Warn before closing the image editor with unsaved changes.", DefaultValue = "false")]
        public bool WarnBeforeClosingEditor { get; set; } = false;

        [IniProperty("ClipboardHistoryPromotionDelayMs", Description = "Delay in milliseconds between copying the file path and the image to the clipboard, so Windows clipboard history can register the file entry. Deprecated / 0 by default.", DefaultValue = "0")]
        [Obsolete("Deprecated. UiClipboard uses atomic Win32 multi-format clipboard batching.")]
        public int ClipboardHistoryPromotionDelayMs { get; set; } = 0;

        [IniProperty("ArrowHotkey", DefaultValue = "A")]
        public string ArrowHotkey { get; set; } = "A";

        [IniProperty("LineHotkey", DefaultValue = "L")]
        public string LineHotkey { get; set; } = "L";

        [IniProperty("TextHotkey", DefaultValue = "T")]
        public string TextHotkey { get; set; } = "T";

        [IniProperty("ResizeHotkey", DefaultValue = "R")]
        public string ResizeHotkey { get; set; } = "R";

        [IniProperty("FreehandHotkey", DefaultValue = "D")]
        public string FreehandHotkey { get; set; } = "D";

        [IniProperty("EmojiHotkey", DefaultValue = "E")]
        public string EmojiHotkey { get; set; } = "E";

        [IniProperty("CounterHotkey", DefaultValue = "I")]
        public string CounterHotkey { get; set; } = "I";

        [IniProperty("HighlightHotkey", DefaultValue = "H")]
        public string HighlightHotkey { get; set; } = "H";

        [IniProperty("PixelateHotkey1", DefaultValue = "O")]
        public string PixelateHotkey1 { get; set; } = "O";

        [IniProperty("PixelateHotkey2", DefaultValue = "P")]
        public string PixelateHotkey2 { get; set; } = "P";

        [IniProperty("CropHotkey", DefaultValue = "C")]
        public string CropHotkey { get; set; } = "C";

        [IniProperty("RotateCcwHotkey", DefaultValue = "Left")]
        public string RotateCcwHotkey { get; set; } = "Left";

        [IniProperty("RotateCwHotkey", DefaultValue = "Right")]
        public string RotateCwHotkey { get; set; } = "Right";

        [IniProperty("DuplicateObjectHotkey", DefaultValue = "Ctrl + D")]
        public string DuplicateObjectHotkey { get; set; } = "Ctrl + D";

        [IniProperty("DeleteObjectHotkey", DefaultValue = "Delete")]
        public string DeleteObjectHotkey { get; set; } = "Delete";

        [IniProperty("ScrollCaptureDelimiterHotkey", DefaultValue = "Space")]
        public string ScrollCaptureDelimiterHotkey { get; set; } = "Space";

        [IniProperty("CustomColors", Description = "A comma separated list of custom colors.", DefaultValue = "#000000,#00FF00,#FF0000,#FFFF00,#0000FF,#FFFFFF")]
        public string CustomColors { get; set; } = "#000000,#00FF00,#FF0000,#FFFF00,#0000FF,#FFFFFF";

        [IniProperty("LastTextSize", DefaultValue = "20")]
        public double LastTextSize { get; set; } = 20;

        [IniProperty("LastTextBoxWidth", DefaultValue = "0")]
        public double LastTextBoxWidth { get; set; } = 0;

        [IniProperty("LastTextBoxHeight", DefaultValue = "0")]
        public double LastTextBoxHeight { get; set; } = 0;

        [IniProperty("LastEmojiSize", DefaultValue = "60")]
        public double LastEmojiSize { get; set; } = 60.0;

        [IniProperty("LastCounterSize", DefaultValue = "42")]
        public double LastCounterSize { get; set; } = 42.0;

        [IniProperty("LastCounterColor", DefaultValue = "#FF0000")]
        public string LastCounterColor { get; set; } = "#FF0000";

        [IniProperty("LastToolColors", DefaultValue = "")]
        public string LastToolColors { get; set; } = "";

        [IniProperty("LastToolThicknesses", DefaultValue = "")]
        public string LastToolThicknesses { get; set; } = "";

        public override object GetDefault(string property) => null;

        public override void AfterLoad()
        {
            if (ClipboardFormats == null || ClipboardFormats.Count == 0) ClipboardFormats = new List<ClipboardFormat> { ClipboardFormat.DIB };
            // Keep OutputFileFormat and OutputFileAllowPng in sync — PNG is the default lossless output.
            if (OutputFileFormat == OutputFormat.png)
                OutputFileAllowPng = true;
            else if (OutputFileFormat == OutputFormat.jpg)
                OutputFileAllowPng = false;
            else if (OutputFileAllowPng)
                OutputFileFormat = OutputFormat.png;
            else
                OutputFileFormat = OutputFormat.jpg;

            // Quality is strictly maximum (100%) — never expose or allow sub-100 lossy output.
            OutputFileJpegQuality = 100;
        }
    }
}
