using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
// NOT `using System.Windows.Shapes` — it defines Path, which collides with
// System.IO.Path, and this file is full of file-path calls. The two shapes it
// would supply are written out in full instead.
using System.Windows.Threading;
using AdvancedSnip.Services;

namespace AdvancedSnip
{
    /// <summary>
    /// Plays back and edits a finished recording.
    ///
    /// Playback is <see cref="System.Windows.Controls.MediaElement"/>, which is the Windows
    /// media stack — the same components that decoded the file in any other player. Nothing
    /// is bundled and nothing is installed, which is the same reasoning behind the
    /// hand-written ICO and PDF writers elsewhere in this project.
    ///
    /// **The edit is a list of kept pieces, and nothing touches the file until export.**
    /// <see cref="VideoProject"/> is arithmetic over timestamps: splitting, deleting a
    /// range, reordering, per-clip speed and mute are all list operations that complete
    /// instantly. Preview plays the original file and seeks across the cuts as it goes, so
    /// what you watch is what you will get without waiting for a render. That is the whole
    /// design — an editor that re-encoded on every change would be unusable, and one that
    /// previewed something different from what it exports would be worse.
    ///
    /// **Export is non-destructive.** It writes a new file next to the original and leaves
    /// the original alone. Someone reviewing a recording they just made has exactly one
    /// copy of it, and an edit that overwrote in place would be one mis-placed marker away
    /// from losing the take.
    /// </summary>
    public partial class VideoReviewWindow : Window
    {
        /// <summary>
        /// How often the playhead is checked. Small enough that a cut lands within a frame
        /// or two at 30 fps, large enough not to spend a core on it.
        /// </summary>
        private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(40);

        private readonly DispatcherTimer _tick;

        private string _path = "";
        private TimeSpan _duration;
        private VideoProject? _project;

        /// <summary>Where the playhead is on the EDITED timeline.</summary>
        private TimeSpan _output;

        /// <summary>Selection markers, in source time. Equal means no selection.</summary>
        private TimeSpan _in, _out;

        private int _selected = -1;
        private bool _playing;
        private bool _scrubbing;
        private bool _suppressScrub;
        private bool _populating;
        private bool _opened;
        private bool _seeking;
        private double _previewSpeed = 1.0;
        private CancellationTokenSource? _trimCancel;

        public event EventHandler<string>? Deleted;

        public VideoReviewWindow(string path)
        {
            InitializeComponent();

            _tick = new DispatcherTimer { Interval = TickInterval };
            _tick.Tick += (_, _) => OnTick();

            // Was a hand-rolled SetTitleBarDark on Loaded, which fires after the window is
            // already on screen — long enough to see a light title bar flash in a dark
            // theme. ApplyToWindow defers to SourceInitialized instead, and is the one
            // place that knows how to do this.
            ThemeManager.ApplyToWindow(this);

            Load(path);
        }

        /// <summary>Points the window at a recording, replacing whatever it was showing.</summary>
        public void Load(string path)
        {
            _path = path;
            _opened = false;
            _duration = TimeSpan.Zero;
            _project = null;
            _output = TimeSpan.Zero;
            _in = _out = TimeSpan.Zero;
            _selected = -1;

            Title = Path.GetFileName(path);
            FileText.Text = path;
            StatusText.Text = "";
            PlayerMessage.Visibility = Visibility.Collapsed;

            Stop();
            Player.Source = new Uri(path);
            // Play-then-pause is what forces MediaElement to decode and show the first
            // frame. Setting Source alone leaves the surface black until playback starts.
            Player.Play();
            Player.Pause();

            RedrawTimeline();
            UpdateEditUi();
        }

        // ── playback ──────────────────────────────────────────────────────────

        private void Player_Opened(object sender, RoutedEventArgs e)
        {
            _opened = true;
            _duration = Player.NaturalDuration.HasTimeSpan
                ? Player.NaturalDuration.TimeSpan
                : TimeSpan.Zero;

            _project = new VideoProject(_duration);
            _output = TimeSpan.Zero;
            _in = _out = TimeSpan.Zero;
            _selected = _project.Segments.Count > 0 ? 0 : -1;

            _suppressScrub = true;
            Scrubber.Maximum = Math.Max(0.001, _project.OutputDuration.TotalSeconds);
            Scrubber.Value = 0;
            _suppressScrub = false;

            DurationText.Text = Format(_duration);
            RedrawTimeline();
            UpdateEditUi();
        }

        private void Player_Failed(object sender, ExceptionRoutedEventArgs e)
        {
            _opened = false;
            PlayerMessage.Text = "Windows could not play this file. The recording is still on "
                               + "disk — the codecs needed to play it back may be missing on "
                               + "this edition of Windows.";
            PlayerMessage.Visibility = Visibility.Visible;
            TrimBtn.IsEnabled = false;
        }

        private void Player_Ended(object sender, RoutedEventArgs e)
        {
            Pause();
            SeekOutput(TimeSpan.Zero);
        }

        private void Player_Click(object sender, MouseButtonEventArgs e) => TogglePlay();

        private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

        private void TogglePlay()
        {
            if (!_opened) return;
            if (_playing) Pause(); else Start();
        }

        private void Start()
        {
            if (_project == null) return;

            // Playing from past the end of the edit would run into material the user cut,
            // which makes the timeline feel like decoration.
            if (_output >= _project.OutputDuration) SeekOutput(TimeSpan.Zero);

            ApplyPlaybackRate();
            Player.Play();
            _playing = true;
            PlayBtn.Content = "Pause";
            _tick.Start();
        }

        private void Pause()
        {
            Player.Pause();
            _playing = false;
            PlayBtn.Content = "Play";
            _tick.Stop();
        }

        private void Stop()
        {
            try { Player.Stop(); } catch { }
            _playing = false;
            PlayBtn.Content = "Play";
            _tick.Stop();
        }

        /// <summary>
        /// Preview speed times the current clip's own speed.
        ///
        /// A clip marked 2x has to *look* 2x while previewing or the editor is showing
        /// something the export will not produce. MediaElement's SpeedRatio does that for
        /// free, so preview and export agree without rendering anything.
        /// </summary>
        private void ApplyPlaybackRate()
        {
            double segmentSpeed = 1.0;
            if (_project != null)
            {
                int index = _project.SegmentAtOutput(_output);
                if (index >= 0) segmentSpeed = _project.Segments[index].Speed;
            }

            try { Player.SpeedRatio = Math.Clamp(_previewSpeed * segmentSpeed, 0.1, 8.0); }
            catch { }
        }

        /// <summary>
        /// Advances the edited playhead and jumps the player across cuts.
        ///
        /// This is what makes preview honest. The player is walking the original file; when
        /// it reaches the end of a kept piece it has to be moved to wherever the next piece
        /// starts, which may be earlier in the source, later, or in another part of the
        /// recording entirely.
        /// </summary>
        private void OnTick()
        {
            if (_scrubbing || _project == null || _seeking) return;

            var sourcePos = Player.Position;
            int index = _project.SegmentAtOutput(_output);

            if (index < 0)
            {
                Pause();
                SyncUi();
                return;
            }

            var segment = _project.Segments[index];

            // Past the end of this piece — move to the next one.
            if (sourcePos >= segment.End || sourcePos < segment.Start - TimeSpan.FromSeconds(1))
            {
                if (index + 1 >= _project.Segments.Count)
                {
                    Pause();
                    _output = _project.OutputDuration;
                    SyncUi();
                    return;
                }

                _output = _project.OutputStartOf(index + 1);
                SeekPlayer(_project.Segments[index + 1].Start);
                ApplyPlaybackRate();
                SyncUi();
                return;
            }

            // Inside the piece: the edited position is where we started plus how far in we
            // are, compressed by this piece's speed.
            long into = (long)((sourcePos - segment.Start).Ticks / Math.Clamp(segment.Speed, 0.1, 8.0));
            _output = _project.OutputStartOf(index) + TimeSpan.FromTicks(into);

            if (_selected != index && !_playing) _selected = index;
            SyncUi();
        }

        private void SeekPlayer(TimeSpan sourceTime)
        {
            // MediaElement.Position is a request, not an assignment — setting it while a
            // previous seek is still settling makes the picture stutter. The flag keeps the
            // tick from reacting to a position that is mid-flight.
            _seeking = true;
            try { Player.Position = sourceTime; }
            finally { _seeking = false; }
        }

        /// <summary>Moves the playhead to a position on the EDITED timeline.</summary>
        private void SeekOutput(TimeSpan output)
        {
            if (_project == null) return;

            _output = Clamp(output, TimeSpan.Zero, _project.OutputDuration);
            SeekPlayer(_project.OutputToSource(_output));
            ApplyPlaybackRate();

            int index = _project.SegmentAtOutput(_output);
            if (index >= 0) _selected = index;

            SyncUi();
            UpdateEditUi();
        }

        private void SyncUi()
        {
            if (_project == null) return;

            PositionText.Text = Format(_output);
            DurationText.Text = Format(_project.OutputDuration);

            _suppressScrub = true;
            Scrubber.Maximum = Math.Max(0.001, _project.OutputDuration.TotalSeconds);
            Scrubber.Value = Math.Clamp(_output.TotalSeconds, 0, Scrubber.Maximum);
            _suppressScrub = false;

            int index = _project.SegmentAtOutput(_output);
            if (index >= 0 && _project.Segments.Count > 1)
            {
                var s = _project.Segments[index];
                SegmentChip.Visibility = Visibility.Visible;
                SegmentChipText.Text = $"Clip {index + 1} of {_project.Segments.Count}"
                    + (Math.Abs(s.Speed - 1) > 0.001 ? $"  ·  {s.Speed:0.##}x" : "")
                    + (s.Muted ? "  ·  muted" : "");
            }
            else SegmentChip.Visibility = Visibility.Collapsed;

            DrawPlayhead();
        }

        private void Home_Click(object sender, RoutedEventArgs e) => SeekOutput(TimeSpan.Zero);
        private void Back_Click(object sender, RoutedEventArgs e) => Nudge(-1);
        private void Forward_Click(object sender, RoutedEventArgs e) => Nudge(1);

        private void Nudge(double seconds)
        {
            if (!_opened) return;
            SeekOutput(_output + TimeSpan.FromSeconds(seconds));
        }

        /// <summary>Jumps to the boundary between kept pieces — where the cuts actually are.</summary>
        private void PrevCut_Click(object sender, RoutedEventArgs e) => JumpCut(-1);
        private void NextCut_Click(object sender, RoutedEventArgs e) => JumpCut(1);

        private void JumpCut(int direction)
        {
            if (_project == null) return;

            int index = _project.SegmentAtOutput(_output);
            if (index < 0) index = _project.Segments.Count - 1;

            var start = _project.OutputStartOf(index);

            // Going back from just inside a piece means the start of *this* piece, which is
            // what a scrubbing hand expects; going back again reaches the previous one.
            if (direction < 0 && _output - start > TimeSpan.FromMilliseconds(400))
            {
                SeekOutput(start);
                return;
            }

            int target = Math.Clamp(index + direction, 0, _project.Segments.Count - 1);
            SeekOutput(_project.OutputStartOf(target));
        }

        private void PreviewSpeed_Changed(object sender, SelectionChangedEventArgs e)
        {
            _previewSpeed = double.TryParse(TagOf(PreviewSpeedCombo, "1"), out double v) ? v : 1.0;
            ApplyPlaybackRate();
        }

        // ── scrubbing ─────────────────────────────────────────────────────────

        private void Scrub_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
            => _scrubbing = true;

        private void Scrub_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            _scrubbing = false;
            SeekOutput(TimeSpan.FromSeconds(Scrubber.Value));
        }

        private void Scrub_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressScrub) return;
            // ScrubbingEnabled means the frame follows the thumb while dragging, which is
            // the whole point of placing a cut accurately.
            SeekOutput(TimeSpan.FromSeconds(e.NewValue));
        }

        // ── the timeline ──────────────────────────────────────────────────────

        private bool _draggingTimeline;

        private void Timeline_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawTimeline();

        /// <summary>
        /// Draws the edit to scale: one block per kept piece, widths proportional to how
        /// long each lasts in the finished file.
        ///
        /// Drawing the OUTPUT rather than the source is deliberate. A timeline of the
        /// original with cut parts greyed out shows what was thrown away; this shows what
        /// the viewer will actually watch, which is the thing being decided.
        /// </summary>
        private void RedrawTimeline()
        {
            Timeline.Children.Clear();
            if (_project == null || Timeline.ActualWidth < 10) return;

            double width = Timeline.ActualWidth;
            double height = 40;
            double total = Math.Max(0.001, _project.OutputDuration.TotalSeconds);

            double x = 0;
            for (int i = 0; i < _project.Segments.Count; i++)
            {
                var segment = _project.Segments[i];
                double w = Math.Max(2, segment.OutputDuration.TotalSeconds / total * width);

                bool isSelected = i == _selected;

                var block = new Border
                {
                    Width = Math.Max(2, w - 2),
                    Height = height,
                    CornerRadius = new CornerRadius(4),
                    Background = (Brush)FindResource(isSelected ? "Brush.Accent" : "Brush.BorderStrong"),
                    Opacity = segment.Muted ? 0.55 : 1.0,
                    Tag = i
                };

                // Enough room for a label only when the block is wide enough to hold one;
                // a two-pixel sliver with text over it is worse than a plain sliver.
                if (w > 46)
                {
                    string label = $"{i + 1}";
                    if (Math.Abs(segment.Speed - 1) > 0.001) label += $" · {segment.Speed:0.##}x";
                    if (segment.Muted) label += " · muted";

                    block.Child = new TextBlock
                    {
                        Text = label,
                        FontSize = 11,
                        Margin = new Thickness(6, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Foreground = (Brush)FindResource(
                            isSelected ? "Brush.OnAccent" : "Brush.TextBody")
                    };
                }

                Canvas.SetLeft(block, x);
                Canvas.SetTop(block, 0);
                Timeline.Children.Add(block);

                x += w;
            }

            DrawSelectionBand(width, height, total);
            DrawPlayhead();
        }

        /// <summary>
        /// Shades the marked range. The markers are in source time, so a selection that
        /// spans a cut shows up as two bands — which is correct, and is the thing a single
        /// source-time band would hide.
        /// </summary>
        private void DrawSelectionBand(double width, double height, double total)
        {
            if (_project == null || _out <= _in) return;

            var fill = new SolidColorBrush(Color.FromArgb(0x55, 0xFA, 0xCC, 0x15));

            double x = 0;
            foreach (var segment in _project.Segments)
            {
                double w = segment.OutputDuration.TotalSeconds / total * width;

                var from = segment.Start > _in ? segment.Start : _in;
                var to = segment.End < _out ? segment.End : _out;

                if (to > from)
                {
                    double scale = Math.Clamp(segment.Speed, 0.1, 8.0);
                    double a = (from - segment.Start).TotalSeconds / scale;
                    double b = (to - segment.Start).TotalSeconds / scale;

                    var band = new System.Windows.Shapes.Rectangle
                    {
                        Width = Math.Max(1, (b - a) / total * width),
                        Height = height,
                        Fill = fill,
                        RadiusX = 3,
                        RadiusY = 3,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(band, x + a / total * width);
                    Canvas.SetTop(band, 0);
                    Timeline.Children.Add(band);
                }

                x += w;
            }
        }

        private void DrawPlayhead()
        {
            if (_project == null || Timeline.ActualWidth < 10) return;

            // The playhead is removed and redrawn rather than moved, because the blocks
            // behind it are rebuilt on every edit and a stale line would be orphaned.
            for (int i = Timeline.Children.Count - 1; i >= 0; i--)
                if (Timeline.Children[i] is System.Windows.Shapes.Line) Timeline.Children.RemoveAt(i);

            double total = Math.Max(0.001, _project.OutputDuration.TotalSeconds);
            double x = _output.TotalSeconds / total * Timeline.ActualWidth;

            Timeline.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = x, Y1 = -3, X2 = x, Y2 = 46,
                Stroke = (Brush)FindResource("Brush.Danger"),
                StrokeThickness = 2,
                IsHitTestVisible = false
            });
        }

        private void Timeline_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_project == null) return;
            _draggingTimeline = true;
            Timeline.CaptureMouse();
            SeekFromTimeline(e.GetPosition(Timeline).X);
        }

        private void Timeline_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_draggingTimeline) return;

            // Same self-heal as the recording overlay: if the button came up somewhere we
            // never saw, stop following the pointer.
            if (e.LeftButton != MouseButtonState.Pressed) { Timeline_MouseUp(sender, null!); return; }

            SeekFromTimeline(e.GetPosition(Timeline).X);
        }

        private void Timeline_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_draggingTimeline) return;
            _draggingTimeline = false;
            try { Timeline.ReleaseMouseCapture(); } catch { }
        }

        private void SeekFromTimeline(double x)
        {
            if (_project == null || Timeline.ActualWidth < 10) return;

            double fraction = Math.Clamp(x / Timeline.ActualWidth, 0, 1);
            SeekOutput(TimeSpan.FromSeconds(fraction * _project.OutputDuration.TotalSeconds));
        }

        // ── editing ───────────────────────────────────────────────────────────

        private TimeSpan SourceNow => _project?.OutputToSource(_output) ?? TimeSpan.Zero;

        private void Split_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null) return;

            if (!_project.SplitAt(SourceNow))
            {
                StatusText.Text = "Move the playhead away from a cut first.";
                return;
            }

            StatusText.Text = "Split.";
            AfterEdit();
        }

        private void MarkIn_Click(object sender, RoutedEventArgs e)
        {
            if (!_opened) return;
            _in = SourceNow;
            if (_out <= _in) _out = _in;
            StatusText.Text = "";
            AfterEdit(keepPosition: true);
        }

        private void MarkOut_Click(object sender, RoutedEventArgs e)
        {
            if (!_opened) return;
            var p = SourceNow;
            if (p <= _in) { StatusText.Text = "The end has to come after the start."; return; }
            _out = p;
            StatusText.Text = "";
            AfterEdit(keepPosition: true);
        }

        private void DeleteRange_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null || _out <= _in)
            {
                StatusText.Text = "Mark a range first — in, then out.";
                return;
            }

            if (!_project.RemoveRange(_in, _out))
            {
                StatusText.Text = "That would remove the whole recording.";
                return;
            }

            StatusText.Text = "Removed.";
            _in = _out = TimeSpan.Zero;
            AfterEdit();
        }

        private void KeepRange_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null || _out <= _in)
            {
                StatusText.Text = "Mark a range first — in, then out.";
                return;
            }

            if (!_project.KeepOnly(_in, _out))
            {
                StatusText.Text = "That range has nothing left in it.";
                return;
            }

            StatusText.Text = "Trimmed to the selection.";
            _in = _out = TimeSpan.Zero;
            AfterEdit();
        }

        private void RemoveSegment_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null || _selected < 0) return;

            if (!_project.RemoveSegment(_selected))
            {
                StatusText.Text = "There has to be at least one clip left.";
                return;
            }

            _selected = Math.Max(0, _selected - 1);
            AfterEdit();
        }

        private void MoveLeft_Click(object sender, RoutedEventArgs e) => MoveSegment(-1);
        private void MoveRight_Click(object sender, RoutedEventArgs e) => MoveSegment(1);

        private void MoveSegment(int delta)
        {
            if (_project == null || _selected < 0) return;
            if (!_project.MoveSegment(_selected, delta)) return;

            _selected += delta;
            AfterEdit();
        }

        private void SegSpeed_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_populating || _project == null || _selected < 0) return;

            double speed = double.TryParse(TagOf(SegSpeedCombo, "1"), out double v) ? v : 1.0;
            if (!_project.SetSpeed(_selected, speed)) return;

            AfterEdit(keepPosition: true);
            ApplyPlaybackRate();
        }

        private void SegMute_Changed(object sender, RoutedEventArgs e)
        {
            if (_populating || _project == null || _selected < 0) return;
            if (!_project.SetMuted(_selected, SegMuteChk.IsChecked == true)) return;
            AfterEdit(keepPosition: true);
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (_project?.Undo() != true) return;
            StatusText.Text = "";
            AfterEdit();
        }

        private void Redo_Click(object sender, RoutedEventArgs e)
        {
            if (_project?.Redo() != true) return;
            StatusText.Text = "";
            AfterEdit();
        }

        private void ResetEdit_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null) return;
            _project.Reset();
            _in = _out = TimeSpan.Zero;
            _selected = 0;
            StatusText.Text = "Back to the whole recording.";
            AfterEdit();
        }

        /// <summary>
        /// Everything an edit has to refresh, in one place.
        ///
        /// The playhead is clamped rather than reset, because an edit almost never means
        /// "start again from the beginning" — deleting a fumble should leave you looking at
        /// the moment after it.
        /// </summary>
        private void AfterEdit(bool keepPosition = false)
        {
            if (_project == null) return;

            if (_selected >= _project.Segments.Count)
                _selected = _project.Segments.Count - 1;

            if (!keepPosition)
                _output = Clamp(_output, TimeSpan.Zero, _project.OutputDuration);

            RedrawTimeline();
            SyncUi();
            UpdateEditUi();

            if (!keepPosition) SeekPlayer(_project.OutputToSource(_output));
        }

        private void UpdateEditUi()
        {
            bool ready = _opened && _project != null;

            SplitBtn.IsEnabled = ready;
            MarkInBtn.IsEnabled = ready;
            MarkOutBtn.IsEnabled = ready;
            DeleteRangeBtn.IsEnabled = ready && _out > _in;
            KeepRangeBtn.IsEnabled = ready && _out > _in;
            UndoBtn.IsEnabled = _project?.CanUndo == true;
            RedoBtn.IsEnabled = _project?.CanRedo == true;
            ResetEditBtn.IsEnabled = _project?.IsEdited == true;
            TrimBtn.IsEnabled = ready;

            bool hasSelection = ready && _selected >= 0 && _selected < _project!.Segments.Count;
            SegLeftBtn.IsEnabled = hasSelection && _selected > 0;
            SegRightBtn.IsEnabled = hasSelection && _selected < _project!.Segments.Count - 1;
            SegDeleteBtn.IsEnabled = hasSelection && _project!.Segments.Count > 1;
            SegSpeedCombo.IsEnabled = hasSelection;
            SegMuteChk.IsEnabled = hasSelection;

            _populating = true;
            try
            {
                if (hasSelection)
                {
                    var s = _project!.Segments[_selected];
                    SelectedText.Text = $"Clip {_selected + 1} of {_project.Segments.Count}"
                                      + $"  ·  {Format(s.OutputDuration)}";
                    SelectByTag(SegSpeedCombo, s.Speed.ToString("0.##"));
                    SegMuteChk.IsChecked = s.Muted;
                }
                else
                {
                    SelectedText.Text = "No clip selected";
                    SegMuteChk.IsChecked = false;
                }
            }
            finally { _populating = false; }

            if (_project == null) { RangeText.Text = ""; return; }

            if (_out > _in)
            {
                RangeText.Text = $"Selected {Format(_in)} to {Format(_out)} of the original";
            }
            else if (_project.IsEdited)
            {
                var saved = _duration - _project.OutputDuration;
                RangeText.Text = saved > TimeSpan.Zero
                    ? $"{Format(_project.OutputDuration)} after editing — {Format(saved)} removed"
                    : $"{Format(_project.OutputDuration)} after editing";
            }
            else RangeText.Text = "Whole recording";
        }

        // ── file actions ──────────────────────────────────────────────────────

        private void Reveal_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{_path}\"",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (ThemedDialog.Show(this,
                    $"Send {Path.GetFileName(_path)} to the Recycle Bin?",
                    "Delete recording",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            // The file has to be released before the shell can move it.
            Stop();
            Player.Source = null;
            _opened = false;

            string target = _path;
            IntPtr owner = new System.Windows.Interop.WindowInteropHelper(this).Handle;

            if (Win32.RecycleFiles(new[] { target }, owner))
            {
                Deleted?.Invoke(this, target);
                Close();
            }
            else
            {
                StatusText.Text = "The file could not be deleted — it may still be open.";
                Load(target);
            }
        }

        private async void Trim_Click(object sender, RoutedEventArgs e)
        {
            if (!_opened || _project == null) return;

            if (!_project.IsEdited)
            {
                StatusText.Text = "Nothing to export — make a cut first.";
                return;
            }

            string folder = Path.GetDirectoryName(_path) ?? "";
            string stem = Path.GetFileNameWithoutExtension(_path);
            string destination = Path.Combine(folder, $"{stem}_edited.mp4");
            for (int n = 2; File.Exists(destination); n++)
                destination = Path.Combine(folder, $"{stem}_edited{n}.mp4");

            var quality = TagOf(ExportQualityCombo, "High") switch
            {
                "Efficient" => VideoQuality.Efficient,
                "Balanced" => VideoQuality.Balanced,
                "Maximum" => VideoQuality.Maximum,
                _ => VideoQuality.High
            };

            // The project is snapshotted before the render starts: the export runs for a
            // while and the user is free to keep editing, and a render that read a project
            // changing underneath it would produce something nobody asked for.
            var snapshot = _project;

            Pause();
            SetBusy(true);
            StatusText.Text = "Exporting…";

            _trimCancel = new CancellationTokenSource();
            var token = _trimCancel.Token;

            var progress = new Progress<double>(p =>
                StatusText.Text = $"Exporting… {p * 100:0}%");

            // Rendering decodes and re-encodes, so it belongs off the UI thread. MTA for
            // the same reason the capture thread is: Media Foundation objects are
            // thread-bound.
            var result = await Task.Run(() =>
            {
                VideoTrimmer.TrimResult? r = null;
                var thread = new Thread(() =>
                    // 30 is only a fallback: the exporter reads the real frame rate out of
                    // the file and uses that.
                    r = VideoTrimmer.Export(_path, destination, snapshot,
                                            30, quality,
                                            p => ((IProgress<double>)progress).Report(p),
                                            token))
                { IsBackground = true };
                thread.SetApartmentState(ApartmentState.MTA);
                thread.Start();
                thread.Join();
                return r;
            });

            SetBusy(false);
            _trimCancel = null;

            if (result is { Ok: true, Path: not null })
            {
                StatusText.Text = $"Saved {Path.GetFileName(result.Path)}";
                // Switch to the exported copy — that is what the user wants to check now.
                Load(result.Path);
            }
            else
            {
                StatusText.Text = result?.Error ?? "The export failed.";
            }
        }

        private void SetBusy(bool busy)
        {
            TrimBtn.IsEnabled = !busy;
            DeleteBtn.IsEnabled = !busy;
            SplitBtn.IsEnabled = !busy;
            DeleteRangeBtn.IsEnabled = !busy;
            KeepRangeBtn.IsEnabled = !busy;
            ResetEditBtn.IsEnabled = !busy;
            if (!busy) UpdateEditUi();
        }

        // ── keyboard ──────────────────────────────────────────────────────────

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Control);
            bool shift = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift);

            if (ctrl)
            {
                switch (e.Key)
                {
                    case Key.Z: Undo_Click(sender, new RoutedEventArgs()); e.Handled = true; return;
                    case Key.Y: Redo_Click(sender, new RoutedEventArgs()); e.Handled = true; return;
                }
            }

            switch (e.Key)
            {
                case Key.Space: TogglePlay(); e.Handled = true; break;
                case Key.Left: Nudge(shift ? -5 : -1); e.Handled = true; break;
                case Key.Right: Nudge(shift ? 5 : 1); e.Handled = true; break;
                case Key.Home: SeekOutput(TimeSpan.Zero); e.Handled = true; break;
                case Key.PageUp: JumpCut(-1); e.Handled = true; break;
                case Key.PageDown: JumpCut(1); e.Handled = true; break;
                case Key.S: Split_Click(sender, new RoutedEventArgs()); e.Handled = true; break;
                case Key.I: MarkIn_Click(sender, new RoutedEventArgs()); e.Handled = true; break;
                case Key.O: MarkOut_Click(sender, new RoutedEventArgs()); e.Handled = true; break;
                case Key.Delete: DeleteRange_Click(sender, new RoutedEventArgs()); e.Handled = true; break;
                case Key.Escape: Close(); break;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try { _trimCancel?.Cancel(); } catch { }
            _tick.Stop();
            try { Player.Stop(); Player.Source = null; } catch { }
            base.OnClosed(e);
        }

        // ── small helpers ─────────────────────────────────────────────────────

        private static TimeSpan Clamp(TimeSpan value, TimeSpan low, TimeSpan high)
            => value < low ? low : value > high ? high : value;

        private static string TagOf(ComboBox combo, string fallback)
            => combo.SelectedItem is ComboBoxItem item && item.Tag is string tag ? tag : fallback;

        private static void SelectByTag(ComboBox combo, string tag)
        {
            foreach (var item in combo.Items)
                if (item is ComboBoxItem ci && (ci.Tag as string) == tag)
                {
                    combo.SelectedItem = item;
                    return;
                }
        }

        private static string Format(TimeSpan t)
            => t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes}:{t.Seconds:00}";
    }
}
