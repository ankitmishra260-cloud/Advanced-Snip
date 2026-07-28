using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace AdvancedSnip.Services
{
    /// <summary>A single remembered image plus where it was saved.</summary>
    public sealed class ClipItem
    {
        public required BitmapSource Image { get; init; }
        public string? FilePath { get; init; }
        public DateTime Time { get; init; }

        /// <summary>Small label shown under the thumbnail in the history HUD.</summary>
        public string Caption => Time.ToString("HH:mm:ss");
    }

    /// <summary>
    /// Fixed-capacity, most-recent-first image history. Item 0 is always the newest.
    /// A moving "index" tracks which item is currently active so the user can cycle
    /// through them.
    /// </summary>
    public sealed class ClipboardHistory
    {
        private readonly List<ClipItem> _items = new();
        private int _capacity;
        private int _index = -1;

        public ClipboardHistory(int capacity) => _capacity = Math.Max(1, capacity);

        /// <summary>Raised whenever the contents or the active index change.</summary>
        public event EventHandler? Changed;

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
                FilePath = filePath,
                Time = DateTime.Now
            });
            Trim();
            _index = 0;
            OnChanged();
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

                _items[i] = new ClipItem
                {
                    Image = edited,
                    FilePath = filePath,
                    Time = _items[i].Time      // keep its place in the timeline
                };
                _index = i;
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
            _items.Clear();
            _index = -1;
            OnChanged();
        }

        private void Trim()
        {
            while (_items.Count > _capacity)
                _items.RemoveAt(_items.Count - 1);
        }

        private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
