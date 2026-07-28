# ✂ Advanced Snip by Ankit Mishra

A fast, private, feature-rich screen capture tool for Windows — built in C# / WPF / .NET 8.

No telemetry. No cloud. No subscription. Everything runs locally.

---

## Features at a glance

| | |
|---|---|
| **Region capture** | Hotkey-triggered overlay with a magnifier loupe and exact pixel coordinates. Click a window to grab it whole. |
| **Scroll capture** | Points at whatever is under the cursor, scrolls to the end, and stitches every screenful into one tall image. |
| **Clipboard history** | Keeps the last *N* captures in memory. Cycle and paste with hotkeys, or navigate the thumbnail HUD with arrow keys or the mouse wheel. |
| **Editor** | Crop, rotate, flip, annotate, redact, select text, remove backgrounds, export to PNG / JPEG / BMP / TIFF / ICO. |
| **Gallery** | Browses the save folder for thousands of captures. Full-text OCR search, date range filter, multi-select, sort, paging. |
| **Dark / light / system theme** | Follows the OS preference live. |
| **Per-monitor DPI** | Captures land on the right pixels across mixed-scale setups. |
| **Custom title bar** | Native Aero Snap and snap layouts kept intact. |

---

## Capture

### Region snip

Press the hotkey (`Ctrl+Shift+S` by default). The screen freezes and a crosshair overlay appears. Drag a rectangle, or click a window to grab the whole thing. A magnifier loupe at the cursor shows live screen content with pixel coordinates for pixel-accurate edges.

The crop is an exact copy — no resampling at the edges. Layered content such as open menus and tooltips is included.

### Scroll capture

Press `Ctrl+Shift+W`, then point at the area to capture. The overlay highlights the scrollable region under the cursor — the actual page content in a browser rather than the whole window including toolbars. The mouse wheel or arrow keys widen or narrow the selection through the pane hierarchy, so a nested scroller (a sidebar, an embedded code block) can be targeted on its own.

Once confirmed, the app scrolls to the top, captures, scrolls to the bottom, and stitches every screenful into one image.

**What makes the stitching actually accurate:**

- Scroll distance is *measured* per frame, not assumed. Consecutive frames are reduced to rows of averaged luminance cells and matched to find the real offset. Works identically for wheel notches, Page Down, list-item steps, and smooth-scroll animations that land anywhere.
- The scrolling viewport is auto-detected by finding the longest run of rows that moved between frames. Excludes browser chrome, sticky headers, Explorer column headings, and status bars — no per-app special casing.
- The scrollbar is found by testing which columns *fail* to travel with the content, so it is trimmed without cutting real content.
- Frame completion is detected by re-capturing until pixels stop changing, so lazy-loaded images get the time they need.
- UI Automation is probed as an optional accuracy boost — exact progress percentage, confirmed end-of-content — but capture works entirely on pixels without it.

The progress window parks on a different monitor from the capture and is excluded from the screen grab, so it never appears in the result. **Stop & keep** ends early and saves everything gathered so far.

### After every capture

- Auto-saved to the configured folder (PNG or JPEG, configurable quality)
- Copied to the clipboard
- Added to the in-memory clipboard history
- Tray notification appears — **click it to open the capture in the editor**

---

## Clipboard history

The last *N* captures stay in memory (5–30, configured in Settings). Three ways to use them:

**Hotkeys** (remappable):

| Hotkey | Action |
|---|---|
| `Ctrl+Shift+V` | Open the thumbnail HUD |
| `Ctrl+Shift+.` | Next image → clipboard |
| `Ctrl+Shift+,` | Previous image → clipboard |

**Thumbnail HUD:** Press `Ctrl+Shift+V`. A floating panel appears with all captures as thumbnails. Navigate with **arrow keys** or the **mouse wheel**, press **Enter** to paste into the window you came from, or **Esc** to close.

The clipboard updates after a short pause while navigating, so spinning the wheel past eight thumbnails does not queue eight full PNG encodes.

---

## Editor

Open from the tray notification after a capture, by double-clicking a gallery item, from **Edit an image…** in the tray menu, or by dragging any image file onto the window.

### Drawing tools

| Key | Tool |
|---|---|
| C | Crop — drag a box, then Enter to apply |
| P | Pen — freehand |
| H | Highlighter — translucent marker |
| R | Rectangle outline |
| E | Ellipse outline |
| A | Arrow |
| B | Redact — solid black block |
| T | Select text |

Redaction uses a solid block rather than blur or pixelation — both of those are recoverable from a determined attacker, and painting over the pixels is not.

### Text selection on the image (T)

Every recognised word is overlaid on the picture. Drag across words to select a reading-order run — like selecting in a PDF — and the selection is copied on release. Double-click takes the whole line; Ctrl+A takes everything.

Powered by the OCR engine built into Windows (`Windows.Media.Ocr`) — no cloud, no model file, no NuGet package. Uses whichever language packs are installed.

### Copy text (toolbar button)

Reads all text in the image (or the selected crop region if one is active) and puts it on the clipboard. The recognised text appears in a panel at the bottom so individual lines can be selected. `Ctrl+Shift+C`.

Text recognised in the editor is fed into the gallery index, so the same image is never re-read during a gallery text search.

### Remove background

Makes a flat, uniform background transparent. Works extremely well on screenshots because their backgrounds are flat and synthetic.

Three things that go beyond a plain magic wand:

1. **Seeded from the border, not from a colour.** A colour is only removed where it is connected to the outside, so a white page behind a browser window does not take the window's white toolbar with it.
2. **Anti-aliased edges get partial alpha.** Screenshot edges are blends of subject and background; hard 0-or-255 alpha turns them into a jagged fringe.
3. **Colour decontamination.** A semi-transparent edge pixel still holds the old background colour. Un-mixing it removes the halo that would appear when the cut-out is pasted onto a different backdrop.

Tolerance and edge-softness sliders re-preview live. Clicking any region the fill could not reach adds it as a seed. Cancel returns to the unmodified image.

### Export formats

**PNG** (with transparency), **JPEG**, **BMP**, **TIFF**, or **ICO**.

The ICO encoder is written from scratch — the .NET runtime has no ICO writer. Every standard icon size is included: 16, 24, 32, 48, 64, 128, and 256 px. Windows picks a different size for the taskbar, Alt+Tab, the desktop and the file dialog; an icon carrying only 256×256 is shell-scaled with no regard for legibility. Large frames use PNG compression inside the container. Non-square images are centred on a transparent square rather than distorted; nothing is upscaled past the source resolution.

JPEG and BMP carry no alpha channel, so a transparent image is composited onto white before saving.

### Other editor shortcuts

| Shortcut | Action |
|---|---|
| Ctrl+Z / Ctrl+Y | Undo / redo (24 steps) |
| Ctrl+scroll | Zoom in / out |
| Ctrl+0 | Fit to window |
| Ctrl+S | Save (overwrites the original) |
| Ctrl+C | Copy image to clipboard |
| Ctrl+O | Open another image |

---

## Gallery

Scans the save folder — fast even at tens of thousands of files, because it reads metadata only and decodes thumbnails only for the page currently on screen. Indexing and thumbnail loading are cancelled the moment you page, sort, or search again.

### Searching and filtering

**Filename search** (top box) filters by file name as you type.

**Text inside images** — tick **Search text inside images** to reveal a second search box. As you type, only captures containing those words are shown.

Recognition runs in the background, newest captures first, using the built-in Windows OCR engine. The index is cached at `%AppData%\AdvancedSnip\ocr-index.json` and stays current by comparing file size and modification time. A progress indicator shows how much has been indexed.

**Date range** — a preset dropdown covers today, yesterday, last 7 days, last 30 days, and this year. **Custom range** reveals two date pickers. Dates entered in reverse order are swapped rather than silently returning nothing; the end date includes the whole day.

The two search boxes are independent filters that both narrow, so a file name and a phrase together means "a file whose name contains X *and* whose content contains Y."

### Sorting and navigation

- Sort by capture time (newest or oldest first), name (A–Z or Z–A), or file size
- Page through results — 150 per page by default, adjustable from 50 to 500 in Settings
- Multi-select with Ctrl and Shift; Ctrl+A for everything; Delete to move to the Recycle Bin
- Double-click or Enter to open in the editor

**Capture time is read from the filename first.** Copying or syncing a folder rewrites file timestamps, but `Snip_20260728_143005` still says exactly when it was taken.

---

## Settings

Open from the sidebar or the tray right-click menu.

| Setting | Default | Notes |
|---|---|---|
| Save folder | `Pictures\AdvancedSnips` | |
| Filename prefix | `Snip` | Timestamp appended automatically |
| Image format | PNG | JPEG quality configurable separately |
| Copy to clipboard on capture | On | |
| Clipboard history size | 8 | 5–30 |
| Show tray notification | On | |
| Click notification to edit | On | |
| **Theme** | Match Windows | Light / Dark / Match Windows (live) |
| Gallery thumbnails per page | 150 | 50–500 |
| Send deleted captures to Recycle Bin | On | |
| Search text inside images | On | |
| Scroll max height | 20 000 px | 2 000–60 000 |
| Scroll speed | Balanced | Fast / Balanced / Thorough |
| Auto-detect scroll region | On | |
| Restore scroll position after capture | On | |
| Start with Windows | Off | Verified at every launch |
| Open settings on start | On | Suppressed for sign-in launches |
| Minimise to tray on close | On | |

All hotkeys are remappable (must include Ctrl, Alt, Shift, or Win).

Settings are stored in `%AppData%\AdvancedSnip\settings.json`.

---

## Startup reliability

**Start with Windows** does more than write a registry key:

- **Path repair.** A Run value is an absolute path. Rebuilding or moving the app silently breaks it. Advanced Snip checks and repairs the entry at every launch.
- **Task Manager detection.** Disabling a startup app in Task Manager writes a separate `StartupApproved` flag rather than removing the Run key. An app reading only the key reports "enabled" forever while never actually starting. Advanced Snip detects this and reports it plainly, with a re-enable button that acts only when you click it.
- **Write verification.** Every registry write is read back and compared.

A sign-in launch passes `--startup` and goes straight to the tray regardless of the "open settings on start" setting.

---

## Multi-monitor and DPI

The app targets **Per-Monitor-V2 DPI awareness** (Windows 10 1703 and later). Under the older system-DPI mode Windows virtualises coordinates for any display not matching the primary's scale factor, so captures are silently stretched and crops land in the wrong place.

Per-Monitor-V2 means every rectangle in the app is a genuine physical pixel anywhere on the virtual desktop. The selection overlay spans all displays and applies the inverse of WPF's own scaling, so a selection drawn across a 150% laptop panel and a 100% external monitor crops precisely where it was drawn.

The gallery, settings window, and clipboard HUD all open on the display you are actively working on, not always the primary.

---

## Theme

Light, Dark, or **Match Windows** — follows the OS preference live, including its light/dark schedule, rather than resolving once at launch.

The theme is a single resource dictionary swapped at application level, so open windows repaint immediately without a restart. Stock WPF controls (text boxes, checkboxes, dropdowns, scrollbars, menus) are templated to follow it — without this, dark mode leaves them painted from system brushes and staying white. Title bars are darkened through DWM.

The capture overlays and the clipboard HUD stay dark in both themes — they sit on top of a screenshot and need fixed chrome to remain legible.

---

## Requirements

- Windows 10 version 2004 (build 19041) or later
- .NET 8 runtime (Windows Desktop workload)

For OCR: at least one Windows language pack with *Optical character recognition* installed.
Settings → Time & language → Language & region → your language → Language options → Optical character recognition.

---

## Building from source

```powershell
git clone https://github.com/your-handle/advanced-snip
cd advanced-snip/AdvancedSnip
dotnet build -c Release
```

Requires the .NET 8 SDK with the Windows desktop workload. No NuGet packages needed — all OCR and imaging APIs come from the WPF and WinRT framework references.

> **SDK version note.** The project targets `net8.0-windows10.0.19041.0`. You can build it with the .NET 8 SDK or any later one (including .NET 10). The runtime target is `net8.0`, not the SDK version. To target a later runtime, change `TargetFramework` in `AdvancedSnip.csproj`; no code changes needed.

---

## Hotkey reference

| Action | Default |
|---|---|
| Region snip | `Ctrl + Shift + S` |
| Scroll capture | `Ctrl + Shift + W` |
| Clipboard history HUD | `Ctrl + Shift + V` |
| Next image + paste | `Ctrl + Shift + .` |
| Previous image + paste | `Ctrl + Shift + ,` |

All remappable in Settings.

### Editor shortcuts

| Shortcut | Action |
|---|---|
| C / P / H / R / E / A / B / T | Switch tool |
| Enter (crop tool) | Apply crop |
| Escape | Cancel selection or close |
| Ctrl+Z / Ctrl+Y | Undo / redo |
| Ctrl+S | Save |
| Ctrl+C | Copy image to clipboard |
| Ctrl+Shift+C | Copy text from image |
| Ctrl+A | Select all text (text tool) |
| Ctrl+O | Open another image |
| Ctrl+scroll | Zoom |
| Ctrl+0 | Fit to window |

---

## Project layout

```
AdvancedSnip/
├── Services/
│   ├── Win32.cs                  P/Invoke — all in one place
│   ├── DisplayInfo.cs            Monitor enumeration, DPI, work areas
│   ├── WindowPlacement.cs        Physical-pixel window positioning helpers
│   ├── WindowChromeSupport.cs    WM_GETMINMAXINFO hook for the custom title bar
│   ├── AppSettings.cs            JSON settings load / save / clone
│   ├── ClipboardHistory.cs       Ring buffer with current-index tracking
│   ├── ClipboardService.cs       Multi-format clipboard writes (DIB + PNG stream)
│   ├── HotKeyManager.cs          RegisterHotKey / UnregisterHotKey wrapper
│   ├── TrayIconManager.cs        NotifyIcon with a runtime-drawn icon
│   ├── StartupManager.cs         Run key + StartupApproved flag verification
│   ├── ThemeManager.cs           ResourceDictionary swap, DWM dark title bar, OS event
│   ├── ImageInterop.cs           System.Drawing ↔ WPF BitmapSource bridges
│   ├── ScreenCapture.cs          BitBlt with PrintWindow fallback
│   ├── OcrService.cs             Windows.Media.Ocr wrapper, word-level layout
│   ├── OcrIndex.cs               Persistent text index, background indexing
│   ├── BackgroundRemover.cs      Flood fill + edge feathering + colour decontamination
│   ├── IconEncoder.cs            Multi-size ICO writer
│   │
│   │   ── Scroll capture ──────────────────────────────────────────────
│   ├── WindowFinder.cs           Z-order hit test, Chromium subframe resolution
│   ├── OverlayHost.cs            Per-monitor inverse-scale overlay base
│   ├── CaptureFrame.cs           Per-row luminance fingerprint for frame matching
│   ├── ScrollMatcher.cs          Frame-to-frame shift measurement and gutter detection
│   ├── ScrollDriver.cs           Injected wheel → posted wheel → keyboard fallback
│   ├── UiaScroll.cs              Optional UI Automation probe
│   ├── StitchCanvas.cs           Growable pixel-exact output canvas
│   └── ScrollCaptureEngine.cs    Orchestration: probe, adapt, stitch, stop
│
├── Themes/
│   ├── Light.xaml                Semantic brush tokens, light palette
│   ├── Dark.xaml                 Same keys, dark palette
│   └── Controls.xaml             Implicit styles for stock WPF controls
│
├── App.xaml / App.xaml.cs        Entry point, hotkeys, tray wiring, editor plumbing
├── MainWindow.xaml / .cs         Gallery + Settings + About (custom chrome)
├── EditorWindow.xaml / .cs       Image editor
├── HistoryWindow.xaml / .cs      Clipboard history HUD
├── SnipOverlay.xaml / .cs        Region selection overlay
├── ScrollTargetOverlay.xaml / .cs  Scroll capture target picker
├── ScrollProgressWindow.xaml / .cs Scroll capture progress display
└── app.manifest                  Per-Monitor-V2 DPI declaration
```

---

## Notes and known limits

- Scroll capture reads real screen pixels, so the target window must be visible and in front. It is brought forward automatically; if an app refuses the foreground change, the result notes this.
- Leave the mouse and keyboard alone during a scroll capture — the cursor is borrowed to deliver wheel events.
- Scroll capture is vertical only. Horizontal position is whatever the window was at when capture started.
- Content that changes as it scrolls (parallax, animations, video) cannot be stitched cleanly by any pixel-based tool.
- The background remover works extremely well on screenshots with flat, uniform backgrounds. It will not separate a subject from a complex photographic background; that requires a segmentation model. `Services/BackgroundRemover.cs` is the documented extension point if you want to add one.
- OCR requires at least one Windows language pack with the OCR option installed.

---

## Licence

MIT
