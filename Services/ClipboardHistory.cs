using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// A single remembered image plus where it was saved.
    ///
    /// Two things changed here for memory, and both matter more than they look:
    ///
    /// 1. There is now a small <see cref="Thumb"/> alongside the full image, and that is
    ///    what the HUD displays. Binding the full-resolution image into a 110-pixel tall
    ///    element does not make WPF treat it as small.
    ///
    /// 2. The full image is <i>evictable</i>. Once a capture has been written to disk the
    ///    in-memory copy is a cache, not the only copy, so under pressure it can be
    ///    dropped and re-read on demand. A scroll capture of a long page is 1200 x 20000
    ///    pixels — 96 MB uncompressed — and holding eight of those would be 750 MB for a
    ///    feature the user touches once a week. Captures that were never saved (because
    ///    saving is off or failed) are never evicted; for those the memory copy is the
    ///    only copy there is.
    /// </summary>
    public sealed class ClipItem
    {
        private BitmapSource? _image;
        private long _pressure;

        public required BitmapSource Image
        {
            get => _image ?? Reload() ?? EmptyImage;
            init
            {
                _image = value;
                _pressure = MemoryGuard.Track(value);
            }
        }

        /// <summary>Small copy for the HUD. Always resident — it is only a few hundred KB.</summary>
        public BitmapSource? Thumb { get; init; }

        public string? FilePath { get; set; }
        public DateTime Time { get; init; }

        /// <summary>Small label shown under the thumbnail in the history HUD.</summary>
        public string Caption => Time.ToString("HH:mm:ss");

        /// <summary>Roughly what the full image costs while it is resident.</summary>
        /// <summary>
        /// A cheap content hash, computed from the thumbnail and cached.
        ///
        /// Deliberately taken from <see cref="Thumb"/>, not the full image: a thumbnail is
        /// a few tens of kilobytes where the original can be hundreds of megabytes, and
        /// reading the full image would force an evicted item back off disk just to answer
        /// "have I seen this before". <see cref="ImageInterop.MakeThumbnail"/> is
        /// deterministic, so the same picture always produces the same thumb and the same
        /// number.
        /// </summary>
        private long _fingerprint;
        private bool _fingerprinted;

        internal long FingerprintOf()
        {
            if (_fingerprinted) return _fingerprint;
            _fingerprinted = true;
            _fingerprint = Thumb != null ? ImageInterop.Fingerprint(Thumb) : 0;
            return _fingerprint;
        }

        internal long ResidentBytes => _image == null ? 0 : MemoryGuard.BytesOf(_image);

        internal bool IsResident => _image != null;

        /// <summary>True when the full image can be thrown away and read back later.</summary>
        internal bool CanEvict =>
            _image != null && !string.IsNullOrEmpty(FilePath) && File.Exists(FilePath);

        /// <summary>
        /// Explicit accessor for the paths that actually need the full picture — putting
        /// it on the clipboard, pasting it. Named rather than a property so it is obvious
        /// at the call site that this may hit the disk.
        /// </summary>
        public BitmapSource GetFullImage() => Image;

        internal void Evict()
        {
            if (!CanEvict) return;
            _image = null;
            MemoryGuard.Release(_pressure);
            _pressure = 0;
        }

        internal void Drop()
        {
            _image = null;
            MemoryGuard.Release(_pressure);
            _pressure = 0;
        }

        private BitmapSource? Reload()
        {
            if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath)) return null;
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                // IgnoreImageCache keeps this out of WPF's process-wide decoded-image
                // cache, which is keyed by URI and would otherwise hold every capture the
                // app has ever re-read for the lifetime of the process.
                img.CreateOptions = BitmapCreateOptions.IgnoreImageCache
                                  | BitmapCreateOptions.PreservePixelFormat;
                using (var fs = File.OpenRead(FilePath))
                {
                    img.StreamSource = fs;
                    img.EndInit();
                }
                img.Freeze();

                _image = img;
                _pressure = MemoryGuard.Track(img);
                return img;
            }
            catch { return null; }
        }

        /// <summary>Last-resort placeholder so callers never have to null-check.</summary>
        private static readonly BitmapSource EmptyImage = CreateEmpty();

        private static BitmapSource CreateEmpty()
        {
            var bmp = BitmapSource.Create(1, 1, 96, 96,
                System.Windows.Media.PixelFormats.Bgr32, null, new byte[4], 4);
            bmp.Freeze();
            return bmp;
        }
    }

    /// <summary>
    /// Fixed-capacity, most-recent-first image history. Item 0 is always the newest.
    /// A moving "index" tracks which item is currently active so the user can cycle
    /// through them.
    ///
    /// Capacity is now enforced on two axes — a count *and* a byte budget. Count alone is
    /// meaningless when one entry can be a 96 MB scroll capture and the next a 40 KB icon
    /// crop.
    /// </summary>
    public sealed class ClipboardHistory
    {
        /// <summary>
        /// How much full-resolution image data may stay resident. Everything past this is
        /// evicted oldest-first and re-read from disk if the user comes back to it.
        /// Generous enough that ordinary region snips are never touched — twenty 1080p
        /// crops fit inside it — and low enough that a run of scroll captures cannot walk
        /// the process into a gigabyte.
        /// </summary>
        private const long ResidentBudgetBytes = 192L * 1024 * 1024;

        private readonly List<ClipItem> _items = new();
        private int _capacity;
        private int _index = -1;

        public ClipboardHistory(int capacity) => _capacity = Math.Max(1, capacity);

        /// <summary>Raised whenever the contents or the active index change.</summary>
        public event EventHandler? Changed;

        /// <summary>
        /// How many entries the history will hold. Exposed since v2.36 so the gallery can
        /// tell the user *before* it starts adding that a twenty-image selection will not
        /// fit into eight slots, rather than quietly dropping twelve of them out the back
        /// of the ring buffer.
        /// </summary>
        public int Capacity => _capacity;

        public IReadOnlyList<ClipItem> Items => _items;
        public int Index => _index;
        public int Count => _items.Count;

        public ClipItem? Current =>
            _index >= 0 && _index < _items.Count ? _items[_index] : null;

        public void Add(BitmapSource image, string? filePath)
        {
            _items.Insert(0, new ClipItem
            {
                Image = image,
                Thumb = ImageInterop.MakeThumbnail(image),
                FilePath = filePath,
                Time = DateTime.Now
            });
            Trim();
            _index = 0;
            OnChanged();
        }

        /// <summary>
        /// Puts an image at the front of the history without duplicating one already there.
        ///
        /// This is what "copy" means from the gallery or the editor. Those two used to call
        /// <see cref="ClipboardService"/> directly, so the picture reached the system
        /// clipboard but never the HUD — press the history hotkey straight after copying
        /// and the thing you just copied was not in the list. From the outside that reads
        /// as the copy having failed.
        ///
        /// <see cref="Add"/> is wrong for this: copying the same gallery item twice, or
        /// copying something that arrived as a capture a minute ago, would stack identical
        /// thumbnails and push real history off the end of a ring buffer that holds eight.
        /// Matching is by file path where there is one, and otherwise by a fingerprint of
        /// the thumbnail — the editor can copy an unsaved edit, which has no path at all.
        /// </summary>
        public void Promote(BitmapSource image, string? filePath)
        {
            if (image == null) return;

            int found = IndexOfSame(image, filePath);

            if (found >= 0)
            {
                // Already here: move it to the front and select it, rather than adding a
                // second copy. Its original timestamp goes with it — the caption says when
                // the picture was captured, and copying it again does not change that.
                var existing = _items[found];
                _items.RemoveAt(found);
                _items.Insert(0, existing);
                _index = 0;
                OnChanged();
                return;
            }

            Add(image, filePath);
        }

        /// <summary>
        /// Whether a file is already in the history.
        ///
        /// Exists so a caller can tell "moved to the front" from "newly added" without
        /// having to guess from the count — a bulk add reports both, and the two mean
        /// different things to the person who asked for it.
        /// </summary>
        public bool Contains(string? filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;

            for (int i = 0; i < _items.Count; i++)
                if (string.Equals(_items[i].FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }

        /// <summary>Where an equivalent item already sits, or -1.</summary>
        private int IndexOfSame(BitmapSource image, string? filePath)
        {
            if (!string.IsNullOrEmpty(filePath))
            {
                for (int i = 0; i < _items.Count; i++)
                    if (string.Equals(_items[i].FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                        return i;

                // A path that matches nothing is a different picture; no point hashing.
                return -1;
            }

            long print = ImageInterop.Fingerprint(image);
            if (print == 0) return -1;          // could not be read; treat as new

            for (int i = 0; i < _items.Count; i++)
            {
                // Only compare against items that are also path-less. An unsaved editor
                // copy that happens to hash the same as a file on disk is still a
                // different thing to the user — the file one can be re-opened.
                if (!string.IsNullOrEmpty(_items[i].FilePath)) continue;
                if (_items[i].FingerprintOf() == print) return i;
            }

            return -1;
        }

        /// <summary>
        /// Swaps in an edited version of an image already in the history, matched by the
        /// file it was saved to. Without this the HUD would keep offering the unedited
        /// original alongside the edited file on disk — two different pictures under one
        /// name. Falls back to adding it when the file isn't in the history (it may have
        /// been opened from the gallery long after it scrolled off).
        /// </summary>
        public void Replace(string filePath, BitmapSource edited)
        {
            if (string.IsNullOrEmpty(filePath)) return;

            for (int i = 0; i < _items.Count; i++)
            {
                if (!string.Equals(_items[i].FilePath, filePath,
                                   StringComparison.OrdinalIgnoreCase))
                    continue;

                var old = _items[i];
                _items[i] = new ClipItem
                {
                    Image = edited,
                    Thumb = ImageInterop.MakeThumbnail(edited),
                    FilePath = filePath,
                    Time = old.Time            // keep its place in the timeline
                };
                old.Drop();                    // release the superseded copy immediately
                _index = i;
                Trim();
                OnChanged();
                return;
            }

            Add(edited, filePath);
        }

        public ClipItem? MoveNext()
        {
            if (_items.Count == 0) return null;
            _index = (_index + 1) % _items.Count;
            OnChanged();
            return Current;
        }

        public ClipItem? MovePrevious()
        {
            if (_items.Count == 0) return null;
            _index = (_index - 1 + _items.Count) % _items.Count;
            OnChanged();
            return Current;
        }

        public void Select(int i)
        {
            if (i >= 0 && i < _items.Count && i != _index)
            {
                _index = i;
                OnChanged();
            }
        }

        public void SetCapacity(int capacity)
        {
            _capacity = Math.Max(1, capacity);
            Trim();
            if (_index >= _items.Count) _index = _items.Count - 1;
            OnChanged();
        }

        public void Clear()
        {
            foreach (var item in _items) item.Drop();
            _items.Clear();
            _index = -1;
            OnChanged();
            MemoryGuard.RequestTrim();
        }

        /// <summary>
        /// Enforces both limits: drop entries past the count, then evict resident pixels
        /// past the byte budget. Eviction walks oldest-first and always spares the
        /// selected item, because that is the one about to be pasted.
        /// </summary>
        private void Trim()
        {
            while (_items.Count > _capacity)
            {
                _items[^1].Drop();
                _items.RemoveAt(_items.Count - 1);
            }

            long resident = 0;
            for (int i = 0; i < _items.Count; i++) resident += _items[i].ResidentBytes;
            if (resident <= ResidentBudgetBytes) return;

            for (int i = _items.Count - 1; i >= 0 && resident > ResidentBudgetBytes; i--)
            {
                if (i == _index) continue;
                if (!_items[i].CanEvict) continue;

                long freed = _items[i].ResidentBytes;
                _items[i].Evict();
                resident -= freed;
            }

            MemoryGuard.RequestTrim();
        }

        private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
