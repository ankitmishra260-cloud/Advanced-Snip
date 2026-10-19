# ✂ Advanced Snip by Ankit Mishra

**Current release:** v3.0.0

A fast, private, feature-rich screen capture tool for Windows — built in C# / WPF / .NET 8.

No telemetry. No cloud. No subscription. Everything runs locally.

**Documentation status:** v3.0.0 documentation, incorporating the feature set and fixes documented through v3.0.0 including Desktop Pinning and Inverted Index OCR Search.

---

## Quick actions

The title bar carries the four things this app does — **Snip**, **Scroll**, **Record** and **History** — one click from wherever you are in the window. Each carries a colour drawn from the active palette's semantic brushes (accent, success, danger, warning), so they stay visible in Light, Dark and Cyberpunk without a per-theme special case — set as resource *references* rather than resolved brushes, so they follow a theme change instead of freezing at whichever theme was loaded when the window opened. The glyph takes the colour and the label stays neutral: four differently-coloured words in a row is a toybox, a coloured mark beside a plain word is a signpost. The Record button turns into **Stop** while a recording is running, so the window always agrees with the floating toolbar and the hotkeys. Everything here is also a global hotkey; the buttons are for the times you have the window open and would rather not remember one.

**In the tray**, double-clicking the icon opens the window on the gallery — the same as every other tray application in Windows, and the same as the bold default entry in its menu. The menu itself is short on purpose: the way in, the two captures that cannot wait for a window to open, and the way out.

| Tray menu | |
|---|---|
| **Open Advanced Snip** | Default action — also what double-click does |
| Snip now | Shows your configured hotkey beside it |
| Record the screen | Becomes **Stop recording** while one is running |
| Settings… | |
| Exit | |

Scroll capture, clipboard history and opening an image from elsewhere are not in the menu. All three keep their global hotkeys, and all three are one click away once the window is up — a tray menu paying space for doors that already exist is just a longer list to read.

---

## Features at a glance

| | |
|---|---|
| **Region capture** | Hotkey-triggered overlay with a magnifier loupe and exact pixel coordinates. Click a window to grab it whole. |
| **Scroll capture** | Points at whatever is under the cursor, scrolls to the end, and stitches every screenful into one tall image. |
| **Clipboard history** | Keeps the last *N* captures in memory. Cycle and paste with hotkeys, or navigate the thumbnail HUD with arrow keys or the mouse wheel. |
| **Editor** | Crop, rotate, flip, annotate, redact, select text, remove backgrounds, export to PNG / JPEG / BMP / TIFF / ICO. |
| **Real-time screen OCR** | `Ctrl+Shift+O` or the title-bar OCR button. Drag across any visible text to extract and copy it instantly without saving a screenshot. Includes formatting chips, URL actions, search/translate, font views, New OCR, resize, and pinning. |
| **Gallery** | Browses the save folder for thousands of captures. Full-text OCR search, date range filter, multi-select, sort, paging. |
| **Dark / light / system theme** | Follows the OS preference live. |
| **Per-monitor DPI** | Captures land on the right pixels across mixed-scale setups. |
| **Custom title bar** | Native Aero Snap and snap layouts kept intact. |
| **Screen recording** | H.264 MP4 with system sound and microphone, on-screen drawing tools, live zoom. |
| **Video editor** | Split, cut, reorder, per-clip speed and mute. Non-destructive — exports a new file. |

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

**Enlarged preview:** **Right-click** any thumbnail — or press **Space** — to open it full size. The HUD's thumbnails are 110px tall, which is enough to tell one capture from another and not enough to tell whether it is the *right* one: you can see that a screenshot is a settings dialog without being able to read which setting.

The **mouse wheel** and **arrow keys** browse the whole history from inside the preview, and it resizes to each picture. **Enter** pastes, **Esc** or a second right-click goes back to the strip, and clicking away closes both.

The clipboard updates after a short pause while navigating, so spinning the wheel past eight thumbnails does not queue eight full PNG encodes. The preview shows the full-resolution image rather than the thumbnail, pulling it back from disk if memory pressure had evicted it — which is exactly when a closer look is most worth having.

---

## Editor

Open from the tray notification after a capture, by double-clicking a gallery item, from **Open image…** in the gallery toolbar, or by dragging any image file onto the window.

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

### Real-time screen OCR reader

Press **`Ctrl+Shift+O`**, or click the **OCR** button in the title bar, to capture text directly from the screen without creating or saving a screenshot file. Drag a selection across text anywhere on the desktop — web pages, error popups, terminal windows, code, images, remote desktops, and similar content — and the recognised text is extracted and copied instantly.

#### Quick format chips

- **Single line** — merges wrapped lines, useful for prompts and URLs.
- **No spaces** — removes spaces for serial numbers, keys, tokens, and similar strings.
- **UPPER** — converts the result to uppercase.
- **lower** — converts the result to lowercase.
- **Title Case** — converts the result to title case.

#### Power tools

The OCR card automatically detects web URLs and provides a one-click **Open Link** action. It also provides **Google Search** and **Google Translate**, a **Monospace / Prose** font toggle for code or normal text, and **New OCR** to immediately capture another region without closing the card.

#### Resizable and pinnable

The floating OCR card can be resized by dragging any edge or corner. Click **Pin** to keep it floating above other windows while you continue working.

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

### Save as PDF

`Ctrl+P`. Three layouts, each with a live preview of the exact page that will be written — the preview and the writer both drive off one `PlanPage` call, so they cannot disagree.

| Layout | What it does |
|---|---|
| **Split the image across pages** | Scales to the paper width and cuts the height into pages, with an adjustable overlap so nothing is lost at a seam. Zoom past 100% and it tiles into columns as well as rows. |
| **One page as tall as the image** | A single custom-sized page. Nothing cut, nothing scaled. |
| **Fit the whole image on one page** | Shrinks the whole image onto the chosen paper. |

**Position** places the image when there is room to spare: left, centred or right across the page, and top, middle or bottom down it. Available in both the fitting and splitting layouts — though sliced pages stay anchored to the top whatever the vertical setting says, since a slice floated to the middle of its page would put a gap in the middle of a sentence that continues onto the next one.

In the fitting layout the zoom slider means "as large as it will go" at 100%, and shrinks the image further below that. It is clamped at the fit, because a zoom that overflowed would push the image off a page whose whole purpose is to hold all of it.

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

## Screen recording

Press `Ctrl + Shift + R`, or use the Record tab. Recording writes H.264 MP4 through the
encoder built into Windows, so files stay small and any hardware encoder on the machine
(Quick Sync, NVENC, AMF) is used automatically. Nothing is bundled and nothing is
installed.

### What gets recorded

| Source | What it captures |
|---|---|
| The monitor the pointer is on | That whole display |
| Everything | All displays, stitched into one frame |
| Pick a region each time | A rectangle you drag out |
| **Pick a window each time** | One window's **own contents** |

The window source asks the window to draw itself rather than copying the patch of desktop it happens to be sitting on. So anything in front of it is not in the take, and moving it around does not walk it out of frame.

**Two limits worth knowing before you rely on it.**

Content that never goes through a window's normal paint path — video decoded straight onto a GPU surface, hardware overlays, some accelerated canvases — comes back blank, with the rest of the window intact around it. Those pixels genuinely are not in the window's paint output. The app takes one sample frame before recording starts and warns you if the window renders blank, so you find out in a second rather than after ten minutes. For that content, use a screen region instead and keep the window unobstructed.

**A minimised window cannot be recorded at all**, by this or any other capture method. A minimised window is not composited and its application generally stops rendering altogether, so there are no frames for anything to collect. Picking one restores it first rather than pretending otherwise.

The frame keeps the size it started at, because H.264 cannot change dimensions mid-stream. Resize the window smaller during a recording and the surplus stays black; larger and it is cropped.

Choose the monitor under the pointer, every monitor at once, or a region you drag out —
the same overlay used for an ordinary snip.

### Sound

Two independent sources, recorded into the same file as AAC:

- **What the computer is playing**, via WASAPI loopback. No virtual cable, no driver, and
  it does not mute or interrupt whatever is playing.
- **The microphone.**

Each has its own device picker and its own level, so a narrated demo can have the
narration sit above the interface sounds rather than under them. Leaving a device on
*Default* means it follows whatever Windows is using, which keeps working when a headset
is plugged in halfway through.

Neither sound card drives the timeline. Both write into buffers that are drained against
the recording's own clock, padded with silence for whichever device is momentarily
behind. Two devices with two crystals will drift apart over an hour if either is allowed
to set the pace; this way the sound is where the picture is at the end of a long take as
well as the start.

`Ctrl + Shift + U` mutes mid-recording. It silences the output rather than stopping
capture, so the muted stretch is silence of the right length instead of a hole for
everything after it to slide across.

### Drawing while recording

**Most of the time you should not have to change tool at all.** With the pen or marker
out, the modifier you are holding when a drag *starts* decides what that one stroke draws:

| Hold | Draw |
|---|---|
| nothing | freehand |
| `Shift` | a straight line |
| `Ctrl` | an arrow |
| `Ctrl + Shift` | a box |

It lasts for that stroke and reverts. Nothing is selected, nothing is dismissed, and the
pointer never leaves the thing being pointed at. `Alt` is deliberately not used — pressing
it opens the menu bar of whatever is being recorded, and that would be in the take.

The wheel is free for the same reason: while a tool is active the overlay swallows it
anyway, so rolling it changes the **stroke width**, `Shift` + wheel steps through the
**colours**, and `Ctrl` + wheel drives the **zoom**.

`Ctrl + Shift + A` opens the tool picker **wherever the pointer is**. Press `1`–`9` to
choose a tool and the picker gets out of the way immediately, handing focus back to
whatever you were doing. **Pin it** and it does the opposite: it shrinks to a strip of
icons, stays where you drag it, stops taking focus when you click a tool, and is still
there next recording. Summoned suits a recording with three annotations in it; pinned
suits one that is mostly annotation.

If you did pick a shape yourself, `Shift` while dragging constrains it — square, circle,
or a line and arrow snapped to 45°.

| | |
|---|---|
| **Pen** | Freehand. |
| **Marker** | Wide and semi-transparent, for highlighting a line of text. |
| **Arrow** | Drag from tail to point. |
| **Line** | Straight. |
| **Box** / **Circle** | Outline, or shaded with *Fill* on. |
| **Spotlight** | Dims everything outside a rectangle. |
| **Step** | Drops numbered badges — 1, 2, 3 — for walking through a sequence. |
| **Hide** | A solid block over a password or a name. Solid on purpose: blur and pixelation are both recoverable. |

The toolbar keeps the clock, the file size, the sound level and the active tool. Neither
it, the picker, nor the drawing surface appears in the recording — only your screen and
what you drew.

The drawing surface swallows clicks while a tool is active, which is a serious thing to
put over someone's desktop. There are always five ways out that do not depend on each
other: `Esc`, right-click, the *Done* button, the *Off* tile in the picker, and a badge at
the top of the recorded area saying which tool is live and how to leave it. The badge also
names the shape a press would draw *right now*, so a held modifier is never a guess.

**Undo and Clear stay on the toolbar whether or not a tool is active.** Drawings do not
disappear when you put the tools away, so the controls that remove them must not either.
`Clear` is recoverable: one `Undo` brings back everything the last erase removed.

### Live zoom

`Ctrl + Shift + +` and `-` zoom into the recording as it happens — applied when each frame
is composed, so viewers see it, the file is no larger, and there is no processing pass
afterwards. It eases rather than jumping, and follows the pointer only once it leaves the
middle of the view, so the picture does not swim. A dashed outline on screen shows what
viewers can see; the outline itself is not recorded.

---

## Video editor

Click the *Recording saved* notification, or *Review last recording* on the Record tab.

The edit is a list of the pieces you are keeping, and nothing touches the file until you
export. Splitting, deleting a range, reordering, changing a clip's speed and muting a clip
are all instant, and preview plays the original while skipping across the cuts — so what
you watch is what you will get, without waiting for anything to render.

| | |
|---|---|
| **Split** (`S`) | Cuts the clip in two at the playhead. |
| **Mark in / out** (`I` / `O`) | Selects a range on the original. |
| **Delete selection** (`Del`) | Removes it and closes the gap. |
| **Keep only selection** | Throws away everything else. |
| **Move clip** | Reorders pieces in the finished file. |
| **Speed** | 0.25x to 4x, per clip. |
| **Mute clip** | Silences one piece without cutting it. |

The timeline draws the **edit**, not the original — block widths are how long each piece
lasts in the finished file. Click anywhere on it to move the playhead.

*Export edit* writes a new file alongside the original and never changes the original.
Someone reviewing a recording they just made has exactly one copy of it.

---

## Gallery

Three tabs: **Images**, **Videos** and **PDFs**. They are separate lists rather than one mixed grid because the questions you ask of them differ — you search pictures for the words inside them, and recordings by when they were made and how long they run.

All three scan fast even at tens of thousands of files, because scanning reads metadata only and decodes thumbnails only for the page currently on screen. The grid is virtualized: only the cards inside the viewport exist as visuals, so scrolling a page of 500 costs the same as scrolling a page of 20. Indexing and thumbnail loading are cancelled the moment you page, sort, or search again.

**PDFs** lists the documents in your capture folder with a rendered first page and a page count. Double-click to read one in a built-in viewer — page through with the arrow keys, zoom, or hand off to whatever your machine normally opens PDFs with. Rendering uses the PDF engine that ships with Windows, so there is no extra dependency and nothing leaves the machine.

Thumbnails are cached on disk (`%LocalAppData%\AdvancedSnip\thumbs`), keyed by the file's path, size and modification time — so a recording is decoded once and read back as a small JPEG every time after. Edit or re-export a recording and it gets a fresh frame rather than a stale one.

**Videos** reads the recordings folder, which falls back to the capture folder when you have not set one — in which case both tabs read the same directory and are told apart by extension. Cards show a frame from the recording, its running time, and a play badge; double-click or Enter opens it in the video editor to play, trim and export.

The frame is taken about a tenth of the way in rather than at the start, because recordings routinely open on a countdown, a desktop mid-repaint, or black. A file that cannot be read — still being written, or a codec Windows will not open — falls back to a plain card instead of disappearing or holding up the page. Recordings appear as soon as one finishes; the video tab refreshes itself when a recording stops.

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
- Double-click or Enter to open — the image editor for a capture, the video editor for a recording

**Capture time is read from the filename first.** Copying or syncing a folder rewrites file timestamps, but `Snip_20260728_143005` still says exactly when it was taken.

---

## Settings

Open from the sidebar or the tray right-click menu. Split into five tabs — **Capture**, **Recording**, **Gallery**, **Shortcuts**, **General** — so that eight unrelated sections are not one long scroll.

**Recording settings live here, under Recording**, not on the Record page. The Record page is the launcher: it shows what pressing Start will actually do (source, frame rate and quality, which sound is being recorded, where the file lands, and a size estimate) and has a shortcut straight into this tab. Having settings on both pages meant neither was where anyone looked first.

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

Light, Dark, **Cyberpunk**, or **Match Windows** — the last follows the OS preference live, including its light/dark schedule, rather than resolving once at launch.

**Cyberpunk** is a near-black base with a blue cast, full-cyan accents, magenta for danger and amber for warnings, and a cyan window edge. It uses gradients, but only on three surfaces — the page background, the navigation rail, and the selected navigation item — each of which exists once on screen and is painted once. `Brush.Card` is deliberately left solid because every tile in the gallery uses it, which means hundreds at a time during a scroll.

There is no animation and no glow effect in it, on purpose. A pulsing neon edge is a timer that never stops, and it would keep the render thread awake for the whole session; WPF's `Effect` pipeline forces an offscreen render pass per affected element. Neither is worth a look. Every brush in all three palettes is frozen, which makes them immutable, shareable and free of change-notification overhead.

Type stays a cool near-white rather than going neon — accents, borders and state carry the colour, because a theme you cannot read is a costume. Every text-on-surface pair clears WCAG AA at 4.8:1 or better.

The theme is a single resource dictionary swapped at application level, so open windows repaint immediately without a restart — and their title bars are re-darkened at the same time, so nothing is left half-switched.

Confirmations and errors use the app's own dialog rather than the system message box, which is drawn by Windows and always arrives light-titled. Everything with a title bar follows the theme. The recording toolbar, tool picker and scroll-capture progress panel retain their fixed dark overlay chrome because they sit directly over the desktop; the clipboard HUD and its enlarged preview are theme-aware and now follow the active application theme. Stock WPF controls (text boxes, checkboxes, dropdowns, scrollbars, menus) are templated to follow it — without this, dark mode leaves them painted from system brushes and staying white. Title bars are darkened through DWM.

The capture overlays remain dark in both themes so they stay legible over arbitrary desktop content. The clipboard HUD and its enlarged preview are exceptions: since v2.38 they follow the active application theme.

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
| Real-time screen OCR | `Ctrl + Shift + O` |
| Next image + paste | `Ctrl + Shift + .` |
| Previous image + paste | `Ctrl + Shift + ,` |
| Start / stop recording | `Ctrl + Shift + R` |

All remappable in **Settings ▸ Shortcuts**.

### While recording

These are live only for the duration of a recording, and are released the moment it
stops — nothing else on the machine loses them in between. They are system-wide, so they
work inside whatever application is being recorded.

| Shortcut | Action |
|---|---|
| Hold `Shift` / `Ctrl` / `Ctrl+Shift` while drawing | Line / arrow / box, for that stroke only |
| Wheel over the screen | Stroke width |
| `Shift` + wheel | Colour |
| `Ctrl` + wheel | Zoom |
| `Ctrl+Shift+A` | Open the tool picker at the pointer (press again to close) |
| `1`–`9` in the picker | Pen, marker, arrow, line, box, circle, spotlight, step, hide |
| `Shift`+`1`–`6` in the picker | Colour |
| `P` in the picker | Pin / unpin it |
| `0` or `Esc` in the picker | Put the tools away |
| `Ctrl+Shift+P` | Pen |
| `Ctrl+Shift+M` | Marker |
| `Ctrl+Shift+X` | Arrow |
| `Ctrl+Shift+F` | Spotlight (focus) |
| `Ctrl+Shift+B` | Hide (blackout) |
| `Ctrl+Shift+N` | Numbered step |
| `[` / `]` with Ctrl+Shift | Previous / next tool |
| `Ctrl+Shift+Z` | Undo — or bring back the last erase |
| `Ctrl+Shift+E` | Erase everything drawn |
| `Ctrl+Shift+U` | Mute / unmute the sound |
| `Ctrl+Shift+ +` / `-` / `0` | Zoom in / out / reset |
| `Esc` or right-click | Leave the current tool |

Line, box and circle have no chord of their own on purpose: they are one held key away
while drawing, and three more claimed combinations would cost the applications being
recorded more than they are worth. The app's own hotkeys are off limits too — `S` snips,
`W` is scroll capture, `V` is the clipboard HUD and `R` stops the recording — which is why
Spotlight is on `F` and the box has no chord at all.

### Video editor

| Shortcut | Action |
|---|---|
| Space | Play / pause |
| ← / → | Back / forward one second (Shift for five) |
| Page Up / Page Down | Jump to the previous / next cut |
| S | Split at the playhead |
| I / O | Mark the start / end of a selection |
| Delete | Remove the marked selection |
| Ctrl+Z / Ctrl+Y | Undo / redo |
| Home | Back to the start |

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
│   │
│   │   ── Recording ──────────────────────────────────────────────────
│   ├── MediaFoundation.cs        COM interop: sink writer, source reader, media types
│   ├── VideoRecorder.cs          H.264 + AAC encoder, bitrate derivation
│   ├── ScreenRecorder.cs         Capture loop, frame pacing, audio pump
│   ├── AudioCapture.cs           WASAPI loopback + microphone, format conversion, ring
│   ├── AudioMixer.cs             Two sources onto one clock, 16-bit PCM out
│   ├── AnnotationLayer.cs        Freehand ink, painted into frames on the capture thread
│   ├── ShapeLayer.cs             Arrows, boxes, circles, spotlight, redaction, steps
│   ├── ZoomController.cs         Live zoom with easing and pointer-follow dead zone
│   ├── VideoProject.cs           The edit as a segment list — split, cut, reorder, speed
│   └── VideoTrimmer.cs           Renders a project to a new file
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
├── RecordHudWindow.xaml / .cs    Recording status bar — clock, size, level, tool
├── RecordToolPalette.xaml / .cs  Tool picker — summoned at the pointer, or pinned
├── RecordAnnotationWindow.xaml / .cs  The drawing surface over the recorded region
├── VideoReviewWindow.xaml / .cs  Player and timeline editor
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
- Very tall scroll captures are read for text in horizontal bands at full resolution, rather than scaled down to fit the OCR engine's size limit. That keeps the text legible to the engine, and keeps the work off the UI thread; expect a progress percentage rather than an instant result on a 20,000-pixel page.
- Recording sound uses WASAPI loopback for system audio, which needs no virtual cable or driver. If a source cannot be opened — no microphone, a device unplugged mid-take, an N edition of Windows without the Media Feature Pack — the video still records and the notification says what was lost. Losing sound is never allowed to lose the take.
- Recording tops out at 4096 × 2304, which is what the Media Foundation H.264 encoder accepts. Two wide monitors side by side exceed it; record one monitor or a region.
- Exporting an edit decodes and re-encodes rather than copying the compressed stream. That is what makes cuts land exactly where the marker is: H.264 can only start decoding at a keyframe, so a stream copy would snap each cut to the nearest one, sometimes seconds away. It costs time, and it never touches the original file.
- Per-clip speed changes re-time the audio without pitch-shifting it, by dropping or repeating whole frames. Proper time-stretching is a signal-processing project of its own; chipmunk narration on a 2x screen recording is not what anyone wants.

---

## Licence

MIT
