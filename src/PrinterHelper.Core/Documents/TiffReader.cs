using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BitMiracle.LibTiff.Classic;
using SkiaSharp;

namespace PrinterHelper.Documents
{
    /// <summary>Reads every page of a TIFF (Skia has no TIFF decoder), including 1-bit fax pages.</summary>
    static class TiffReader
    {
        const string DamagedMessage = "TIFF 파일이 손상되었거나 읽을 수 없습니다.";

        static int quietInstalled;

        public static List<PageImage> Read(byte[] bytes, string name, CancellationToken ct)
        {
            // LibTiff prints warnings to the console by default.
            if (Interlocked.Exchange(ref quietInstalled, 1) == 0) Tiff.SetErrorHandler(new QuietErrors());
            try
            {
                return ReadPages(bytes, name, ct);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException || ex is InvalidOperationException || ex is OutOfMemoryException))
            {
                // LibTiff's own exceptions are English and technical.
                throw new InvalidOperationException(DamagedMessage, ex);
            }
        }

        static List<PageImage> ReadPages(byte[] bytes, string name, CancellationToken ct)
        {
            var pages = new List<PageImage>();
            using (var stream = new MemoryStream(bytes, writable: false))
            using (var tif = Tiff.ClientOpen("image.tif", "r", stream, new TiffStream()))
            {
                if (tif == null) throw new InvalidOperationException(DamagedMessage);
                int count = tif.NumberOfDirectories();
                for (short i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    // A page that cannot be read is an error, not the end of the file: a silently shorter document is worse.
                    if (!tif.SetDirectory(i)) throw new InvalidOperationException($"TIFF의 {i + 1}쪽을 읽지 못했습니다.");
                    int width = Int(tif, TiffTag.IMAGEWIDTH), height = Int(tif, TiffTag.IMAGELENGTH);
                    if (width <= 0 || height <= 0) throw new InvalidOperationException($"TIFF의 {i + 1}쪽을 읽지 못했습니다.");
                    if ((long)width * height > Imaging.MaxPixels)
                        throw new InvalidOperationException($"그림이 너무 큽니다 ({width}×{height} 픽셀).");

                    // LibTiff's oriented read can flip but not turn. Orientations 1-4 it gets right by itself; for the
                    // sideways ones (5-8) it is told to do only the flip, if any, and the page is turned instead.
                    var orientation = (Orientation)Int(tif, TiffTag.ORIENTATION, (int)Orientation.TOPLEFT);
                    if (orientation == Orientation.LEFTTOP || orientation == Orientation.RIGHTBOT)
                        tif.SetField(TiffTag.ORIENTATION, Orientation.TOPRIGHT);
                    else if (orientation == Orientation.RIGHTTOP || orientation == Orientation.LEFTBOT)
                        tif.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
                    var raster = new int[width * height];
                    if (!tif.ReadRGBAImageOriented(width, height, raster, Orientation.TOPLEFT))
                        throw new InvalidOperationException($"TIFF의 {i + 1}쪽을 읽지 못했습니다.");

                    var photometric = (Photometric)Int(tif, TiffTag.PHOTOMETRIC, (int)Photometric.MINISBLACK);
                    var gray = Int(tif, TiffTag.SAMPLESPERPIXEL, 1) == 1 &&
                               (photometric == Photometric.MINISBLACK || photometric == Photometric.MINISWHITE);
                    using (var bitmap = gray ? ToGray(raster, width, height) : ToBgra(raster, width, height))
                        pages.Add(PageImporter.MakePage(bitmap, Resolution(tif), name, Rotation(orientation)));
                }
            }
            if (pages.Count == 0) throw new InvalidOperationException("그림에 쪽이 없습니다.");
            return pages;
        }

        /// <summary>Clockwise turn still needed after the read above (1-4 come out upright already).</summary>
        static int Rotation(Orientation orientation)
        {
            switch (orientation)
            {
                case Orientation.LEFTTOP: return 270;
                case Orientation.RIGHTTOP: return 90;
                case Orientation.RIGHTBOT: return 90;
                case Orientation.LEFTBOT: return 270;
                default: return 0;
            }
        }

        static int Int(Tiff tif, TiffTag tag, int fallback = 0)
        {
            var value = tif.GetField(tag);
            return value != null && value.Length > 0 ? value[0].ToInt() : fallback;
        }

        static (double X, double Y)? Resolution(Tiff tif)
        {
            var x = tif.GetField(TiffTag.XRESOLUTION);
            var y = tif.GetField(TiffTag.YRESOLUTION);
            if (x == null || y == null) return null;
            double dx = x[0].ToFloat(), dy = y[0].ToFloat();
            if (!(dx > 0 && dy > 0) || double.IsInfinity(dx) || double.IsInfinity(dy)) return null;
            var unit = (ResUnit)Int(tif, TiffTag.RESOLUTIONUNIT, (int)ResUnit.INCH);
            // No unit: the values only give the pixels' shape. Keep the shape with the usual 96 dpi.
            if (unit == ResUnit.NONE) return (96, 96 * dy / dx);
            return unit == ResUnit.CENTIMETER ? (dx * 2.54, dy * 2.54) : (dx, dy);
        }

        static unsafe SKBitmap ToGray(int[] raster, int width, int height)
        {
            var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque));
            var p = (byte*)bitmap.GetPixels();
            for (var y = 0; y < height; y++)
            {
                var row = p + (long)y * bitmap.RowBytes;
                for (var x = 0; x < width; x++) row[x] = (byte)Tiff.GetR(raster[y * width + x]);
            }
            return bitmap;
        }

        static unsafe SKBitmap ToBgra(int[] raster, int width, int height)
        {
            var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            var p = (byte*)bitmap.GetPixels();
            for (var y = 0; y < height; y++)
            {
                var row = p + (long)y * bitmap.RowBytes;
                for (var x = 0; x < width; x++, row += 4)
                {
                    var v = raster[y * width + x];
                    row[0] = (byte)Tiff.GetB(v);
                    row[1] = (byte)Tiff.GetG(v);
                    row[2] = (byte)Tiff.GetR(v);
                    row[3] = (byte)Tiff.GetA(v);
                }
            }
            Imaging.FlattenOnWhite(bitmap);
            return bitmap;
        }

        sealed class QuietErrors : TiffErrorHandler
        {
            public override void WarningHandler(Tiff tif, string method, string format, params object[] args) { }
            public override void WarningHandlerExt(Tiff tif, object clientData, string method, string format, params object[] args) { }
            public override void ErrorHandler(Tiff tif, string method, string format, params object[] args) { }
            public override void ErrorHandlerExt(Tiff tif, object clientData, string method, string format, params object[] args) { }
        }
    }
}
