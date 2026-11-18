using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using PrinterHelper.Documents;

namespace PrinterHelper.Printing
{
    /// <summary>Printing on Windows: the system print dialog, then GDI through the printer driver.</summary>
    [SupportedOSPlatform("windows")]
    public static class WindowsPrinting
    {
        /// <summary>The installed print queue whose TCP/IP port points at this host, or null.</summary>
        public static string FindQueueForHost(string host)
        {
            var wanted = HostOnly(host);
            if (wanted == null) return null;
            return Discovery.WindowsPrinterPorts.GetNetworkPrinters()
                .FirstOrDefault(p => string.Equals(HostOnly(p.Host), wanted, StringComparison.OrdinalIgnoreCase))?.QueueName;
        }

        /// <summary>"192.168.0.48:80" → "192.168.0.48": the scanner may be on another port than the printer.</summary>
        static string HostOnly(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return null;
            host = host.Trim();
            var colon = host.IndexOf(':');
            return colon > 0 && colon == host.LastIndexOf(':') ? host.Substring(0, colon) : host;
        }

        /// <summary>Shows the Windows print dialog. Returns null when the user cancels.</summary>
        /// <param name="owner">Window handle of the owner window.</param>
        public static PrinterSettings ShowPrintDialog(IntPtr owner, string preferredQueue) =>
            WindowsPrintDialog.Show(owner, preferredQueue);

        /// <summary>Prints one page per sheet (blocking; call it off the UI thread).</summary>
        public static void Print(PrinterSettings settings, IReadOnlyList<PageImage> pages, string documentName)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (pages == null || pages.Count == 0) throw new ArgumentException("인쇄할 페이지가 없습니다.", nameof(pages));
            if (!settings.IsValid) throw new InvalidOperationException($"프린터({settings.PrinterName})를 찾을 수 없습니다.");

            // Copy the list: the window may change it while the job is being spooled.
            var snapshot = pages.ToList();
            var next = 0;
            using (var document = new PrintDocument())
            {
                document.PrinterSettings = settings;
                document.DocumentName = string.IsNullOrWhiteSpace(documentName) ? "프린터 도우미" : documentName;
                // No "printing page 1 of 3" popup from a background thread.
                document.PrintController = new StandardPrintController();
                document.OriginAtMargins = false;
                document.BeginPrint += (sender, e) => next = 0;
                document.PrintPage += (sender, e) =>
                {
                    var pageSettings = e.PageSettings;
                    RectangleF paper = e.PageBounds;
                    DrawPage(e.Graphics, snapshot[next], paper, PrintableArea(pageSettings, paper),
                        new PointF(pageSettings.HardMarginX, pageSettings.HardMarginY));
                    next++;
                    e.HasMorePages = next < snapshot.Count;
                };
                document.Print();
            }
        }

        /// <summary>
        /// The area the printer reaches, in 1/100 inch from the sheet corner, turned to match the sheet.
        /// PageSettings.PrintableArea swaps the device's values back to portrait for landscape pages.
        /// </summary>
        internal static RectangleF PrintableArea(PageSettings pageSettings, RectangleF paper)
        {
            RectangleF area;
            try
            {
                area = pageSettings.PrintableArea;
            }
            catch (Exception)
            {
                return paper; // no margins known: PrintLayout falls back to its default
            }
            return OrientLike(area, paper);
        }

        internal static RectangleF OrientLike(RectangleF area, RectangleF paper)
        {
            if (area.Width <= 0 || area.Height <= 0) return paper;
            if ((area.Width > area.Height) != (paper.Width > paper.Height))
                return new RectangleF(area.Y, area.X, area.Height, area.Width);
            return area;
        }

        /// <summary>
        /// Draws one page the way <see cref="PrintLayout"/> places it. All values are in 1/100 inch
        /// (the printer Graphics' page unit).
        /// </summary>
        /// <param name="paper">The sheet (PageBounds).</param>
        /// <param name="printableArea">What the printer can reach, measured from the sheet corner.</param>
        /// <param name="origin">Where the Graphics origin is on the sheet: the hard margin, with OriginAtMargins off.</param>
        internal static void DrawPage(Graphics graphics, PageImage page, RectangleF paper, RectangleF printableArea, PointF origin)
        {
            var margin = PrintLayout.UniformMargin(
                (printableArea.Left - paper.Left) / 100,
                (printableArea.Top - paper.Top) / 100,
                (paper.Right - printableArea.Right) / 100,
                (paper.Bottom - printableArea.Bottom) / 100);
            var printable = PrintLayout.PrintableArea(paper.Width / 100, paper.Height / 100, margin);
            var placement = PrintLayout.Place(page, printable);
            var box = placement.Destination;

            using (var bitmap = PrintBitmap(page, placement.Rotation))
            using (var attributes = new ImageAttributes())
            {
                // Without this the bicubic filter blends the image edges with transparent black.
                attributes.SetWrapMode(WrapMode.TileFlipXY);

                var state = graphics.Save();
                try
                {
                    graphics.TranslateTransform(-origin.X, -origin.Y);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    float left = (float)(paper.Left + box.X * 100), top = (float)(paper.Top + box.Y * 100);
                    float right = (float)(paper.Left + box.Right * 100), bottom = (float)(paper.Top + box.Bottom * 100);
                    var corners = new[] { new PointF(left, top), new PointF(right, top), new PointF(left, bottom) };
                    graphics.DrawImage(bitmap, corners, new RectangleF(0, 0, bitmap.Width, bitmap.Height), GraphicsUnit.Pixel, attributes);
                }
                finally
                {
                    graphics.Restore(state);
                }
            }
        }

        /// <summary>
        /// The page decoded into a plain in-memory bitmap and turned. A bitmap still backed by its JPEG
        /// is handed to the driver as the JPEG itself ("JPEG passthrough"), and XPS-based drivers such
        /// as Microsoft Print to PDF then print an empty page.
        /// </summary>
        internal static Bitmap PrintBitmap(PageImage page, int rotation)
        {
            // GDI+ reads the stream lazily, so it has to stay open as long as the decoded JPEG.
            using (var stream = new MemoryStream(page.Jpeg, false))
            using (var source = new Bitmap(stream))
            {
                var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
                try
                {
                    using (var g = Graphics.FromImage(bitmap))
                    {
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height));
                    }
                    var turn = RotateFlipFor(rotation);
                    if (turn != RotateFlipType.RotateNoneFlipNone) bitmap.RotateFlip(turn);
                    return bitmap;
                }
                catch
                {
                    bitmap.Dispose();
                    throw;
                }
            }
        }

        /// <summary>GDI+ turns clockwise, the same way as PageImage.Rotation.</summary>
        internal static RotateFlipType RotateFlipFor(int rotation) => rotation switch
        {
            90 => RotateFlipType.Rotate90FlipNone,
            180 => RotateFlipType.Rotate180FlipNone,
            270 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone,
        };
    }
}
