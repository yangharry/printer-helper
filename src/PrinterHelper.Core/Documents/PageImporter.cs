using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PrinterHelper.Pdf;
using SkiaSharp;

namespace PrinterHelper.Documents
{
    /// <summary>Turns PDF and picture files into pages so they can be merged, saved and printed.</summary>
    public static class PageImporter
    {
        /// <summary>Resolution used to render PDF pages.</summary>
        public const double PdfDpi = 200;

        public const long MaxRenderPixels = 40_000_000;

        static readonly string[] PictureExtensions = { ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

        /// <summary>File patterns for the open dialog, e.g. "*.pdf".</summary>
        public static IReadOnlyList<string> Patterns { get; } = new[] { "*.pdf" }.Concat(PictureExtensions.Select(e => "*" + e)).ToArray();

        public static bool IsSupported(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".pdf" || PictureExtensions.Contains(ext);
        }

        public static Task<List<PageImage>> ImportAsync(string path, CancellationToken ct)
        {
            // macOS file names often come decomposed (NFD); show them composed like everywhere else.
            var name = Path.GetFileName(path).Normalize();
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return Task.Run(() =>
            {
                var bytes = File.ReadAllBytes(path);
                // Go by the content, not the name: a scan saved as .jpg that is really a TIFF still opens, and so does
                // a JPEG named .pdf. The name only picks the message when nothing can read the file.
                switch (Kind(bytes))
                {
                    case "pdf": return PdfRenderer.Render(bytes, name, PdfDpi, MaxRenderPixels, ct);
                    case "tiff": return TiffReader.Read(bytes, name, ct);
                    default: return ImportPicture(bytes, name, ext, ct);
                }
            }, ct);
        }

        /// <summary>"pdf" or "tiff" from the first bytes, otherwise "picture" (Skia recognises those formats itself).</summary>
        static string Kind(byte[] b)
        {
            bool Starts(params byte[] sig) => b.Length >= sig.Length && sig.Select((x, i) => b[i] == x).All(same => same);
            if (Starts(0x49, 0x49, 42, 0) || Starts(0x49, 0x49, 43, 0) || Starts(0x4D, 0x4D, 0, 42) || Starts(0x4D, 0x4D, 0, 43))
                return "tiff";
            // A picture signature first: its metadata could contain the text "%PDF-".
            if (Starts(0xFF, 0xD8, 0xFF) || Starts(0x89, 0x50, 0x4E, 0x47) || Starts(0x47, 0x49, 0x46, 0x38) || Starts(0x42, 0x4D) ||
                Starts(0x52, 0x49, 0x46, 0x46) && b.Length >= 12 && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
                return "picture";
            // The PDF header may come after some junk; readers look in the first kilobyte.
            var head = System.Text.Encoding.ASCII.GetString(b, 0, Math.Min(b.Length, 1024));
            return head.Contains("%PDF-") ? "pdf" : "picture";
        }

        static List<PageImage> ImportPicture(byte[] bytes, string name, string ext, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using (var data = SKData.CreateCopy(bytes))
            using (var codec = SKCodec.Create(data))
            {
                if (codec == null)
                    throw new InvalidOperationException(ext == ".pdf" ? "PDF 파일이 손상되었거나 읽을 수 없습니다."
                        : ext == ".tif" || ext == ".tiff" ? "TIFF 파일이 손상되었거나 읽을 수 없습니다."
                        : "그림 형식을 읽지 못했습니다.");
                var format = codec.EncodedFormat;
                // Phone photos store "which way is up" in EXIF instead of rotating the pixels.
                var rotation = format == SKEncodedImageFormat.Jpeg || format == SKEncodedImageFormat.Webp ? Rotation(codec.EncodedOrigin) : 0;
                var density = ImageDensity.Read(bytes);

                // A plain JPEG goes into the PDF without re-compression, but never with its metadata.
                if (format == SKEncodedImageFormat.Jpeg && IsSquare(density))
                {
                    var clean = JpegInfo.StripMetadata(bytes);
                    var info = clean != null ? JpegInfo.TryRead(clean) : null;
                    if (info != null)
                        return new List<PageImage> { PageImage.FromJpeg(clean, Imaging.PageDpi(info.Width, info.Height, density?.X ?? 96), name, rotation) };
                }

                // GIF: an animation counts as its first frame. A huge picture is decoded smaller, keeping its size on paper.
                using (var bitmap = Imaging.Decode(codec, Imaging.MaxPixels, out var scale))
                {
                    if (density.HasValue) density = (density.Value.X * scale, density.Value.Y * scale);
                    return new List<PageImage> { MakePage(bitmap, density, name, rotation) };
                }
            }
        }

        /// <summary>
        /// Builds a page from decoded pixels. Pixels that are not square (faxes are often 204x98 dpi)
        /// are stretched to square first, or the page comes out squashed.
        /// </summary>
        internal static PageImage MakePage(SKBitmap bitmap, (double X, double Y)? density, string name, int rotation)
        {
            // A ratio far beyond any fax (204x98 is about 2) is a broken header, not a shape: ignore it.
            if (density.HasValue && !(density.Value.X / density.Value.Y >= 0.25 && density.Value.X / density.Value.Y <= 4))
                density = null;
            var dpiX = density?.X ?? 96;
            if (!IsSquare(density))
            {
                var height = (long)Math.Round(bitmap.Height * density.Value.X / density.Value.Y);
                if (height < 1 || height * bitmap.Width > Imaging.MaxPixels)
                    throw new InvalidOperationException($"그림이 너무 큽니다 ({bitmap.Width}×{height} 픽셀).");
                using (var stretched = Imaging.StretchVertical(bitmap, (int)height))
                    return PageImage.FromBitmap(stretched, Imaging.PageDpi(stretched.Width, stretched.Height, dpiX), name, rotation);
            }
            return PageImage.FromBitmap(bitmap, Imaging.PageDpi(bitmap.Width, bitmap.Height, dpiX), name, rotation);
        }

        static bool IsSquare((double X, double Y)? density) =>
            density == null || Math.Abs(density.Value.X - density.Value.Y) <= 0.01 * Math.Max(density.Value.X, density.Value.Y);

        static int Rotation(SKEncodedOrigin origin)
        {
            switch (origin)
            {
                case SKEncodedOrigin.BottomRight: return 180;
                case SKEncodedOrigin.RightTop: return 90;
                case SKEncodedOrigin.LeftBottom: return 270;
                default: return 0; // mirrored orientations are left as they are, as before
            }
        }
    }
}
