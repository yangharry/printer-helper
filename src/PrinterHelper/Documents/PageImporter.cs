using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PrinterHelper.Documents
{
    /// <summary>Turns PDF and picture files into pages so they can be merged, saved and printed.</summary>
    public static class PageImporter
    {
        /// <summary>Resolution used to render PDF pages.</summary>
        public const double PdfDpi = 200;

        const long MaxRenderPixels = 40_000_000;

        static readonly string[] PictureExtensions = { ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff" };

        public const string OpenFileFilter = "PDF와 그림 파일|*.pdf;*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.bmp;*.gif;*.tif;*.tiff|모든 파일|*.*";

        public static bool IsSupported(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".pdf" || PictureExtensions.Contains(ext);
        }

        public static Task<List<PageImage>> ImportAsync(string path, CancellationToken ct)
        {
            return Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                ? Task.Run(() => ImportPdfAsync(path, ct), ct)
                : Task.Run(() => ImportPicture(path, ct), ct);
        }

        static async Task<List<PageImage>> ImportPdfAsync(string path, CancellationToken ct)
        {
            var name = Path.GetFileName(path);
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            PdfDocument document;
            try
            {
                document = await PdfDocument.LoadFromFileAsync(file);
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x8007052B))
            {
                throw new InvalidOperationException("암호가 걸린 PDF는 열 수 없습니다.");
            }

            var pages = new List<PageImage>();
            for (uint i = 0; i < document.PageCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                using (var page = document.GetPage(i))
                {
                    // Size is in 1/96 inch and already accounts for the page's own rotation.
                    var dpi = PdfDpi;
                    var width = page.Size.Width / 96.0 * dpi;
                    var height = page.Size.Height / 96.0 * dpi;
                    if (width * height > MaxRenderPixels)
                    {
                        var scale = Math.Sqrt(MaxRenderPixels / (width * height));
                        width *= scale;
                        height *= scale;
                        dpi *= scale;
                    }
                    var options = new PdfPageRenderOptions
                    {
                        DestinationWidth = (uint)Math.Max(1, Math.Round(width)),
                        DestinationHeight = (uint)Math.Max(1, Math.Round(height)),
                        BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
                    };
                    using (var rendered = new InMemoryRandomAccessStream())
                    {
                        await page.RenderToStreamAsync(rendered, options);
                        rendered.Seek(0);
                        using (var stream = rendered.AsStreamForRead())
                        {
                            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                            pages.Add(PageImage.FromBitmap(frame, dpi, name));
                        }
                    }
                }
            }
            return pages;
        }

        static List<PageImage> ImportPicture(string path, CancellationToken ct)
        {
            var name = Path.GetFileName(path);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var bytes = File.ReadAllBytes(path);
            var decoder = BitmapDecoder.Create(new MemoryStream(bytes, writable: false),
                BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

            var pages = new List<PageImage>();
            foreach (var frame in decoder.Frames)
            {
                ct.ThrowIfCancellationRequested();
                var rotation = ExifRotation(frame);

                // Faxes are often 204x98 dpi: stretch to square pixels, or the page comes out squashed.
                BitmapSource source = frame;
                var square = Math.Abs(frame.DpiX - frame.DpiY) <= 0.01 * Math.Max(frame.DpiX, frame.DpiY);
                if (!square && frame.DpiX > 0 && frame.DpiY > 0)
                {
                    var stretched = new TransformedBitmap(frame, new ScaleTransform(1, frame.DpiX / frame.DpiY));
                    stretched.Freeze();
                    source = stretched;
                }
                var dpi = Imaging.PageDpi(source.PixelWidth, source.PixelHeight, frame.DpiX);

                // A plain JPEG goes into the PDF without re-compression, but never with its metadata.
                var clean = square && decoder is JpegBitmapDecoder && decoder.Frames.Count == 1 ? JpegInfo.StripMetadata(bytes) : null;
                pages.Add(clean != null && JpegInfo.TryRead(clean) != null
                    ? PageImage.FromJpeg(clean, dpi, name, rotation)
                    : PageImage.FromBitmap(source, dpi, name, rotation));
                if (ext == ".gif") break; // animated GIF: first frame only
            }
            if (pages.Count == 0) throw new InvalidOperationException("그림에 쪽이 없습니다.");
            return pages;
        }

        /// <summary>Phone photos store "which way is up" in EXIF instead of rotating the pixels.</summary>
        static int ExifRotation(BitmapFrame frame)
        {
            try
            {
                if (frame.Metadata is BitmapMetadata metadata && metadata.ContainsQuery("/app1/ifd/{ushort=274}"))
                {
                    switch (Convert.ToInt32(metadata.GetQuery("/app1/ifd/{ushort=274}")))
                    {
                        case 3: return 180;
                        case 6: return 90;
                        case 8: return 270;
                    }
                }
            }
            catch
            {
                // Unreadable metadata: leave the picture as it is.
            }
            return 0;
        }
    }
}
