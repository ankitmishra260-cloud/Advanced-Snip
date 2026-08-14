using System;
using System.Collections.Generic;
using System.Linq;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// One kept piece of the source recording.
    ///
    /// An edit is a list of these rather than a list of deletions, which is the decision
    /// that makes everything else in the editor simple. Cutting a middle section out is
    /// splitting one segment into two; deleting a bad take is removing a segment;
    /// reordering is moving one in the list. Every operation is a list operation, and the
    /// source file is never touched.
    /// </summary>
    public sealed class VideoSegment
    {
        /// <summary>Where this piece starts in the ORIGINAL recording.</summary>
        public TimeSpan Start { get; set; }

        /// <summary>Where it ends in the original.</summary>
        public TimeSpan End { get; set; }

        /// <summary>
        /// Playback rate. 2.0 plays this piece twice as fast and it takes half as long in
        /// the finished file. Applied by re-timestamping frames rather than by dropping
        /// them, so slowing down produces smooth motion rather than a stutter.
        /// </summary>
        public double Speed { get; set; } = 1.0;

        /// <summary>Silence this piece without cutting it.</summary>
        public bool Muted { get; set; }

        public TimeSpan SourceDuration => End > Start ? End - Start : TimeSpan.Zero;

        /// <summary>How long this piece lasts in the finished file, after speed.</summary>
        public TimeSpan OutputDuration =>
            TimeSpan.FromTicks((long)(SourceDuration.Ticks / Math.Clamp(Speed, 0.1, 8.0)));

        public VideoSegment Clone() => new()
        {
            Start = Start,
            End = End,
            Speed = Speed,
            Muted = Muted
        };
    }

    /// <summary>
    /// The edit being assembled over a recording: which parts are kept, in what order, at
    /// what speed.
    ///
    /// **Nothing here touches the file.** The project is arithmetic over timestamps; the
    /// export in <see cref="VideoTrimmer"/> is the only thing that reads or writes pixels.
    /// That split is what makes the editor feel instant — every operation below is a list
    /// edit measured in microseconds, and the one slow thing happens once, when the user
    /// asks for it.
    ///
    /// **Undo is whole snapshots.** The same reasoning as the image editor's undo stack:
    /// a project is a few dozen small structs, so copying it is free, and a replayed
    /// command can never land differently than it did the first time.
    /// </summary>
    public sealed class VideoProject
    {
        private const int MaxUndo = 60;

        private readonly List<List<VideoSegment>> _undo = new();
        private readonly List<List<VideoSegment>> _redo = new();

        public List<VideoSegment> Segments { get; private set; } = new();

        /// <summary>Length of the untouched recording.</summary>
        public TimeSpan SourceDuration { get; }

        public VideoProject(TimeSpan sourceDuration)
        {
            SourceDuration = sourceDuration;
            Segments.Add(new VideoSegment { Start = TimeSpan.Zero, End = sourceDuration });
        }

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        /// <summary>True when the edit would produce something different from the source.</summary>
        public bool IsEdited =>
            Segments.Count != 1
            || Segments[0].Start > TimeSpan.FromMilliseconds(80)
            || Segments[0].End < SourceDuration - TimeSpan.FromMilliseconds(80)
            || Math.Abs(Segments[0].Speed - 1.0) > 0.001
            || Segments[0].Muted;

        /// <summary>Length of the finished file.</summary>
        public TimeSpan OutputDuration
        {
            get
            {
                long ticks = 0;
                foreach (var s in Segments) ticks += s.OutputDuration.Ticks;
                return TimeSpan.FromTicks(ticks);
            }
        }

        // ── history ───────────────────────────────────────────────────────────

        private void Snapshot()
        {
            _undo.Add(Segments.Select(s => s.Clone()).ToList());
            if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
            _redo.Clear();
        }

        public bool Undo()
        {
            if (_undo.Count == 0) return false;
            _redo.Add(Segments.Select(s => s.Clone()).ToList());
            Segments = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            return true;
        }

        public bool Redo()
        {
            if (_redo.Count == 0) return false;
            _undo.Add(Segments.Select(s => s.Clone()).ToList());
            Segments = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            return true;
        }

        public void Reset()
        {
            Snapshot();
            Segments = new List<VideoSegment>
            {
                new() { Start = TimeSpan.Zero, End = SourceDuration }
            };
        }

        // ── editing ───────────────────────────────────────────────────────────

        /// <summary>
        /// Splits the segment containing <paramref name="sourceTime"/> into two.
        ///
        /// Split is the primitive everything else is built from — cutting a section out is
        /// two splits and a delete — so it is worth having exactly one implementation of.
        /// Returns false when the position falls on a boundary or outside every segment,
        /// where a split would produce a zero-length piece.
        /// </summary>
        public bool SplitAt(TimeSpan sourceTime)
        {
            int index = IndexOfSourceTime(sourceTime);
            if (index < 0) return false;

            var segment = Segments[index];

            // A split within a tenth of a second of an edge produces a sliver nobody
            // wanted and that no encoder will render usefully.
            var guard = TimeSpan.FromMilliseconds(100);
            if (sourceTime - segment.Start < guard || segment.End - sourceTime < guard)
                return false;

            Snapshot();

            var tail = segment.Clone();
            tail.Start = sourceTime;
            Segments[index].End = sourceTime;
            Segments.Insert(index + 1, tail);
            return true;
        }

        /// <summary>
        /// Removes the range between two source times, closing the gap.
        ///
        /// This is the one people reach for: "take out the bit where I fumbled". It is
        /// implemented over the segment list rather than as a special case, so a removal
        /// that spans several already-cut pieces behaves the same as one inside a single
        /// piece.
        /// </summary>
        public bool RemoveRange(TimeSpan from, TimeSpan to)
        {
            if (to <= from) return false;

            Snapshot();

            var result = new List<VideoSegment>();

            foreach (var s in Segments)
            {
                // Entirely outside the removal — keep as is.
                if (s.End <= from || s.Start >= to) { result.Add(s); continue; }

                // Entirely inside — drop it.
                if (s.Start >= from && s.End <= to) continue;

                // The removal takes a bite out of the middle: two pieces survive.
                if (s.Start < from && s.End > to)
                {
                    var head = s.Clone(); head.End = from;
                    var tail = s.Clone(); tail.Start = to;
                    result.Add(head);
                    result.Add(tail);
                    continue;
                }

                // Overlaps one end only.
                var trimmed = s.Clone();
                if (s.Start < from) trimmed.End = from;
                else trimmed.Start = to;
                result.Add(trimmed);
            }

            if (result.Count == 0)
            {
                // Removing everything would leave nothing to export. Undo the snapshot so
                // the user is not left with an empty project and a spent undo step.
                _undo.RemoveAt(_undo.Count - 1);
                return false;
            }

            Segments = result;
            return true;
        }

        public bool RemoveSegment(int index)
        {
            if (index < 0 || index >= Segments.Count || Segments.Count == 1) return false;
            Snapshot();
            Segments.RemoveAt(index);
            return true;
        }

        /// <summary>Moves a segment earlier or later in the finished file.</summary>
        public bool MoveSegment(int index, int delta)
        {
            int target = index + delta;
            if (index < 0 || index >= Segments.Count) return false;
            if (target < 0 || target >= Segments.Count) return false;

            Snapshot();
            var s = Segments[index];
            Segments.RemoveAt(index);
            Segments.Insert(target, s);
            return true;
        }

        public bool SetSpeed(int index, double speed)
        {
            if (index < 0 || index >= Segments.Count) return false;
            speed = Math.Clamp(speed, 0.25, 4.0);
            if (Math.Abs(Segments[index].Speed - speed) < 0.001) return false;

            Snapshot();
            Segments[index].Speed = speed;
            return true;
        }

        public bool SetMuted(int index, bool muted)
        {
            if (index < 0 || index >= Segments.Count) return false;
            if (Segments[index].Muted == muted) return false;

            Snapshot();
            Segments[index].Muted = muted;
            return true;
        }

        /// <summary>Trims the whole edit to one range, discarding everything outside it.</summary>
        public bool KeepOnly(TimeSpan from, TimeSpan to)
        {
            if (to <= from) return false;

            Snapshot();
            var result = new List<VideoSegment>();

            foreach (var s in Segments)
            {
                if (s.End <= from || s.Start >= to) continue;

                var kept = s.Clone();
                if (kept.Start < from) kept.Start = from;
                if (kept.End > to) kept.End = to;
                if (kept.End > kept.Start) result.Add(kept);
            }

            if (result.Count == 0) { _undo.RemoveAt(_undo.Count - 1); return false; }

            Segments = result;
            return true;
        }

        // ── mapping between the two timelines ─────────────────────────────────

        /// <summary>Which segment a source position falls in, or -1.</summary>
        public int IndexOfSourceTime(TimeSpan sourceTime)
        {
            for (int i = 0; i < Segments.Count; i++)
                if (sourceTime >= Segments[i].Start && sourceTime <= Segments[i].End) return i;
            return -1;
        }

        /// <summary>
        /// Where a source position lands in the finished file, or null when that moment was
        /// cut out. Used to move the playhead when the user clicks the source timeline.
        /// </summary>
        public TimeSpan? SourceToOutput(TimeSpan sourceTime)
        {
            long ticks = 0;
            foreach (var s in Segments)
            {
                if (sourceTime >= s.Start && sourceTime <= s.End)
                {
                    long into = (sourceTime - s.Start).Ticks;
                    return TimeSpan.FromTicks(ticks + (long)(into / Math.Clamp(s.Speed, 0.1, 8.0)));
                }
                ticks += s.OutputDuration.Ticks;
            }
            return null;
        }

        /// <summary>
        /// Where an output position reads from in the source. This is what drives preview
        /// playback: the player seeks the original file, and this says where to.
        /// </summary>
        public TimeSpan OutputToSource(TimeSpan outputTime)
        {
            long remaining = outputTime.Ticks;

            foreach (var s in Segments)
            {
                long length = s.OutputDuration.Ticks;
                if (remaining <= length)
                    return s.Start + TimeSpan.FromTicks(
                        (long)(remaining * Math.Clamp(s.Speed, 0.1, 8.0)));
                remaining -= length;
            }

            return Segments.Count > 0 ? Segments[^1].End : TimeSpan.Zero;
        }

        /// <summary>The segment playing at a given output position, or -1 past the end.</summary>
        public int SegmentAtOutput(TimeSpan outputTime)
        {
            long remaining = outputTime.Ticks;
            for (int i = 0; i < Segments.Count; i++)
            {
                long length = Segments[i].OutputDuration.Ticks;
                if (remaining <= length) return i;
                remaining -= length;
            }
            return -1;
        }

        /// <summary>Where a segment begins in the finished file.</summary>
        public TimeSpan OutputStartOf(int index)
        {
            long ticks = 0;
            for (int i = 0; i < index && i < Segments.Count; i++)
                ticks += Segments[i].OutputDuration.Ticks;
            return TimeSpan.FromTicks(ticks);
        }
    }
}
