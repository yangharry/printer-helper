using System;
using SkiaSharp;

namespace PrinterHelper.Documents
{
    /// <summary>
    /// Pixel work on SkiaSharp. Pages are kept as either 8-bit gray (<see cref="SKColorType.Gray8"/>) or
    /// opaque BGRA, which is what a JPEG in a PDF can hold.
    /// </summary>
    static class Imaging
    {
        const int JpegQuality = 90;
        const int ThumbnailWidth = 200;

        /// <summary>
        /// The most pixels a picture is decoded at (100 MP is 400 MB as BGRA). Phone photos are well below it;
        /// anything bigger is decoded at a reduced size when the format allows it, or refused.
        /// </summary>
        public const long MaxPixels = 100_000_000;

        /// <summary>
        /// Decodes everything a codec holds into gray or opaque BGRA, putting any transparency on a white
        /// background (otherwise transparent areas would turn black in the JPEG). Color profiles are ignored.
        /// </summary>
        public static SKBitmap Decode(SKCodec codec) => Decode(codec, MaxPixels, out _);

        /// <param name="scale">How much smaller than the file the result is (1 = full size).</param>
        public static SKBitmap Decode(SKCodec codec, long maxPixels, out double scale)
        {
            var full = codec.Info.Size;
            var size = FitSize(codec, maxPixels);
            scale = (double)size.Width / full.Width;
            if (codec.Info.ColorType == SKColorType.Gray8)
            {
                var gray = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Gray8, SKAlphaType.Opaque));
                Check(codec.GetPixels(gray.Info, gray.GetPixels()));
                return gray;
            }

            var info = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var bitmap = new SKBitmap(info);
            Check(codec.GetPixels(info, bitmap.GetPixels()));
            FlattenOnWhite(bitmap);
            return bitmap;
        }

        /// <summary>The full size, or the largest size the codec can decode to that stays under the pixel limit.</summary>
        static SKSizeI FitSize(SKCodec codec, long maxPixels)
        {
            var full = codec.Info.Size;
            if (full.Width <= 0 || full.Height <= 0) throw new InvalidOperationException("그림 크기를 읽지 못했습니다.");
            long pixels = (long)full.Width * full.Height;
            if (pixels <= maxPixels) return full;
            // JPEG decodes in eighths, WEBP to any size; other formats cannot scale and are refused.
            var wanted = Math.Sqrt((double)maxPixels / pixels);
            for (var attempt = 0; attempt < 30; attempt++, wanted *= 0.9)
            {
                var size = codec.GetScaledDimensions((float)wanted);
                if (size.Width < full.Width && size.Width > 0 && size.Height > 0 && (long)size.Width * size.Height <= maxPixels)
                    return size;
            }
            throw new InvalidOperationException($"그림이 너무 큽니다 ({full.Width}×{full.Height} 픽셀).");
        }

        static void Check(SKCodecResult result)
        {
            // IncompleteInput still gives a usable (partly gray) picture, like the Windows decoder did.
            if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                throw new InvalidOperationException($"그림을 읽지 못했습니다 ({result}).");
        }

        /// <summary>Blends unpremultiplied BGRA pixels onto white and marks them opaque.</summary>
        public static unsafe void FlattenOnWhite(SKBitmap bitmap)
        {
            var p = (byte*)bitmap.GetPixels();
            var rowBytes = bitmap.RowBytes;
            for (var y = 0; y < bitmap.Height; y++)
            {
                var row = p + (long)y * rowBytes;
                for (var x = 0; x < bitmap.Width; x++, row += 4)
                {
                    int alpha = row[3];
                    if (alpha == 255) continue;
                    var white = 255 - alpha;
                    row[0] = (byte)((row[0] * alpha + 255 * white) / 255);
                    row[1] = (byte)((row[1] * alpha + 255 * white) / 255);
                    row[2] = (byte)((row[2] * alpha + 255 * white) / 255);
                    row[3] = 255;
                }
            }
        }

        /// <summary>Encodes gray as a 1-channel JPEG and BGRA as a color JPEG, without metadata or color profile.</summary>
        public static byte[] EncodeJpeg(SKBitmap bitmap)
        {
            using (var pixmap = bitmap.PeekPixels())
            using (var data = pixmap.Encode(new SKJpegEncoderOptions(JpegQuality, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore)))
            {
                if (data == null) throw new InvalidOperationException("그림을 JPEG로 바꾸지 못했습니다.");
                var jpeg = data.ToArray();
                // Skia may add a color profile block; the page must carry nothing but the image.
                return JpegInfo.StripMetadata(jpeg) ?? jpeg;
            }
        }

        /// <summary>
        /// A small JPEG for the page list, 200 to 400 pixels wide. The JPEG decoder scales by eighths while
        /// decoding, which is fast; what is still more than twice too wide is then averaged down.
        /// </summary>
        public static (byte[] Jpeg, int Width, int Height) Thumbnail(byte[] jpeg)
        {
            using (var data = SKData.CreateCopy(jpeg))
            using (var codec = SKCodec.Create(data))
            {
                if (codec == null) throw new InvalidOperationException("그림을 읽지 못했습니다.");
                var full = codec.Info.Size;
                var scale = Math.Min(1f, (float)ThumbnailWidth / full.Width);
                var scaled = codec.GetScaledDimensions(scale);
                var gray = codec.Info.ColorType == SKColorType.Gray8;
                var info = new SKImageInfo(scaled.Width, scaled.Height, gray ? SKColorType.Gray8 : SKColorType.Bgra8888, SKAlphaType.Opaque);
                using (var decoded = new SKBitmap(info))
                {
                    Check(codec.GetPixels(info, decoded.GetPixels()));
                    var factor = Math.Min(decoded.Width / ThumbnailWidth, decoded.Height);
                    if (factor < 2) return (EncodeJpeg(decoded), decoded.Width, decoded.Height);
                    using (var smaller = Shrink(decoded, factor))
                        return (EncodeJpeg(smaller), smaller.Width, smaller.Height);
                }
            }
        }

        /// <summary>Averages each factor x factor block into one pixel (works for gray and BGRA alike).</summary>
        static unsafe SKBitmap Shrink(SKBitmap source, int factor)
        {
            int bpp = source.BytesPerPixel, width = source.Width / factor, height = source.Height / factor;
            var result = new SKBitmap(new SKImageInfo(width, height, source.ColorType, SKAlphaType.Opaque));
            var src = (byte*)source.GetPixels();
            var dst = (byte*)result.GetPixels();
            var area = factor * factor;
            var sums = new int[bpp];
            for (var y = 0; y < height; y++)
            {
                var d = dst + (long)y * result.RowBytes;
                for (var x = 0; x < width; x++, d += bpp)
                {
                    Array.Clear(sums, 0, bpp);
                    for (var dy = 0; dy < factor; dy++)
                    {
                        var s = src + (long)(y * factor + dy) * source.RowBytes + (long)x * factor * bpp;
                        for (var dx = 0; dx < factor * bpp; dx++) sums[dx % bpp] += s[dx];
                    }
                    for (var c = 0; c < bpp; c++) d[c] = (byte)((sums[c] + area / 2) / area);
                }
            }
            return result;
        }

        /// <summary>
        /// Stretches a bitmap vertically with linear interpolation. Faxes are often 204x98 dpi: their
        /// pixels are twice as tall as wide, and the page comes out squashed unless the rows are doubled.
        /// </summary>
        public static unsafe SKBitmap StretchVertical(SKBitmap source, int newHeight)
        {
            var bpp = source.BytesPerPixel;
            var result = new SKBitmap(new SKImageInfo(source.Width, newHeight, source.ColorType, SKAlphaType.Opaque));
            var src = (byte*)source.GetPixels();
            var dst = (byte*)result.GetPixels();
            int srcRow = source.RowBytes, dstRow = result.RowBytes, rowLength = source.Width * bpp;
            var ratio = (double)source.Height / newHeight;
            for (var y = 0; y < newHeight; y++)
            {
                var at = Math.Max(0, (y + 0.5) * ratio - 0.5);
                var y0 = Math.Min(source.Height - 1, (int)at);
                var y1 = Math.Min(source.Height - 1, y0 + 1);
                var w1 = at - y0;
                var a = src + (long)y0 * srcRow;
                var b = src + (long)y1 * srcRow;
                var d = dst + (long)y * dstRow;
                for (var i = 0; i < rowLength; i++)
                    d[i] = (byte)(a[i] + (b[i] - a[i]) * w1 + 0.5);
            }
            return result;
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
