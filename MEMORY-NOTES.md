# Advanced Snip 2.6 — memory pass

Behaviour is unchanged. Every edit here is about how much memory the app holds
and for how long.

## Why three screenshots cost 700 MB

Two things compounded.

**A PNG on disk is not the size of a bitmap in memory.** A saved capture is
2–3 MB because PNG compresses it. Live, a bitmap is always
`width × height × 4` bytes with no compression: 14.7 MB for 2560×1440,
33 MB for 3840×2160. Ten captures were never 30 MB.

**Every snip captured the whole virtual desktop, and held it four times.**
The region you drag is cropped out of a full-desktop grab, and the overlay kept
that grab alive as:

| copy | dual 1440p | released |
|---|---|---|
| GDI+ `Bitmap` | 29 MB | disposed |
| `GetHbitmap()` inside the conversion | 29 MB | disposed |
| WIC buffer behind the `BitmapSource` | 29 MB | GC only |
| WPF render surface for the full-screen `Image` | ~29 MB | GC only |

The last two are the problem, and not just because they are large. A
`BitmapSource` is a ~40 byte managed object in front of an unmanaged WIC
buffer. The collector budgets its work against the managed heap, so from its
point of view nothing was allocated and there is no reason to run. Dead
captures therefore accumulated indefinitely.

On top of that: PNG encode buffers land on the Large Object Heap, which is
swept but never compacted by default; the history HUD bound full-resolution
images into 110-pixel-tall elements; and the editor's undo stack held 24
uncompressed copies of the picture.

OCR indexing is a real but transient spike (four workers, one full decode
each) and only runs once the gallery has been opened. It was not the cause.

## Changes

### `Services/ImageInterop.cs`
- `LockBits` → `BitmapSource.Create` directly, removing the `GetHbitmap()`
  round-trip and one full-size GDI copy per capture.
- Pixel format is `Bgr32`, which is exactly what
  `CreateBitmapSourceFromHBitmap` produced before, so nothing changes
  visually. It also sidesteps the screen-capture trap where `BitBlt` leaves
  alpha at zero and a `Bgra32` reading renders the shot invisible.
- Dropped the PNG encode/decode round-trip small crops used to take; it cost a
  compression pass and two transient copies to reach identical pixels. Kept as
  a fallback for bitmaps that refuse to lock.
- New `MakeThumbnail`, baked into a `CachedBitmap` so it does not hold the
  full-size source.

### `Services/MemoryGuard.cs` (new)
- `GC.AddMemoryPressure` / `RemoveMemoryPressure` so the collector finally
  accounts for the pixels behind each image.
- `RequestTrim()`: a debounced, LOH-compacting collection that runs after a
  capture, when the app is idle by definition. Also returns the working set.

### `SnipOverlay.xaml.cs`, `ScrollTargetOverlay.xaml.cs`
- The dim layer allocated a brush and up to three geometries **per mouse
  move**. Now built once and mutated in place.
- The magnifier built a `CroppedBitmap` per mouse move, each one a live WIC
  object referencing the full desktop image. Now one reused `WriteableBitmap`,
  with the old path kept as a fallback for non-32-bit formats.
- `OnClosed` clears the image sources so the backdrop and its render surface
  go immediately rather than whenever the window is collected.

### `Services/ClipboardHistory.cs`
- `ClipItem` gains a small `Thumb`; the HUD binds that instead of the full
  image.
- The full image is now evictable: once written to disk the memory copy is a
  cache, so it can be dropped and re-read. A 1200×20000 scroll capture is
  96 MB; eight of those was 750 MB.
- Capacity is enforced on two axes, a count **and** a 192 MB resident budget.
  The selected item is never evicted. Captures that were never saved are never
  evicted either — for those the memory copy is the only copy.

### `EditorWindow.xaml.cs`
- Undo was 24 full-resolution copies: 760 MB on a 4K image, several GB on a
  tall page capture. Now count-capped **and** byte-budgeted (256 MB undo,
  128 MB redo), always keeping at least one step. On a 1080p capture that is
  still the full 24.
- `OnClosed` releases the history, the displayed image and the
  background-removal panel's pristine copy.

### `MainWindow.xaml.cs` (gallery)
- Thumbnails for items no longer on the visible page are released. Paging
  through 2000 captures used to retain 2000 decoded thumbnails, roughly a
  quarter of a gigabyte, none of them on screen.
- `BitmapCreateOptions.IgnoreImageCache` on gallery decodes. WPF's
  process-wide image cache is keyed by URI and holds a strong reference, so
  without it every capture the gallery ever rendered stayed for the life of
  the process regardless of what this window did with its own references.

### `Services/ClipboardService.cs`
- Publishing an image materialises it three times (DIB, PNG, OLE copy). Above
  64 megapixels — reachable only by a very tall scroll capture — the PNG
  format is skipped; the DIB still pastes everywhere.
- Retry loop instead of one attempt plus a fallback.

### `AdvancedSnip.csproj`
- Workstation + concurrent GC stated explicitly (guards against a machine-wide
  `DOTNET_gcServer=1`, which on a 16-core box can account for hundreds of MB
  by itself).
- `RetainVMGarbageCollection=false` so freed segments go back to the OS.
- `System.GC.ConserveMemory=5`. If captures ever feel slower than they used
  to, this is the first line to try removing.

## Not changed, but worth knowing

- `StitchCanvas.EnsureCapacity` grows 1.5× and copies, so a long scroll
  capture transiently holds the old and new buffers at once. That is inherent
  to a growing canvas; lowering **Scroll max height** in Settings is the lever.
- OCR indexing decodes to uncompressed Bgra8, so four workers on 4K captures
  is ~130 MB in flight. Bounded and released per file, but it is why the
  worker count is small and why indexing is opt-in.

## Build

Written without a .NET SDK available, so this is verified by static analysis
only: brace balance, XAML handler and data-binding resolution, and cross-type
member resolution. Build it and report any compiler errors.
