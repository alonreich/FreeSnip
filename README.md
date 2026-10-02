<p align="center">
  <img src="src/freesnip/FreeSnip.ico" width="128" height="128" alt="FreeSnip Icon">
</p>

<h1 align="center">FreeSnip 📸🗣️</h1>

<h3 align="center">A modern, premium, lightning-fast screen capture &amp; annotation utility for Windows —<br>built from the ground up with <b>Avalonia UI</b> and <b>.NET 9</b>.</h3>

<p align="center">
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET_9-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 9" /></a>
  <a href="https://avaloniaui.net/"><img src="https://img.shields.io/badge/Avalonia_UI-11.x-purple?style=for-the-badge" alt="Avalonia UI" /></a>
  <a href="https://github.com/alonreich/FreeSnip/releases/latest"><img src="https://img.shields.io/badge/Compiled-NativeAOT-black?style=for-the-badge" alt="NativeAOT" /></a>
  <a href="https://www.gnu.org/licenses/gpl-3.0.en.html"><img src="https://img.shields.io/badge/License-GPLv3-blue.svg?style=for-the-badge" alt="GPLv3" /></a>
</p>

<h2 align="center">
  <a href="https://github.com/alonreich/FreeSnip/releases/latest/download/FreeSnip.exe">⬇️ DOWNLOAD LATEST — FreeSnip.exe ⬇️</a>
</h2>

<p align="center">
  <sub>Need built-in OCR? Grab <a href="https://github.com/alonreich/FreeSnip/releases/latest/download/FreeSnip_tesseract.exe"><b>FreeSnip_tesseract.exe</b></a> instead.</sub>
</p>

> **🔗 These links never go stale.** They are GitHub *latest-release* permalinks — they always resolve to the most recently published build, so whenever you click them you get the newest version. FreeSnip deliberately keeps **exactly one release alive at any time**: every publish deletes all previous releases and their tags, no exceptions.

---

## ✨ Features

| 🎯 Feature | ✨ Description |
| :--- | :--- |
| 🖥️ **Precision Capture** | Capture your entire screen, a specific window, or an exact rectangular region across multiple monitors — effortlessly. |
| 🎨 **Premium Image Editor** | A sleek, dark-themed editor that feels natively premium, with snapping, zooming, and multi-monitor awareness. |
| 🖍️ **Rich Annotations** | Arrows, shapes, freehand drawing, numbered steps, pixelate, Greenshot-style marker highlight, and text overlays. |
| 🤖 **Interactive OCR Mode** | Powered by Tesseract. Toggle into OCR mode right inside the editor — the UI fades away, detected text is highlighted live, and you click-and-drag to copy exactly what you need. |
| 🚀 **Blazing Fast** | Hyper-optimized **Native AOT** build for instant launch times and a tiny memory footprint. |
| ⚙️ **Highly Configurable** | Global hotkeys, startup behaviors, multi-monitor notification overlays with a configurable blink duration, and more. |

## 📦 Installation & Releases

FreeSnip ships as **standalone executables** — download and run, no installer wizard required. Two builds are published with every release:

1. **[FreeSnip.exe](https://github.com/alonreich/FreeSnip/releases/latest/download/FreeSnip.exe)** — the core, blazing-fast Native AOT compiled executable.
2. **[FreeSnip_tesseract.exe](https://github.com/alonreich/FreeSnip/releases/latest/download/FreeSnip_tesseract.exe)** — the standard deployment executable bundled with OCR capabilities.

Both links are *latest-release* permalinks and always download the newest build. There is never more than one release in this repository: each publish replaces everything that came before it.

## 🛠️ Build from Source

Requirements:
- .NET 9.0 SDK
- Windows 10/11
- GitHub CLI (`gh`), signed in — only needed for the publish step

Clone the repository and run the build script to generate the binaries in the `.\compiled\` directory:

```powershell
git clone https://github.com/alonreich/FreeSnip.git
cd FreeSnip
.\Build.cmd
```

The script compiles both binaries, stamps them with a `vYYYY.MM.DD.HHmm` version, then **replaces the one-and-only GitHub release**: every pre-existing release and tag — local and remote — is deleted first (no exceptions), a fresh release is published with `FreeSnip.exe`, `FreeSnip_tesseract.exe` and `LICENSE.txt` as assets, and the upload is verified by SHA256 digest.

To build without touching GitHub at all:

```powershell
.\Build.cmd --no-publish
```

## ⚖️ License & Open Source Compliance

FreeSnip is proudly open-source and is licensed under the **GNU General Public License v3.0 (GPLv3)**.

### Why GPLv3?
To provide you with the most reliable capture engine possible, portions of FreeSnip’s low-level native capture logic, bounds calculations and highlight filter were adapted from **[Greenshot](https://getgreenshot.org/)**, an incredible open-source screenshot tool. Because Greenshot is licensed under the GPL, FreeSnip inherits this license to ensure full compliance and respect for their hard work.

### Third-Party Acknowledgements
FreeSnip stands on the shoulders of giants. We gratefully acknowledge the following open-source projects used in our software:

- **[Avalonia UI](https://github.com/AvaloniaUI/Avalonia):** (MIT License) - The powerhouse driving our beautiful, cross-platform user interface.
- **[SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp):** (Apache 2.0 via Split License) - Used for lightning-fast, cross-platform image processing, cropping, and rendering.
- **[Tesseract OCR](https://github.com/tesseract-ocr/tesseract) & [.NET Wrapper](https://github.com/charlesw/tesseract):** (Apache 2.0) - The engine behind our magical interactive text extraction.
- **[log4net](https://github.com/apache/logging-log4net):** (Apache 2.0) - Robust and reliable application logging.

For full license texts and copyright notices of our third-party dependencies, please see the `THIRDPARTYNOTICES.txt` file included in this repository.
