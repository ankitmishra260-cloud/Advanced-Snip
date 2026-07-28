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
    }

    internal sealed class OcrIndexProgress
    {
        public int Done { get; init; }
        public int Total { get; init; }
        public bool Finished { get; init; }
        public string? Current { get; init; }
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

        private readonly ConcurrentDictionary<string, OcrIndexEntry> _entries =
            new(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource? _cts;
        private int _done, _total;
        private bool _dirty;

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
                    if (!string.IsNullOrEmpty(e.Path))
                        _entries[e.Path] = e;
            }
            catch { /* a corrupt cache just means we index again */ }
        }

        internal void Save()
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
            return e.Text.Contains(needle, StringComparison.OrdinalIgnoreCase);
        }

        internal bool HasEntry(string path) => _entries.ContainsKey(path);

        internal string TextFor(string path)
            => _entries.TryGetValue(path, out var e) ? e.Text : "";

        /// <summary>Stores text the editor already recognised, saving a re-read later.</summary>
        internal void Remember(string path, string text)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return;
                _entries[path] = new OcrIndexEntry
                {
                    Path = path,
                    Size = info.Length,
                    Ticks = info.LastWriteTimeUtc.Ticks,
                    Text = text
                };
                _dirty = true;
            }
            catch { }
        }

        // ── indexing ──────────────────────────────────────────────────────────

        internal void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            _cts?.Dispose();
            _cts = null;
            IsIndexing = false;
        }

        /// <summary>
        /// Brings the index up to date for the given files, newest first. Safe to call
        /// again while running — the previous pass is cancelled first.
        /// </summary>
        internal async Task IndexAsync(IReadOnlyList<(string Path, DateTime Captured)> files)
        {
            Stop();
            if (!OcrService.IsAvailable) return;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            var pending = files
                .Where(f => NeedsIndexing(f.Path))
                .OrderByDescending(f => f.Captured)
                .Select(f => f.Path)
                .ToList();

            _done = 0;
            _total = pending.Count;

            if (_total == 0)
            {
                Report(finished: true);
                return;
            }

            IsIndexing = true;
            Report(finished: false);

            // Leave the machine usable: OCR is CPU-bound and this runs while the user is
            // typing into the search box.
            int workers = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));

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
            }
        }

        private bool NeedsIndexing(string path)
        {
            try
            {
                if (!_entries.TryGetValue(path, out var e)) return true;
                var info = new FileInfo(path);
                if (!info.Exists) return false;
                return e.Size != info.Length || e.Ticks != info.LastWriteTimeUtc.Ticks;
            }
            catch { return false; }
        }

        private void Report(bool finished, string? current = null)
            => Progress?.Invoke(this, new OcrIndexProgress
            {
                Done = Volatile.Read(ref _done),
                Total = _total,
                Finished = finished,
                Current = current
            });
    }
}
