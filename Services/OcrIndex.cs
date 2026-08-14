using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AdvancedSnip.Services
{
    internal sealed class OcrIndexEntry
    {
        public string Path { get; set; } = "";
        public long   Size { get; set; }
        public long   Ticks { get; set; }      // last write time, for staleness
        public string Text { get; set; } = "";

        /// <summary>
        /// Which recognition pipeline produced this text. See
        /// <see cref="OcrIndex.ReaderVersion"/> — an entry from an older one is discarded
        /// on load rather than trusted.
        /// </summary>
        public int Reader { get; set; }
    }

    internal sealed class OcrIndexProgress
    {
        public int Done { get; init; }
        public int Total { get; init; }
        public bool Finished { get; init; }
        public string? Current { get; init; }

        /// <summary>
        /// How many of <see cref="Total"/> came back with no readable text.
        ///
        /// Reported because "search finds nothing" and "these pictures contain no words"
        /// look identical from the outside, and the difference is the whole diagnosis. A
        /// count on screen turns a complaint into a number.
        /// </summary>
        public int Empty { get; init; }
    }

    /// <summary>
    /// Makes "search inside my screenshots" fast enough to type into.
    ///
    /// Recognising text takes on the order of a hundred milliseconds per image. Doing that
    /// on every keystroke across a folder of thousands is obviously impossible, so the
    /// work is done once, in the background, and cached on disk. Searching then runs over
    /// an in-memory dictionary and is instant.
    ///
    /// Design points that matter:
    ///
    ///  * Indexing is opt-in. It costs real CPU, so it starts only when the user actually
    ///    turns on text search rather than silently churning through their pictures folder.
    ///
    ///  * Newest first. The capture someone is hunting for is far more often from this
    ///    week than from two years ago, so the index becomes useful long before it's
    ///    complete.
    ///
    ///  * Entries are invalidated by size and modification time, so editing a capture
    ///    re-reads it, and the cache never serves text from a picture that no longer
    ///    looks like that.
    ///
    ///  * Bounded concurrency. OCR is CPU-bound; running one task per file would swamp
    ///    the machine the user is trying to work on.
    /// </summary>
    internal sealed class OcrIndex
    {
        private static string CacheDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "AdvancedSnip");
        private static string CachePath => Path.Combine(CacheDir, "ocr-index.json");

        /// <summary>
        /// Bumped whenever recognition changes in a way that could produce different text
        /// for the same picture.
        ///
        /// Without this, improving the reader improves nothing anyone can see. The cache
        /// is invalidated by file size and modification time, which are properties of the
        /// *picture* — a picture that was read badly, or read as nothing at all, has not
        /// changed, so its disappointing result would be served forever by a better
        /// engine that never gets asked.
        ///
        /// 2: images are now upscaled before recognition, which is what makes small text,
        /// digits and punctuation legible to the engine.
        ///
        /// 3: files are read in horizontal bands. Before this, a tall capture was scaled
        /// down whole to fit the engine's size limit and came back with nothing — so every
        /// scroll capture in a folder was cached as "no text" and stayed that way. Without
        /// this bump the fix would be invisible: the entries are keyed on the file, and the
        /// file has not changed.
        /// </summary>
        internal const int ReaderVersion = 3;

        private readonly ConcurrentDictionary<string, OcrIndexEntry> _entries =
            new(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource? _cts;
        private int _done, _total;
        private bool _dirty;

        /// <summary>
        /// What the run currently in flight is already going to read.
        ///
        /// Exists so a gallery reload does not throw away a run that is already doing the
        /// work. See <see cref="IndexAsync"/>.
        /// </summary>
        private readonly HashSet<string> _covered = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Serialises the periodic saves the workers trigger.</summary>
        private readonly object _saveGate = new();

        /// <summary>How many files may finish between one save and the next.</summary>
        private const int SaveEvery = 15;

        internal bool IsIndexing { get; private set; }
        internal int Indexed => _entries.Count;

        internal event EventHandler<OcrIndexProgress>? Progress;

        // ── persistence ───────────────────────────────────────────────────────

        internal void Load()
        {
            try
            {
                if (!File.Exists(CachePath)) return;
                var list = JsonSerializer.Deserialize<List<OcrIndexEntry>>(
                    File.ReadAllText(CachePath));
                if (list == null) return;

                foreach (var e in list)
                    if (!string.IsNullOrEmpty(e.Path) && e.Reader == ReaderVersion)
                        _entries[e.Path] = e;
            }
            catch { /* a corrupt cache just means we index again */ }
        }

        internal void Save()
        {
            if (!_dirty) return;

            // Workers call this mid-run now, so two of them can arrive together. Without
            // the lock they would race on File.WriteAllText and leave a half-written cache
            // — which Load() would then throw away, losing exactly the work being saved.
            lock (_saveGate)
            {
            if (!_dirty) return;
            try
            {
                Directory.CreateDirectory(CacheDir);

                // Drop entries for files that no longer exist, so the cache doesn't grow
                // without bound as captures are deleted.
                var alive = _entries.Values.Where(e => File.Exists(e.Path)).ToList();

                File.WriteAllText(CachePath, JsonSerializer.Serialize(alive));
                _dirty = false;
            }
            catch { }
            }
        }

        internal void Clear()
        {
            _entries.Clear();
            _dirty = true;
            try { if (File.Exists(CachePath)) File.Delete(CachePath); } catch { }
        }

        // ── query ─────────────────────────────────────────────────────────────

        /// <summary>
        /// True when this file's recognised text contains <paramref name="needle"/>.
        /// A file that hasn't been indexed yet simply doesn't match — the caller shows
        /// indexing progress so that reads as "not yet" rather than "not there".
        /// </summary>
        internal bool Matches(string path, string needle)
        {
            if (!_entries.TryGetValue(path, out var e) || e.Text.Length == 0) return false;

            if (e.Text.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;

            // A phrase the engine happened to split across a line does not contain the
            // space the user typed — it contains a newline. Searching for two words that
            // are plainly next to each other in the picture would find nothing, which
            // reads as "the index missed this image" rather than "the line wrapped".
            // Only worth the second pass when the needle spans words.
            if (needle.AsSpan().IndexOfAny(" \t\r\n") < 0) return false;

            return Flatten(e.Text).Contains(Flatten(needle), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Collapses every run of whitespace to one space, so line breaks stop mattering.</summary>
        private static string Flatten(string value)
        {
            var sb = new System.Text.StringBuilder(value.Length);
            bool space = false;

            foreach (char c in value)
            {
                if (char.IsWhiteSpace(c)) { space = true; continue; }
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(c);
            }

            return sb.ToString();
        }

        internal bool HasEntry(string path) => _entries.ContainsKey(path);

        internal string TextFor(string path)
            => _entries.TryGetValue(path, out var e) ? e.Text : "";

        /// <summary>Stores text the editor already recognised, saving a re-read later.</summary>
        internal void Remember(string path, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) Interlocked.Increment(ref _empty);

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return;
                _entries[path] = new OcrIndexEntry
                {
                    Path = path,
                    Size = info.Length,
                    Ticks = info.LastWriteTimeUtc.Ticks,
                    Text = text,
                    Reader = ReaderVersion
                };
                _dirty = true;
            }
            catch { }
        }

        // ── indexing ──────────────────────────────────────────────────────────

        internal void Stop()
        {
            // Flush first. Cancelling discards whatever the workers were part-way through,
            // and there is no reason to also discard what they had already finished.
            try { Save(); } catch { }

            try { _cts?.Cancel(); } catch { }
            _cts?.Dispose();
            _cts = null;
            IsIndexing = false;
            _covered.Clear();
        }

        /// <summary>
        /// Brings the index up to date for the given files, newest first. Safe to call
        /// again while running — the previous pass is cancelled first.
        /// </summary>
        /// <summary>
        /// Brings the index up to date. With <paramref name="force"/> it re-reads every
        /// file whether or not the cache already has an answer for it.
        ///
        /// Force exists because a cached "no text" is indistinguishable from a cached
        /// result — the entry looks complete either way, and nothing short of changing the
        /// file will make the normal pass look at it again. When recognition has improved,
        /// or when a picture is demonstrably readable in the editor and still will not
        /// come up in search, that is the only way for the user to say "read it again".
        /// </summary>
        internal async Task IndexAsync(IReadOnlyList<(string Path, DateTime Captured)> files,
                                       bool force = false)
        {
            if (!OcrService.IsAvailable) return;

            // A forced pass is a deliberate instruction, so it takes over from whatever
            // ordinary pass may be running rather than being coalesced into it.
            if (force) Stop();

            var pending = files
                .Where(f => force || NeedsIndexing(f.Path))
                .OrderByDescending(f => f.Captured)
                .Select(f => f.Path)
                .ToList();

            if (pending.Count == 0)
            {
                if (!IsIndexing) Report(finished: true);
                return;
            }

            // **Do not restart a run that is already doing this work.**
            //
            // This method is reached from every gallery reload — turning a page, changing
            // the sort, typing in the search box, refreshing. It used to begin with Stop(),
            // so each of those cancelled the run and started it again, throwing away
            // whatever the workers were part-way through.
            //
            // Files are read newest first, which means the oldest are read last, which
            // means they are exactly the ones a restart never reaches. Someone paging
            // through their captures looking for the ones that had not been indexed was
            // restarting the run each time they looked — and the last page was the part
            // that never got done. That is the reported symptom, and this is the cause.
            if (IsIndexing && !force)
            {
                bool alreadyCovered = true;
                foreach (var path in pending)
                    if (!_covered.Contains(path)) { alreadyCovered = false; break; }

                if (alreadyCovered) return;      // let it finish
            }

            Stop();

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _covered.Clear();
            foreach (var path in pending) _covered.Add(path);

            _done = 0;
            _empty = 0;
            _total = pending.Count;

            IsIndexing = true;
            Report(finished: false);

            // Leave the machine usable: OCR is CPU-bound and this runs while the user is
            // typing into the search box.
            //
            // The memory arithmetic behind this number changed in v2.25. A worker used to
            // decode a whole image to uncompressed Bgra8 before recognising it, so four
            // workers on 4K captures meant about 130 MB in flight and a tall scroll capture
            // was far worse. Reading in bands caps each worker at roughly one band —
            // 27 MB — however tall the picture is, so the ceiling is now predictable rather
            // than a function of the largest file in the folder.
            //
            // Raised from ProcessorCount/2 to ProcessorCount-1 on that basis, still capped
            // at four. Banding also made each tall capture several recognitions instead of
            // one, so a first pass over a large folder got slower exactly when it got
            // correct; this buys some of that back. Left at four rather than higher because
            // the constraint is now CPU and the user is using the machine.
            int workers = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1));

            try
            {
                await Task.Run(async () =>
                {
                    using var slots = new SemaphoreSlim(workers);
                    var running = new List<Task>();

                    foreach (var path in pending)
                    {
                        if (token.IsCancellationRequested) break;
                        await slots.WaitAsync(token).ConfigureAwait(false);

                        running.Add(Task.Run(async () =>
                        {
                            try
                            {
                                string text = await OcrService
                                    .ReadFileAsync(path, token).ConfigureAwait(false);
                                Remember(path, text);
                            }
                            catch { }
                            finally
                            {
                                slots.Release();
                                int done = Interlocked.Increment(ref _done);
                                if (done % 5 == 0 || done == _total)
                                    Report(finished: false, current: path);

                                // Write the cache out as we go. It used to be saved only
                                // when a run ended, so a close or a crash part-way through
                                // a folder threw away everything read since the last one —
                                // and on a first pass over a large folder that is the whole
                                // session's work. Every fifteen files bounds the loss to
                                // seconds without writing a file per image.
                                if (done % SaveEvery == 0) Save();
                            }
                        }, token));
                    }

                    try { await Task.WhenAll(running).ConfigureAwait(false); }
                    catch { }
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                IsIndexing = false;
                Save();
                Report(finished: true);

                // A long indexing run churns a lot of large short-lived buffers. Compact
                // once at the end rather than leaving the heap fragmented behind it.
                MemoryGuard.RequestTrim();
            }
        }

        private bool NeedsIndexing(string path)
        {
            try
            {
                if (!_entries.TryGetValue(path, out var e)) return true;
                var info = new FileInfo(path);
                if (!info.Exists) return false;
                // Reader first: an entry written by an older pipeline is stale no matter
                // how untouched the picture is. Load() already drops those, so this only
                // matters within a session that has upgraded mid-flight — but the two
                // checks disagreeing is exactly the sort of thing that survives for years.
                return e.Reader != ReaderVersion
                    || e.Size != info.Length
                    || e.Ticks != info.LastWriteTimeUtc.Ticks;
            }
            catch { return false; }
        }

        private int _empty;

        private void Report(bool finished, string? current = null)
            => Progress?.Invoke(this, new OcrIndexProgress
            {
                Done = Volatile.Read(ref _done),
                Total = _total,
                Finished = finished,
                Current = current,
                Empty = Volatile.Read(ref _empty)
            });
    }
}
