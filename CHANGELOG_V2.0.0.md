# Advanced Snip v2.0.0 — consolidated release notes

This release consolidates the product changes documented after the original v1 README, covering the shipped work recorded in v2.12 through v2.38, plus the Real-Time Screen OCR Reader added for v2.0.0.

> **Source note:** The supplied history contains no separate v2.0.0 implementation changelog. This file therefore consolidates the documented v2.x changes rather than inventing changes that are not present in the supplied records; the Real-Time Screen OCR Reader supplied in the latest request is included as a v2.0.0 feature.

## Highlights

- Full-screen and region screen recording with H.264/AAC, system audio and microphone capture.
- Window recording with the selected window's own contents rather than the desktop pixels behind it.
- Recording-time annotation tools, live zoom, sound controls and a non-destructive video editor.
- A multi-type Gallery covering images, videos and PDFs, with cached thumbnails and OCR search.
- Built-in PDF viewing with text selection, find, page actions and markup.
- Mixed-DPI capture/overlay improvements for multi-monitor Windows setups.
- Cyberpunk theme plus substantially improved theme consistency across windows and floating panels.
- Expanded clipboard workflow, including gallery/editor copies, a larger preview, clear-all, multi-select-to-history and display-aware HUD placement.
- Improved settings organization, title-bar quick actions, tray integration and keyboard workflows.
- Repeated verification/build-sweep improvements across the codebase, including fixes for release-blocking compiler errors.
- **Real-Time Screen OCR Reader** with `Ctrl+Shift+O` / title-bar launch, drag-to-read capture, instant clipboard output, formatting chips, URL/search/translate actions, font modes, New OCR, resize and pinning.


## Real-Time Screen OCR Reader

Added a dedicated live screen OCR workflow for extracting text directly from visible desktop content without saving screenshot files.

- Launch with **`Ctrl+Shift+O`** or the **OCR** button in the title bar.
- Drag a selection across text on web pages, error popups, terminal windows, code, images, remote desktops and other visible content.
- Extracted text is copied instantly without creating a screenshot file.
- **Quick Format Chips:** Single line, No spaces, UPPER, lower, and Title Case.
- **Power Tools:** automatic URL detection with one-click **Open Link**, **Google Search**, **Google Translate**, **Monospace / Prose** font toggle, and **New OCR** for immediately capturing another region.
- **Resizable & Pinnable:** resize the floating OCR card from any edge or corner, or use **Pin** to keep it above other windows.
---

## Capture and scrolling

### Region capture improvements

- Preserved pixel-accurate region selection with magnification and exact coordinates.
- Improved mixed-DPI behavior so the capture overlay measures the display it is actually on instead of relying on the app window's scale assumptions.
- Overlay UI is now sized from the active display's real scale, avoiding oversized or incorrectly scaled overlays on 125%, 150%, 200% and other mixed-scale arrangements.
- Added an on-overlay scale readout to make DPI behavior observable during debugging and verification.
- Capture notifications appear on the display where the capture was performed.

### Scroll capture

- Continued to use adaptive stitching rather than assuming a fixed scroll distance.
- Fixed hangs around text selection/copy operations during scroll capture.
- Improved continuous vertical scrolling so all pages can be presented in one continuous view where applicable.
- Progress and completion handling were hardened around dynamic/lazy-loaded content.

---

## Screen recording

### Recording sources

Recording was expanded from basic capture to a full recording workflow:

- Record the monitor under the pointer.
- Record all displays as one stitched frame.
- Record a user-selected region.
- Record a selected window using the window's own paint output.

Window recording intentionally does not mean "crop whatever happens to be behind the window." The recorded window can move without the recording simply following its old desktop rectangle.

A known limitation remains: minimized windows cannot be relied on as recording sources because the application/window may stop producing usable painted frames. The application restores a minimized target instead of silently producing a blank recording.

### Audio

- Added system-audio capture through WASAPI loopback.
- Added microphone capture.
- Independent device selection and levels for each source.
- Audio is synchronized to the recording clock rather than allowing either device to determine the timeline.
- Mid-recording mute/unmute produces silence for the muted interval instead of collapsing the timeline.
- Recording continues when an audio source cannot be opened; the user is informed of the missing source instead of losing the whole take.

### Recording annotations

Added an in-recording drawing surface and tool workflow including:

- Pen
- Marker
- Arrow
- Line
- Box
- Circle
- Spotlight
- Numbered steps
- Solid redaction/hide

Drawing interactions were designed to avoid repeatedly switching tools. Modifier keys can turn a drag into a line, arrow or constrained box for that stroke, while the mouse wheel controls stroke width, color or zoom depending on the held modifier.

The tool picker can be summoned at the pointer or pinned. Escape, right-click, Done, the Off control and the active-tool badge provide independent ways to leave a drawing mode.

Undo and Clear remain available while tools are active.

### Live zoom

- Added live recording zoom with eased transitions and pointer-follow behavior.
- Zoom is applied during frame composition so the effect is visible in the recorded video without requiring a separate post-processing pass.
- The on-screen view boundary is not recorded.

---

## Video editor

A dedicated video review/edit workflow was added:

- Split at the playhead.
- Mark in/out ranges.
- Delete a selection.
- Keep only a selection.
- Reorder clips.
- Change per-clip speed from 0.25× to 4×.
- Mute individual clips.
- Undo/redo editing actions.
- Preview the edited timeline without modifying the original recording.
- Export edits to a new file.

The original recording remains untouched.

---

## Gallery

The Gallery evolved into separate **Images**, **Videos** and **PDFs** tabs.

### Images

- Filename search.
- Full-text OCR search.
- Date-range filtering.
- Sorting.
- Multi-select.
- Paging.
- Recycle-Bin deletion.
- Improved handling of OCR results for numbers, symbols and clearer text.
- OCR for very tall captures now works in horizontal bands at full resolution, rather than scaling the entire image into an unsuitable OCR size.

### Search indexing

- Indexing can resume after interruption.
- Progress is durable instead of restarting from zero after every interruption.
- Added **Rebuild index**.
- Rebuild index was moved to **Settings → Gallery** so it no longer competes with normal gallery actions.
- Search can match text across line breaks.
- Editor-generated/changed text is correctly reflected in the search index.
- The UI reports how many indexed items returned empty results rather than hiding that information.
- Indexing work is performed without blocking normal gallery interaction.

### Videos

- Added a dedicated Videos tab.
- Video thumbnails are cached on disk and regenerated when the source file changes.
- Video cards show a representative frame, duration and playback affordance.
- The preview frame is chosen away from the first frame when possible to avoid countdown/black opening frames.
- Files that cannot currently be decoded fall back to a safe placeholder instead of blocking the gallery.
- The Videos tab refreshes when a recording finishes.

### PDFs

- Added a dedicated PDFs tab.
- Added first-page thumbnails and page counts.
- Fixed PDF pages that failed to render.
- Built-in PDF viewer workflow added.
- Page fitting/export sizing issues fixed so pages retain their intended dimensions.

---

## PDF viewer

The built-in PDF viewer was expanded substantially:

- Continuous vertical scrolling through the document.
- Zoom with re-rendering at the new scale so zoom reveals actual page detail.
- DPI-correct rendering on scaled displays, avoiding blurry PDF pages.
- Text selection and copy.
- Find/search within the PDF.
- Page actions.
- PDF markup/annotation tools.
- Improved page placement and fit behavior.
- Theme integration with the rest of the application.

---

## Editor and clipboard workflow

### Editor

- Reorganized the editor toolbar so related actions are easier to reach.
- Quick actions in the title bar were standardized and colourized by semantic action.
- Fixed editor notification actions opening the wrong image.
- Improved PDF-related editor/export behavior.
- Preserved image editing capabilities from v1 while expanding integration with clipboard history.

### Clipboard history and HUD

- Copying an image from the Gallery or Editor now also promotes/adds it to clipboard history.
- The clipboard HUD opens on the display currently being used instead of always opening on the primary display.
- The HUD follows the active work display.
- Enlarged image preview added to the HUD.
- Right-clicking an item first makes that item the active selection before contextual actions are applied.
- Added **Clear** beside History to clear both the app's clipboard history and the Windows clipboard.
- Clear asks for confirmation when there is history to lose and explicitly states that saved gallery files are not affected.
- Added multi-select **Add to clipboard history** from the Gallery.
- Multi-select operations respect configured history capacity and report added, promoted, skipped and unreadable items distinctly.
- Duplicate images are promoted rather than consuming another history slot.
- Added red-pen markup to the enlarged clipboard preview.
- v2.38 made the clipboard HUD and enlarged preview follow the active application theme instead of remaining permanently dark.

---

## Themes and visual design

- Added a Cyberpunk theme.
- Improved Light, Dark and Match Windows consistency across the application's windows and dialogs.
- Theme resources are shared through application-level dictionaries so open windows update together.
- Stock WPF controls were templated to follow the active theme.
- PDF and video windows now participate in theme changes.
- Replaced system message boxes where necessary with app-owned themed dialogs.
- Floating recording/annotation/scroll overlays retain dedicated dark overlay chrome for readability over arbitrary desktop content.
- Clipboard HUD and preview became theme-aware in v2.38.
- Fixed title-bar legibility for the Record button and application name.
- Added consistent settings cards across Capture, Recording, Gallery, Shortcuts, General and Help.
- Consolidated repeated card markup into shared styles to prevent individual settings pages from drifting visually.

---

## Window, navigation and tray UX

- Main window changed from a near-square layout to a landscape layout suitable for the gallery and settings panes.
- Window sizing now checks the actual work area of the monitor where it opens and shrinks when necessary so controls do not open underneath the taskbar.
- Added title-bar quick actions for Snip, Scroll, Record and History.
- Record changes to Stop while recording.
- Recording and capture settings were separated so each category has a clearer home.
- Settings were split into Capture, Recording, Gallery, Shortcuts and General.
- Expanded Help coverage.
- Tray menu and tray behavior were cleaned up.
- Tray icon now uses the application's own icon.
- Added clear current-build/version reporting instead of leaving the sidebar at the old v1.0 label.

---

## Reliability, correctness and engineering hardening

- Fixed the recording hotkey/settings row mismatch.
- Fixed video thumbnail performance and redundant decoding.
- Fixed OCR omissions involving numbers, symbols and clear text.
- Fixed theme application in PDF and video windows.
- Fixed incorrect notification-to-image routing.
- Fixed PDF export sizing.
- Added broader programmatic structural verification for XAML names and event handlers after large UI refactors.
- Added repeated C# build/sweep checks to catch missing identifiers, incorrect references and release-blocking compiler errors.
- Fixed v2.37 compiler errors caused by an incorrect local variable reference and an instance/type name collision involving `Dispatcher`.
- Corrected the patching process after detecting a replacement that modified the wrong occurrence of duplicate source text.
- Tightened verification around code transformations rather than relying on regex-only scope analysis.

---

## Performance and resource handling

The supplied history also includes a dedicated memory pass:

- Removed unnecessary full-size bitmap copies during capture conversion.
- Reduced GDI/WIC round-trips.
- Added thumbnail creation that does not retain full-resolution source images.
- Added unmanaged memory accounting with `GC.AddMemoryPressure` / `RemoveMemoryPressure`.
- Added a debounced memory trim after capture.
- Reused overlay brushes/geometries instead of reallocating them on every mouse move.
- Avoided repeatedly creating large cropped bitmaps for the magnifier.
- Reduced unnecessary PNG encode/decode cycles.
- Reduced the memory retained by clipboard-history previews.
- Reduced the memory retained by editor undo data.

---

## Documentation/status notes

The updated README accompanying this release is the consolidated user-facing description of the feature set documented through v2.38.

The original v1 README was kept deliberately concise; this release documentation expands it to cover recording, video editing, PDFs, OCR/indexing, clipboard workflows, mixed-DPI behavior, themes, settings organization and the newer UX.

