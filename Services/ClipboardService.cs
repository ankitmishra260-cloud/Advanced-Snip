using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// Puts an image on the Windows clipboard in several formats so it pastes into
    /// as many apps as possible (Paint/Word take a DIB; browsers and chat apps often
    /// prefer a raw PNG stream).
    /// </summary>
    internal static class ClipboardService
    {
        internal static void SetImage(BitmapSource src)
        {
            try
            {
                var data = new DataObject();

                // Standard WPF bitmap / DIB — works in most Windows apps.
                data.SetImage(src);

                // PNG stream — browsers, Slack, Discord, etc. prefer this.
                // IMPORTANT: the MemoryStream must stay alive (not disposed) until
                // after SetDataObject is called, because the DataObject holds a
                // reference into it. Do NOT wrap in a using block here.
                var ms = new MemoryStream();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(src));
                encoder.Save(ms);
                ms.Position = 0;
                data.SetData("PNG", ms);

                // copy: true flushes the data into the OLE clipboard so it persists
                // after our process releases it.
                Clipboard.SetDataObject(data, copy: true);

                // Safe to dispose the stream only after the OLE copy is done.
                ms.Dispose();
            }
            catch
            {
                // The clipboard is a shared, single-owner resource; another app may
                // hold it briefly. Wait and fall back to the simple API.
                try
                {
                    Thread.Sleep(80);
                    Clipboard.SetImage(src);
                }
                catch
                {
                    // Give up — the snip is saved to disk and in the history.
                }
            }
        }
    }
}
