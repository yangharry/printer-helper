using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PrinterHelper.Documents
{
    static class Imaging
    {
        const int JpegQuality = 90;

        public static BitmapSource Decode(byte[] data, int decodePixelWidth = 0)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodePixelWidth > 0) image.DecodePixelWidth = decodePixelWidth;
            image.StreamSource = new MemoryStream(data, writable: false);
            image.EndInit();
            image.Freeze();
            return image;
        }

        public static byte[] EncodeJpeg(BitmapSource source)
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
            // No thumbnail, metadata or color profile: the source picture's EXIF must not be carried over.
            encoder.Frames.Add(BitmapFrame.Create(ToJpegFormat(source), null, null, null));
            using (var ms = new MemoryStream())
            {
                encoder.Save(ms);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Converts to 8-bit gray or 24-bit color, putting any transparency on a white background
        /// (otherwise transparent areas would turn black in the JPEG).
        /// </summary>
        static BitmapSource ToJpegFormat(BitmapSource source)
        {
            var format = source.Format;
            if (format == PixelFormats.Gray8 || format == PixelFormats.Bgr24 || format == PixelFormats.Rgb24)
                return SquareDpi(source);
            if (format == PixelFormats.BlackWhite || format == PixelFormats.Gray2 || format == PixelFormats.Gray4 ||
                format == PixelFormats.Gray16 || format == PixelFormats.Gray32Float)
                return SquareDpi(Frozen(new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0)));

            var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int width = bgra.PixelWidth, height = bgra.PixelHeight;
            var input = new byte[width * height * 4];
            bgra.CopyPixels(input, width * 4, 0);

            var stride = (width * 3 + 3) & ~3;
            var output = new byte[stride * height];
            for (var y = 0; y < height; y++)
            {
                var src = y * width * 4;
                var dst = y * stride;
                for (var x = 0; x < width; x++, src += 4, dst += 3)
                {
                    int alpha = input[src + 3];
                    var white = 255 - alpha;
                    output[dst] = (byte)((input[src] * alpha + 255 * white) / 255);
                    output[dst + 1] = (byte)((input[src + 1] * alpha + 255 * white) / 255);
                    output[dst + 2] = (byte)((input[src + 2] * alpha + 255 * white) / 255);
                }
            }
            var dpi = source.DpiX > 0 ? source.DpiX : 96;
            return Frozen(BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Bgr24, null, output, stride));
        }

        /// <summary>
        /// The pixels are already square when this is called (see PageImporter), so the stored DPI must be
        /// square too: WPF sizes images by DPI, and a 204x98 dpi fax would be drawn twice as tall.
        /// </summary>
        static BitmapSource SquareDpi(BitmapSource source)
        {
            if (Math.Abs(source.DpiX - source.DpiY) < 0.01) return source;
            var stride = (source.PixelWidth * source.Format.BitsPerPixel + 7) / 8;
            var pixels = new byte[stride * source.PixelHeight];
            source.CopyPixels(pixels, stride, 0);
            var dpi = source.DpiX > 0 ? source.DpiX : 96;
            return Frozen(BitmapSource.Create(source.PixelWidth, source.PixelHeight, dpi, dpi, source.Format, source.Palette, pixels, stride));
        }

        static BitmapSource Frozen(BitmapSource b)
        {
            b.Freeze();
            return b;
        }

        /// <summary>
        /// Picks a resolution for an imported picture: its own DPI, raised if needed so the page
        /// fits on A4 (a 4000-pixel phone photo at 72 dpi would otherwise be a 1.4 m wide page).
        /// </summary>
        public static double PageDpi(int width, int height, double sourceDpi)
        {
            double longSide = Math.Max(width, height), shortSide = Math.Min(width, height);
            var fit = Math.Max(longSide / 11.69, shortSide / 8.27);
            var dpi = sourceDpi >= 50 && sourceDpi <= 2400 ? sourceDpi : 96;
            return Math.Max(dpi, fit);
        }
    }
}
