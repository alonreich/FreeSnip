# FreeSnip Changelog

All notable user-facing changes to FreeSnip are documented here.
This log maintains a rolling cap of the latest 5 releases in clear, plain English.

---

### [3.1.0] - 2026-10-11
- **Anchor-Preserving Canvas Refit**: Cropping, slicing, or resizing dynamically resizes the editor window in place around its current center anchor without disorienting jumps to the monitor center.
- **Intelligent Workspace Sizing & Zoom Guard**: Automatically resets zoom magnification to 100% on crop commit and detects manual window border resizing to preserve custom user frame dimensions.
- **Antivirus False-Positive Hardening**: Replaced temporary directory staging with direct Program Files staging and native reboot deletion (`MoveFileEx`), eliminating false-positive malware heuristics.
- **Binary & Metadata Integrity**: Synchronized PE metadata (`OriginalFilename`, `InternalName`, `ProductVersion`), preserved portable PDB debug directories, Authenticode signed all bundled third-party libraries, and extended startup telemetry grace period.

---

### [3.0.0] - 2026-10-10
- **High-Performance Editor & Non-Destructive Canvas**: Built on Avalonia UI with vector annotations (arrows, shapes, text, emojis, counters), customizable line thickness, and instant undo/redo history.
- **Dual-Engine Text Recognition (OCR)**: Fast offline text extraction using Windows OCR or bundled LTS Tesseract, copying recognized text to the clipboard and opening it in Notepad.
- **Intelligent Application-Agnostic Scroll Capture**: Continuous vertical and horizontal capturing with consensus motion tracking that filters out stationary ribbons, toolbars, and formulas.
- **Context-Aware Save & Double Clipboard**: Lossless PNG exports with resilient download folder fallback, in-memory Ctrl+C/Ctrl+V vector duplication, and instant double-clipboard image plus quoted path copying.
- **Shadow-Staged Resilient Updates**: Automatic background update checks with strict SHA-256 validation, resumable downloads, shadow staging, and settings preservation.

---

### [2.9.0] - 2026-09-15
- **Unified Cross-Monitor Capture**: Borderless virtual desktop rubberband selection supporting mixed-DPI multi-monitor layouts without coordinate drift.
- **Magnifier Lens & Color Sampler**: 8x pixel magnification with live HEX color code display and circular zoom loupe for millimeter-precision region selection.
- **Instant Snapshot Retention**: Optional 24-hour temporary backup storing raw captures locally with automated cleanup.

---

### [2.8.0] - 2026-08-01
- **Permanent Redaction & Blur Tool**: Irreversible pixelation and blur filters to mask sensitive credentials, passwords, and personal information.
- **Global Hotkey Customization**: Configurable keyboard combinations with real-time clash detection preventing hotkey conflicts with other tools.

---

### [2.7.0] - 2026-06-20
- **Magnetic Window Snapping**: Cursor snapping directly to open application windows with a 10-pixel proximity threshold for quick window snips.
- **Non-Destructive Crop & Slice**: Crop, resize, and slice canvas areas without flattening or rasterizing vector annotations.
