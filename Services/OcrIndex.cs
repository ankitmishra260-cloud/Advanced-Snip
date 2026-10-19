using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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

        [JsonIgnore]
        internal string? NormalizedSearchText { get; set; }

        [JsonIgnore]
        internal HashSet<string>? Tokens { get; set; }
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
    /// High-performance, memory-efficient OCR text index for gallery captures.
    ///
    /// Features:
    ///  * In-memory Inverted Index for sub-millisecond multi-term searches across thousands of images.
    ///  * Multi-term Boolean AND queries ("word1 word2" finds images with both words anywhere in text).
    ///  * Quoted exact phrase matching ("exact phrase").
    ///  * OCR-aware canonical normalization (resolving 0/O, 1/l/I/|, rn/m, vv/w, cl/d confusions).
    ///  * Prefix matching (instant results while typing partial words).
    ///  * Typo / fuzzy tolerance (Damerau-Levenshtein distance <= 1 for longer words).
    ///  * Atomic disk persistence (no cache corruption upon unexpected process termination).
    ///  * Automatic background capture queuing when new snips are saved.
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
        /// </summary>
        internal const int ReaderVersion = 3;

        private readonly ConcurrentDictionary<string, OcrIndexEntry> _entries =
            new(StringComparer.OrdinalIgnoreCase);

        // Inverted indexes mapping tokens to the set of document paths containing them
        private readonly ConcurrentDictionary<string, HashSet<string>> _invertedIndex =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, HashSet<string>> _canonicalIndex =
            new(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource? _cts;
        private int _done, _total;
        private bool _dirty;

        /// <summary>
        /// What the run currently in flight is already going to read.
        /// </summary>
        private readonly HashSet<string> _covered = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Serialises the periodic saves the workers trigger.</summary>
        private readonly object _saveGate = new();

        /// <summary>How many files may finish between one save and the next.</summary>
        private const int SaveEvery = 50;

        // Auto-queue for captures newly saved by the user
        private readonly ConcurrentQueue<string> _autoQueue = new();
        private int _autoWorking;

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
                {
                    if (!string.IsNullOrEmpty(e.Path) && e.Reader == ReaderVersion)
                    {
                        IndexEntryTokens(e);
                        _entries[e.Path] = e;
                    }
                }
            }
            catch { /* a corrupt cache just means we index again */ }
        }

        internal void Save(bool pruneMissing = false)
        {
            if (!_dirty) return;

            lock (_saveGate)
            {
                if (!_dirty) return;
                try
                {
                    Directory.CreateDirectory(CacheDir);
                    string tmpPath = CachePath + ".tmp";

                    List<OcrIndexEntry> list;
                    if (pruneMissing)
                    {
                        // Clean up dead entries only when explicitly requested (e.g. at end of a run)
                        var dead = _entries.Keys.Where(p => !File.Exists(p)).ToList();
                        foreach (var dp in dead)
                        {
                            if (_entries.TryRemove(dp, out var removed))
                                RemoveEntryTokens(removed);
                        }
                        list = _entries.Values.ToList();
                    }
                    else
                    {
                        list = _entries.Values.ToList();
                    }

                    File.WriteAllText(tmpPath, JsonSerializer.Serialize(list));
                    File.Move(tmpPath, CachePath, overwrite: true);
                    _dirty = false;
                }
                catch { }
            }
        }

        internal void Clear()
        {
            _entries.Clear();
            _invertedIndex.Clear();
            _canonicalIndex.Clear();
            _dirty = true;
            try { if (File.Exists(CachePath)) File.Delete(CachePath); } catch { }
        }

        // ── tokenization & indexing ──────────────────────────────────────────

        private void IndexEntryTokens(OcrIndexEntry entry)
        {
            var rawTokens = ExtractTokens(entry.Text);
            var tokenSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var t in rawTokens)
            {
                tokenSet.Add(t);
                AddToInvertedIndex(_invertedIndex, t, entry.Path);

                string canon = ToOcrCanonical(t);
                if (!string.Equals(canon, t, StringComparison.OrdinalIgnoreCase))
                {
                    AddToInvertedIndex(_canonicalIndex, canon, entry.Path);
                }
            }

            entry.Tokens = tokenSet;
            entry.NormalizedSearchText = " " + string.Join(" ", rawTokens) + " ";
        }

        private void RemoveEntryTokens(OcrIndexEntry entry)
        {
            if (entry.Tokens == null) return;
            foreach (var t in entry.Tokens)
            {
                RemoveFromInvertedIndex(_invertedIndex, t, entry.Path);
                string canon = ToOcrCanonical(t);
                if (!string.Equals(canon, t, StringComparison.OrdinalIgnoreCase))
                    RemoveFromInvertedIndex(_canonicalIndex, canon, entry.Path);
            }
        }

        private static void AddToInvertedIndex(
            ConcurrentDictionary<string, HashSet<string>> dict, string token, string path)
        {
            dict.AddOrUpdate(token,
                _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path },
                (_, set) => { lock (set) { set.Add(path); } return set; });
        }

        private static void RemoveFromInvertedIndex(
            ConcurrentDictionary<string, HashSet<string>> dict, string token, string path)
        {
            if (dict.TryGetValue(token, out var set))
            {
                lock (set) { set.Remove(path); }
            }
        }

        private static List<string> ExtractTokens(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return list;

            int start = -1;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsLetterOrDigit(c))
                {
                    if (start < 0) start = i;
                }
                else
                {
                    if (start >= 0)
                    {
                        list.Add(text.Substring(start, i - start).ToLowerInvariant());
                        start = -1;
                    }
                }
            }

            if (start >= 0)
                list.Add(text.Substring(start, text.Length - start).ToLowerInvariant());

            return list;
        }

        /// <summary>
        /// Normalizes common OCR confusions so searches match even if the engine read
        /// '0' for 'O', '1' for 'l'/'I', 'rn' for 'm', 'vv' for 'w', or 'cl' for 'd'.
        /// </summary>
        private static string ToOcrCanonical(string token)
        {
            if (string.IsNullOrEmpty(token)) return token;

            var sb = new System.Text.StringBuilder(token.Length);
            for (int i = 0; i < token.Length; i++)
            {
                char c = token[i];

                if (c == 'r' && i + 1 < token.Length && token[i + 1] == 'n')
                {
                    sb.Append('m');
                    i++;
                    continue;
                }
                if (c == 'v' && i + 1 < token.Length && token[i + 1] == 'v')
                {
                    sb.Append('w');
                    i++;
                    continue;
                }
                if (c == 'c' && i + 1 < token.Length && token[i + 1] == 'l')
                {
                    sb.Append('d');
                    i++;
                    continue;
                }

                switch (c)
                {
                    case '0':
                        sb.Append('o');
                        break;
                    case '1':
                    case 'l':
                    case 'i':
                    case '|':
                        sb.Append('i');
                        break;
                    default:
                        sb.Append(char.ToLowerInvariant(c));
                        break;
                }
            }

            return sb.ToString();
        }

        // ── query engine ──────────────────────────────────────────────────────

        /// <summary>
        /// Finds all image paths that match the given query string.
        /// Supports multi-term Boolean AND, quoted exact phrases ("exact phrase"),
        /// prefix matching, OCR confusable aliases, and typo tolerance.
        /// </summary>
        internal HashSet<string> FindMatchingPaths(string query)
        {
            var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(query)) return empty;

            var (phrases, terms) = ParseQuery(query);
            if (phrases.Count == 0 && terms.Count == 0) return empty;

            HashSet<string>? currentMatches = null;

            // 1. Process unquoted search terms (Boolean AND)
            foreach (var term in terms)
            {
                var termMatches = FindTermMatches(term);
                if (termMatches.Count == 0) return empty; // AND requires every term to match

                if (currentMatches == null)
                    currentMatches = termMatches;
                else
                {
                    currentMatches.IntersectWith(termMatches);
                    if (currentMatches.Count == 0) return empty;
                }
            }

            // 2. Process quoted phrases (exact sequential phrase matching)
            if (phrases.Count > 0)
            {
                currentMatches ??= new HashSet<string>(_entries.Keys, StringComparer.OrdinalIgnoreCase);

                currentMatches.RemoveWhere(path =>
                {
                    if (!_entries.TryGetValue(path, out var e) || e.NormalizedSearchText == null)
                        return true;

                    foreach (var phrase in phrases)
                    {
                        string nPhrase = " " + string.Join(" ", ExtractTokens(phrase)) + " ";
                        if (!e.NormalizedSearchText.Contains(nPhrase, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    return false;
                });
            }

            return currentMatches ?? empty;
        }

        private HashSet<string> FindTermMatches(string term)
        {
            var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string lowerTerm = term.ToLowerInvariant();

            // 1. Exact token match via inverted index
            if (_invertedIndex.TryGetValue(lowerTerm, out var exactSet))
            {
                lock (exactSet) { matches.UnionWith(exactSet); }
            }

            // 2. Canonical OCR confusable match
            string canon = ToOcrCanonical(lowerTerm);
            if (!string.Equals(canon, lowerTerm, StringComparison.OrdinalIgnoreCase))
            {
                if (_invertedIndex.TryGetValue(canon, out var invCanonSet))
                {
                    lock (invCanonSet) { matches.UnionWith(invCanonSet); }
                }
            }
            if (_canonicalIndex.TryGetValue(canon, out var canonSet))
            {
                lock (canonSet) { matches.UnionWith(canonSet); }
            }

            // 3. Prefix match (e.g. typing "sett" finds "settings")
            foreach (var kvp in _invertedIndex)
            {
                if (kvp.Key.StartsWith(lowerTerm, StringComparison.OrdinalIgnoreCase))
                {
                    lock (kvp.Value) { matches.UnionWith(kvp.Value); }
                }
            }

            // 4. Fuzzy / typo tolerance (edit distance <= 1 for terms of length >= 4)
            if (matches.Count == 0 && lowerTerm.Length >= 4)
            {
                foreach (var kvp in _invertedIndex)
                {
                    if (Math.Abs(kvp.Key.Length - lowerTerm.Length) <= 1 &&
                        LevenshteinDistance(lowerTerm, kvp.Key) <= 1)
                    {
                        lock (kvp.Value) { matches.UnionWith(kvp.Value); }
                    }
                }
            }

            // 5. Substring fallback (for codes or special tokens)
            if (matches.Count == 0)
            {
                foreach (var e in _entries.Values)
                {
                    if (e.NormalizedSearchText != null &&
                        e.NormalizedSearchText.Contains(lowerTerm, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(e.Path);
                    }
                    else if (e.Text.Contains(term, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(e.Path);
                    }
                }
            }

            return matches;
        }

        private static (List<string> Phrases, List<string> Terms) ParseQuery(string query)
        {
            var phrases = new List<string>();
            var terms = new List<string>();

            int i = 0;
            while (i < query.Length)
            {
                if (char.IsWhiteSpace(query[i])) { i++; continue; }

                if (query[i] == '"')
                {
                    int close = query.IndexOf('"', i + 1);
                    if (close > i + 1)
                    {
                        phrases.Add(query.Substring(i + 1, close - i - 1).Trim());
                        i = close + 1;
                        continue;
                    }
                }

                int start = i;
                while (i < query.Length && !char.IsWhiteSpace(query[i]) && query[i] != '"') i++;
                string word = query.Substring(start, i - start).Trim();
                if (word.Length > 0) terms.Add(word);
            }

            return (phrases, terms);
        }

        private static int LevenshteinDistance(string s, string t)
        {
            int n = s.Length, m = t.Length;
            if (n == 0) return m;
            if (m == 0) return n;

            var d = new int[n + 1, m + 1];
            for (int i = 0; i <= n; i++) d[i, 0] = i;
            for (int j = 0; j <= m; j++) d[0, j] = j;

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (s[i - 1] == t[j - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);

                    // Damerau transposition
                    if (i > 1 && j > 1 && s[i - 1] == t[j - 2] && s[i - 2] == t[j - 1])
                    {
                        d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + cost);
                    }
                }
            }

            return d[n, m];
        }

        /// <summary>
        /// True when this file's recognised text matches the query.
        /// </summary>
        internal bool Matches(string path, string needle)
        {
            if (!_entries.TryGetValue(path, out var e) || e.Text.Length == 0) return false;
            var matches = FindMatchingPaths(needle);
            return matches.Contains(path);
        }

        internal bool HasEntry(string path) => _entries.ContainsKey(path);

        internal string TextFor(string path)
            => _entries.TryGetValue(path, out var e) ? e.Text : "";

        /// <summary>Stores text already recognised, updating both the cache and inverted indexes.</summary>
        internal void Remember(string path, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) Interlocked.Increment(ref _empty);

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return;

                if (_entries.TryGetValue(path, out var existing))
                    RemoveEntryTokens(existing);

                var entry = new OcrIndexEntry
                {
                    Path = path,
                    Size = info.Length,
                    Ticks = info.LastWriteTimeUtc.Ticks,
                    Text = text,
                    Reader = ReaderVersion
                };

                IndexEntryTokens(entry);
                _entries[path] = entry;
                _dirty = true;
            }
            catch { }
        }

        // ── auto-ingestion ────────────────────────────────────────────────────

        /// <summary>
        /// Enqueues a newly created or modified capture to be indexed automatically in the background.
        /// </summary>
        internal void EnqueueCapture(string path)
        {
            if (!OcrService.IsAvailable || string.IsNullOrEmpty(path)) return;
            _autoQueue.Enqueue(path);
            _ = ProcessAutoQueueAsync();
        }

        private async Task ProcessAutoQueueAsync()
        {
            if (Interlocked.CompareExchange(ref _autoWorking, 1, 0) != 0) return;

            try
            {
                while (_autoQueue.TryDequeue(out var path))
                {
                    if (IsIndexing) continue; // Ongoing bulk run will cover files
                    if (!File.Exists(path)) continue;
                    if (!NeedsIndexing(path)) continue;

                    try
                    {
                        string text = await OcrService.ReadFileAsync(path).ConfigureAwait(false);
                        Remember(path, text);
                        Save();
                    }
                    catch { }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _autoWorking, 0);
            }
        }

        // ── indexing ──────────────────────────────────────────────────────────

        internal void Stop()
        {
            try { Save(); } catch { }

            try { _cts?.Cancel(); } catch { }
            _cts?.Dispose();
            _cts = null;
            IsIndexing = false;
            _covered.Clear();
        }

        internal async Task IndexAsync(IReadOnlyList<(string Path, DateTime Captured)> files,
                                       bool force = false)
        {
            if (!OcrService.IsAvailable) return;

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

            if (IsIndexing && !force)
            {
                bool alreadyCovered = true;
                foreach (var path in pending)
                    if (!_covered.Contains(path)) { alreadyCovered = false; break; }

                if (alreadyCovered) return;
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

                                if (done % SaveEvery == 0) Save(pruneMissing: false);
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
                Save(pruneMissing: true);
                Report(finished: true);
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
