using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AdvancedSnip.Services;

namespace AdvancedSnip
{
    /// <summary>
    /// The floating controls shown while a recording runs.
    ///
    /// Excluded from capture via WDA_EXCLUDEFROMCAPTURE, the same mechanism the scroll
    /// progress window uses, so the toolbar never appears in the video the user is making
    /// with it. Its colours are fixed dark rather than themed, for the same reason the
    /// capture overlays are: it sits on top of arbitrary screen content and has to stay
    /// legible whatever is behind it.
    ///
    /// **The drawing controls moved out of here.** They used to be a row of swatches,
    /// a marker toggle and a thickness button that appeared next to the Stop button. That
    /// put every tool change at the bottom of one monitor, which meant travelling there
    /// and back mid-demo with the pointer nowhere near the thing being annotated. They now
    /// live in <see cref="RecordToolPalette"/>, which opens under the pointer. What is
    /// left here is what belongs on a status bar: the clock, the size, the level meter,
    /// the current tool, and Stop.
    ///
    /// Two things are kept here rather than only in the picker, and both for the same
    /// reason — they have to be reachable when no tool is active. Undo and Clear act on
    /// drawings that stay on screen after the tools are put away, and the current-tool chip
    /// is a button, so the reply to "not that one" is a click on the thing that is wrong
    /// rather than a hunt for the thing that changes it.
    /// </summary>
    public partial class RecordHudWindow : Window
    {
        private readonly RecordAnnotationWindow? _ink;

        public event EventHandler? StopRequested;
        public event EventHandler<bool>? PauseChanged;

        /// <summary>Raised when the Tools button is pressed, so App can open the picker.</summary>
        public event EventHandler? ToolsRequested;

        public RecordHudWindow(RecordAnnotationWindow? ink, bool allowAnnotations)
        {
            InitializeComponent();
            _ink = ink;

            if (!allowAnnotations || ink == null)
            {
                ToolsBtn.Visibility = Visibility.Collapsed;
                EditTools.Visibility = Visibility.Collapsed;
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var handle = new WindowInteropHelper(this).Handle;
            int ex = NativeMethods.GetWindowLong(handle, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(handle, NativeMethods.GWL_EXSTYLE,
                                        ex | NativeMethods.WS_EX_TOOLWINDOW);

            // The whole point: the toolbar you are using to make the recording must not
            // be in the recording.
            try { Win32.SetWindowDisplayAffinity(handle, Win32.WDA_EXCLUDEFROMCAPTURE); } catch { }
        }

        /// <summary>Puts the HUD near the bottom of whichever monitor the pointer is on.</summary>
        public void PlaceNearCursor()
        {
            UpdateLayout();
            WindowPlacement.BottomCenterOn(this, DisplayInfo.FromCursor());
        }

        public void UpdateStatus(TimeSpan elapsed, long bytes)
        {
            ClockText.Text = elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
                : $"{elapsed.Minutes}:{elapsed.Seconds:00}";

            SizeText.Text = bytes <= 0 ? "" : MainWindow.FormatBytes(bytes);
        }

        /// <summary>
        /// Moves the level meter.
        ///
        /// The bar is driven by the peak since the last report rather than an average,
        /// because the question it answers is "is anything reaching the recorder at all",
        /// and an average of mostly-silence answers that badly. It is deliberately not a
        /// calibrated meter — the useful signals are "moving" and "pinned at the top",
        /// and both are legible at 46 pixels wide.
        /// </summary>
        public void UpdateAudio(bool active, float peak, bool muted)
        {
            if (!active)
            {
                if (AudioChip.Visibility != Visibility.Collapsed)
                    AudioChip.Visibility = Visibility.Collapsed;
                return;
            }

            AudioChip.Visibility = Visibility.Visible;
            AudioGlyph.Text = muted ? "\U0001F507" : "\U0001F50A";
            AudioGlyph.Foreground = new SolidColorBrush(muted
                ? Color.FromRgb(0xF8, 0x71, 0x71)
                : Color.FromRgb(0xD1, 0xD5, 0xDB));

            if (muted) { AudioLevelBar.Width = 0; return; }

            // Square root, so quiet speech still shows movement. A linear bar spends most
            // of a normal recording within a pixel or two of zero.
            double scaled = Math.Sqrt(Math.Clamp(peak, 0f, 1f));
            AudioLevelBar.Width = Math.Round(scaled * 46);

            AudioLevelBar.Background = new SolidColorBrush(
                peak > 0.95f ? Color.FromRgb(0xEF, 0x44, 0x44) :   // clipping
                peak > 0.75f ? Color.FromRgb(0xFA, 0xCC, 0x15)     // hot
                             : Color.FromRgb(0x22, 0xC5, 0x5E));
        }

        // ── controls ──────────────────────────────────────────────────────────

        private void Hud_Drag(object sender, MouseButtonEventArgs e)
        {
            // A full-screen recording will eventually have the HUD sitting over something
            // the user wants to see, so it has to be movable.
            try { DragMove(); } catch { }
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
            => StopRequested?.Invoke(this, EventArgs.Empty);

        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            bool nowPaused = PauseBtn.Content as string == "Pause";
            PauseBtn.Content = nowPaused ? "Resume" : "Pause";
            RecDot.Fill = new SolidColorBrush(nowPaused
                ? Color.FromRgb(0x94, 0xA3, 0xB8)
                : Color.FromRgb(0xEF, 0x44, 0x44));
            PauseChanged?.Invoke(this, nowPaused);
        }

        private void Tools_Click(object sender, RoutedEventArgs e)
            => ToolsRequested?.Invoke(this, EventArgs.Empty);

        private void Done_Click(object sender, RoutedEventArgs e)
            => _ink?.SetTool(RecordTool.None);

        /// <summary>
        /// Puts the HUD back on top of the pen overlay.
        ///
        /// Both windows are topmost, and within that band the last one to be raised wins.
        /// The overlay is WS_EX_NOACTIVATE so it should never raise itself, but this is the
        /// window the user needs in order to stop a recording, so it re-asserts its
        /// position rather than trusting that nothing else will ever reorder the band.
        /// </summary>
        public void RaiseAboveOverlay()
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (handle == IntPtr.Zero) return;
                Win32.SetWindowPos(handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                                   Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            }
            catch { }
        }

        /// <summary>
        /// Shows which tool is live, in its colour, and reveals Undo / Clear / Done while
        /// one is. Called from wherever the tool changed — the palette, a hotkey, Escape,
        /// or a right-click on the overlay — so the toolbar can never disagree with the
        /// surface about what the pointer is about to do.
        /// </summary>
        public void ShowTool(RecordTool tool, Color colour)
        {
            bool on = tool != RecordTool.None;

            DoneBtn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            ToolChip.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

            if (!on) return;

            ToolText.Text = RecordAnnotationWindow.NameOf(tool);
            ToolDot.Fill = new SolidColorBrush(colour);
            RaiseAboveOverlay();
        }

        /// <summary>
        /// Enables Undo and Clear according to whether there is anything to act on.
        ///
        /// Undo stays live after an erase, because an erase is the thing most worth being
        /// able to take back and the buttons would otherwise both go dim at the exact
        /// moment someone realises they hit the wrong one.
        /// </summary>
        public void ShowDrawingState(bool hasDrawings, bool canUndoClear)
        {
            UndoInkBtn.IsEnabled = hasDrawings || canUndoClear;
            ClearInkBtn.IsEnabled = hasDrawings;

            UndoInkBtn.ToolTip = !hasDrawings && canUndoClear
                ? "Bring back everything the last erase removed (Ctrl+Shift+Z)"
                : "Remove the last thing drawn (Ctrl+Shift+Z)";
        }

        /// <summary>
        /// Shows the current zoom level, or hides the chip at 1x. The zoom is only visible
        /// in the recorded frames, so without this the presenter has no readout of it at
        /// all beyond the viewport outline.
        /// </summary>
        public void ShowZoom(double factor)
        {
            bool on = factor > 1.0001;
            ZoomChip.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on) ZoomText.Text = factor % 1 == 0 ? $"{factor:0}x" : $"{factor:0.0}x";
        }

        private void Keys_Click(object sender, RoutedEventArgs e)
        {
            KeysPanel.Visibility = KeysPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;

            // The HUD sizes to its content, so opening the legend grows it downwards and
            // can push it off the bottom of the screen. Re-place it once the new size is
            // known.
            UpdateLayout();
            WindowPlacement.BottomCenterOn(this, DisplayInfo.FromCursor());
            RaiseAboveOverlay();
        }

        private void UndoInk_Click(object sender, RoutedEventArgs e) => _ink?.Undo();

        private void ClearInk_Click(object sender, RoutedEventArgs e) => _ink?.ClearAll();
    }
}
