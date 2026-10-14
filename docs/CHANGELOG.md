# FreeSnip Changelog

All notable user-facing changes to FreeSnip are documented here.
This log maintains a rolling cap of the latest 5 releases in clear, plain English.

---

### [3.3.0] - 2026-10-14
- **Outward Canvas Frame Border Preview**: Added live canvas border preview matching configured thickness and color outward without altering captured image dimensions or suffering canvas edge clipping.
- **Dynamic Editor Settings Synchronization**: Synchronized user preferences live across all active image editor windows upon saving settings.
- **High-Contrast Focus Indicators**: Global `:focus-visible` visual feedback across buttons and text inputs for WCAG keyboard accessibility.
- **Responsive Crop Tool Selection**: Wrapped aspect ratio presets in a responsive panel to prevent viewport clipping on smaller screens.
- **Real-Time Resize Validation**: Added live dimension validation and error messaging in the resize dialog, preventing invalid scale operations.
- **Symmetric Layout & Visual Polish**: Aligned color preset grids, normalized update action labeling, and harmonized toolbar separators.

---

### [3.2.0] - 2026-10-14
- **Responsive Alternate Editor Hotkeys**: Fully wired secondary alternate hotkeys across all editor tools and actions (Counter, Arrow, Line, FreeDraw, Highlight, Text, Rotate, Resize, Emoji, Blur, Crop, Duplicate, Delete).
- **Responsive Setup Cancellation**: Enabled clean, immediate cancellation on the Setup & Uninstaller progress windows, halting staging, filesystem, and registry operations without leaving locked files.
- **Legacy Rebranding Residue & Dual Startup Purge**: Automated background sanitization and cleanup for legacy app versions (`SnapVox`, `lightweight_greenshot`, `Greenshot`) across Task Scheduler, registry Run keys, shortcuts, and AppData directories, preventing duplicate app instances on startup.
- **Accurate Hotkey Conflict Reporting**: Refined PrintScreen collision detection to isolate genuine capture key collisions from third-party app shortcuts (such as OneDrive Alt+PrintScreen), ending false conflict toasts and throttling duplicate notifications.
- **Framed Canvas Centering & Dynamic Crop Resizing**: Added uniform 24px framing padding when opening snippet captures to eliminate right/bottom cutoff edges, restored automatic window resizing after cropping, and guaranteed centered canvas placement by resetting zoom focal points, zeroing scroll offsets, and refreshing viewport padding on layout changes.
- **Streamlined Update & Upgrade Dialogs**: Replaced brittle dynamic sizing with fixed geometry (`460x220` and `520x360`), eliminating text and border truncation. Converted wordy stacked buttons into clean, modern horizontal action bars ("Restart Now" / "Later" and "Update Now" / "Remind Me Later" / "Skip this version").

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

