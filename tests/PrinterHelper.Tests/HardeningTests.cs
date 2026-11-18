using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using BitMiracle.LibTiff.Classic;
using PrinterHelper.Discovery;
using PrinterHelper.Documents;
using SkiaSharp;
using static PrinterHelper.Tests.Assert;

namespace PrinterHelper.Tests
{
    /// <summary>Hostile, broken or unusual input that once crashed, hung or misbehaved.</summary>
    static class HardeningTests
    {
        static List<PageImage> Import(string fileName, byte[] bytes)
        {
            var path = Path.Combine(Program.OutDir, fileName);
            File.WriteAllBytes(path, bytes);
            return PageImporter.ImportAsync(path, CancellationToken.None).GetAwaiter().GetResult();
        }

        static string ImportError(string fileName, byte[] bytes)
        {
            try { Import(fileName, bytes); }
            catch (InvalidOperationException ex) { return ex.Message; }
            throw new Exception("check failed: expected a friendly error for " + fileName);
        }

        [Test("A picture with an absurd size is refused, not decoded")]
        static void HugePictureRefused()
        {
            // A valid PNG header claiming 30000 x 30000 pixels (900 MP) with almost no data.
            var message = ImportError("huge.png", Png(30000, 30000));
            Check(message.Contains("너무 큽니다"), "too big: " + message);
        }

        [Test("A large JPEG that has to be decoded is decoded smaller, keeping its size on paper")]
        static void LargeJpegScaled()
        {
            using (var data = SKData.CreateCopy(TestImages.Jpeg(800, 600, true)))
            using (var codec = SKCodec.Create(data))
            using (var bitmap = Imaging.Decode(codec, 120_000, out var scale))
            {
                Check((long)bitmap.Width * bitmap.Height <= 120_000, $"under the limit ({bitmap.Width}x{bitmap.Height})");
                Check(scale < 1 && Math.Abs(bitmap.Width - 800 * scale) < 1, $"scale {scale} matches the width");
            }
        }

        [Test("A corrupt TIFF resolution does not stretch the page to millions of rows")]
        static void CorruptResolutionIgnored()
        {
            var page = Import("odd-resolution.tif", Tiff(40, 30, xRes: 1000, yRes: 1)).Single();
            Check(page.PixelWidth == 40 && page.PixelHeight == 30, $"kept its pixels ({page.PixelWidth}x{page.PixelHeight})");
        }

        [Test("TIFF stored sideways is turned, not mirrored")]
        static void TiffOrientation()
        {
            // Stored rows with a dark pixel at the stored top-left corner; orientation 6 = turn 90° clockwise to view.
            var page = Import("sideways.tif", Tiff(40, 20, orientation: Orientation.RIGHTTOP)).Single();
            Check(page.Rotation == 90, $"rotation {page.Rotation}");
            Check(page.PixelWidth == 40 && page.PixelHeight == 20, "pixels kept as stored");
            var (b, _, _) = TestImages.Pixel(page.Jpeg, 1, 1);
            Check(b < 60, $"stored top-left corner still top-left in the pixels ({b})");
        }

        [Test("TIFF: all eight orientations come out upright and unmirrored")]
        static void TiffAllOrientations()
        {
            // The picture as it should look: 40x24, black top-left corner, gray top-right corner, white elsewhere.
            const int w = 40, h = 24;
            byte Visual(int x, int y) => x < 4 && y < 4 ? (byte)0 : x >= w - 4 && y < 4 ? (byte)128 : (byte)255;
            for (var o = 1; o <= 8; o++)
            {
                var sideways = o >= 5;
                int sw = sideways ? h : w, sh = sideways ? w : h;
                // Stored pixel (c, r) per the TIFF meaning of each orientation (where stored row 0 / column 0 are).
                byte Stored(int c, int r)
                {
                    switch (o)
                    {
                        case 1: return Visual(c, r);
                        case 2: return Visual(w - 1 - c, r);
                        case 3: return Visual(w - 1 - c, h - 1 - r);
                        case 4: return Visual(c, h - 1 - r);
                        case 5: return Visual(r, c);
                        case 6: return Visual(w - 1 - r, c);
                        case 7: return Visual(w - 1 - r, h - 1 - c);
                        default: return Visual(r, h - 1 - c);
                    }
                }
                var page = Import($"orientation-{o}.tif", TiffFrom(sw, sh, Stored, (Orientation)o)).Single();
                var view = Upright(page);
                var where = $"orientation {o} (rotation {page.Rotation})";
                Check(view.GetLength(0) == w && view.GetLength(1) == h, $"{where}: {view.GetLength(0)}x{view.GetLength(1)}");
                Check(view[1, 1] < 60, $"{where}: black corner top-left ({view[1, 1]})");
                Check(view[w - 2, 1] > 80 && view[w - 2, 1] < 180, $"{where}: gray corner top-right ({view[w - 2, 1]})");
                Check(view[1, h - 2] > 200 && view[w - 2, h - 2] > 200, $"{where}: bottom corners white");
            }
        }

        [Test("Pictures with misleading names or text are still read as pictures")]
        static void MisleadingNames()
        {
            var jpeg = TestImages.Jpeg(64, 48, true);
            Check(Import("photo-named.pdf", jpeg).Single().PixelWidth == 64, "JPEG named .pdf");
            Check(Import("photo-named.tif", jpeg).Single().PixelWidth == 64, "JPEG named .tif");
            // A comment segment that mentions "%PDF-" right after the JPEG start.
            var comment = Encoding.ASCII.GetBytes("Scanned from %PDF-1.7 workflow");
            var com = new byte[] { 0xFF, 0xFE, (byte)((comment.Length + 2) >> 8), (byte)(comment.Length + 2) }.Concat(comment);
            var tagged = jpeg.Take(2).Concat(com).Concat(jpeg.Skip(2)).ToArray();
            Check(Import("pdf-comment.jpg", tagged).Single().PixelWidth == 64, "JPEG mentioning %PDF- in a comment");
            Check(ImportError("broken.pdf", new byte[] { 1, 2, 3, 4, 5 }).Contains("PDF"), "unreadable .pdf says PDF");
        }

        /// <summary>A page's pixels as gray values, turned clockwise by its rotation: [x, y] as a viewer shows it.</summary>
        static byte[,] Upright(PageImage page)
        {
            using (var bitmap = SKBitmap.Decode(page.Jpeg))
            {
                int w = bitmap.Width, h = bitmap.Height;
                var turns = page.Rotation / 90;
                var outW = turns % 2 == 0 ? w : h;
                var outH = turns % 2 == 0 ? h : w;
                var view = new byte[outW, outH];
                for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                    {
                        int vx = x, vy = y;
                        for (var t = 0; t < turns; t++)
                        {
                            // One clockwise quarter turn of the current (width, height) frame.
                            var height = t % 2 == 0 ? h : w;
                            (vx, vy) = (height - 1 - vy, vx);
                        }
                        view[vx, vy] = bitmap.GetPixel(x, y).Red;
                    }
                return view;
            }
        }

        static byte[] TiffFrom(int width, int height, Func<int, int, byte> pixel, Orientation orientation)
        {
            using (var stream = new MemoryStream())
            {
                using (var tif = BitMiracle.LibTiff.Classic.Tiff.ClientOpen("t.tif", "w", stream, new TiffStream()))
                {
                    tif.SetField(TiffTag.IMAGEWIDTH, width);
                    tif.SetField(TiffTag.IMAGELENGTH, height);
                    tif.SetField(TiffTag.BITSPERSAMPLE, 8);
                    tif.SetField(TiffTag.SAMPLESPERPIXEL, 1);
                    tif.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);
                    tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
                    tif.SetField(TiffTag.ROWSPERSTRIP, height);
                    tif.SetField(TiffTag.ORIENTATION, orientation);
                    var row = new byte[width];
                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++) row[x] = pixel(x, y);
                        tif.WriteScanline(row, y);
                    }
                    tif.WriteDirectory();
                }
                return stream.ToArray();
            }
        }

        [Test("TIFF without a resolution unit keeps the pixels' shape")]
        static void TiffAspectOnly()
        {
            var page = Import("fax-nounit.tif", Tiff(200, 100, xRes: 204, yRes: 98, unit: ResUnit.NONE)).Single();
            Check(Math.Abs((double)page.PixelHeight / page.PixelWidth - 100.0 * 204 / 98 / 200) < 0.02, $"stretched to square pixels ({page.PixelWidth}x{page.PixelHeight})");
        }

        [Test("A damaged TIFF gives a Korean message")]
        static void DamagedTiff()
        {
            var junk = new byte[300];
            new Random(7).NextBytes(junk);
            junk[0] = (byte)'I'; junk[1] = (byte)'I'; junk[2] = 42; junk[3] = 0;
            var message = ImportError("damaged.tif", junk);
            Check(message.Contains("TIFF"), "message: " + message);
        }

        [Test("Files are read by their content, not their name")]
        static void SniffByContent()
        {
            var pages = Import("really-a-tiff.jpg", Tiff(30, 20));
            Check(pages.Count == 1 && pages[0].PixelWidth == 30, "TIFF named .jpg imports");
        }

        [Test("JPEG resolution: EXIF wins over JFIF, as in 1.0.1")]
        static void ExifBeforeJfif()
        {
            var jpeg = Fixtures.Bytes("scan-gray-300.jpg"); // JFIF says 300 dpi
            var withExif = jpeg.Take(2).Concat(ExifApp1(150)).Concat(jpeg.Skip(2)).ToArray();
            Check(ImageDensity.Read(withExif) == (150, 150), $"EXIF 150 dpi used (got {ImageDensity.Read(withExif)})");
            Check(ImageDensity.Read(jpeg) == (300, 300), "JFIF alone still read");
        }

        [Test("Thumbnails of big pictures stay small")]
        static void BigThumbnail()
        {
            var page = TestImages.Page(4000, 3000, true, 300);
            Check(page.ThumbnailWidth >= 200 && page.ThumbnailWidth < 400, $"thumbnail {page.ThumbnailWidth}x{page.ThumbnailHeight}");
            Check(Math.Abs((double)page.ThumbnailHeight / page.ThumbnailWidth - 0.75) < 0.02, "shape kept");
        }

        [Test("An mDNS flood is resolved quickly and capped")]
        static void MdnsFlood()
        {
            var responses = new List<MdnsResponse>();
            for (var p = 0; p < 64; p++)
            {
                var message = new MdnsMessage { IsResponse = true };
                for (var i = 0; i < 100; i++)
                {
                    var instance = $"Fake {p}-{i}._uscan._tcp.local";
                    message.Records.Add(new MdnsRecord { Name = "_uscan._tcp.local", Type = MdnsRecord.TypePtr, Ttl = 120, Target = instance });
                    message.Records.Add(new MdnsRecord { Name = instance, Type = MdnsRecord.TypeSrv, Ttl = 120, Target = $"host{p}-{i}.local", Port = 80 });
                }
                responses.Add(new MdnsResponse { Message = message, Source = IPAddress.Parse("10.0.0.9") });
            }
            var clock = Stopwatch.StartNew();
            var services = MdnsBrowser.Resolve(responses);
            Check(clock.ElapsedMilliseconds < 1000, $"resolved in {clock.ElapsedMilliseconds} ms");
            Check(services.Count <= 32, $"at most 32 services (got {services.Count})");
        }

        // ───────────── builders ─────────────

        static byte[] Tiff(int width, int height, float xRes = 100, float yRes = 100, ResUnit unit = ResUnit.INCH, Orientation orientation = Orientation.TOPLEFT)
        {
            using (var stream = new MemoryStream())
            {
                using (var tif = BitMiracle.LibTiff.Classic.Tiff.ClientOpen("t.tif", "w", stream, new TiffStream()))
                {
                    tif.SetField(TiffTag.IMAGEWIDTH, width);
                    tif.SetField(TiffTag.IMAGELENGTH, height);
                    tif.SetField(TiffTag.BITSPERSAMPLE, 8);
                    tif.SetField(TiffTag.SAMPLESPERPIXEL, 1);
                    tif.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);
                    tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
                    tif.SetField(TiffTag.ROWSPERSTRIP, height);
                    tif.SetField(TiffTag.XRESOLUTION, xRes);
                    tif.SetField(TiffTag.YRESOLUTION, yRes);
                    tif.SetField(TiffTag.RESOLUTIONUNIT, unit);
                    tif.SetField(TiffTag.ORIENTATION, orientation);
                    var row = new byte[width];
                    for (var y = 0; y < height; y++)
                    {
                        // White, except a dark 4x4 block in the stored top-left corner.
                        for (var x = 0; x < width; x++) row[x] = (byte)(x < 4 && y < 4 ? 0 : 255);
                        tif.WriteScanline(row, y);
                    }
                    tif.WriteDirectory();
                }
                return stream.ToArray();
            }
        }

        static byte[] Png(int width, int height)
        {
            var ihdr = new byte[13];
            ihdr[0] = (byte)(width >> 24); ihdr[1] = (byte)(width >> 16); ihdr[2] = (byte)(width >> 8); ihdr[3] = (byte)width;
            ihdr[4] = (byte)(height >> 24); ihdr[5] = (byte)(height >> 16); ihdr[6] = (byte)(height >> 8); ihdr[7] = (byte)height;
            ihdr[8] = 8; // 8-bit gray
            return new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 }
                .Concat(Chunk("IHDR", ihdr)).Concat(Chunk("IDAT", new byte[] { 0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 }))
                .Concat(Chunk("IEND", new byte[0])).ToArray();
        }

        static byte[] Chunk(string type, byte[] data)
        {
            var typeBytes = Encoding.ASCII.GetBytes(type);
            var crc = Crc32(typeBytes.Concat(data).ToArray());
            return new[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length }
                .Concat(typeBytes).Concat(data)
                .Concat(new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc }).ToArray();
        }

        static uint Crc32(byte[] bytes)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in bytes)
            {
                crc ^= b;
                for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
            return ~crc;
        }

        /// <summary>An APP1 EXIF block (little-endian) whose IFD0 says <paramref name="dpi"/> dots per inch.</summary>
        static byte[] ExifApp1(uint dpi)
        {
            var tiff = new List<byte> { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0 };
            void U16(int v) { tiff.Add((byte)v); tiff.Add((byte)(v >> 8)); }
            void U32(uint v) { tiff.Add((byte)v); tiff.Add((byte)(v >> 8)); tiff.Add((byte)(v >> 16)); tiff.Add((byte)(v >> 24)); }
            const int entries = 3, ifdSize = 2 + entries * 12 + 4;
            uint rational = 8 + ifdSize;
            U16(entries);
            U16(0x011A); U16(5); U32(1); U32(rational);
            U16(0x011B); U16(5); U32(1); U32(rational + 8);
            U16(0x0128); U16(3); U32(1); U16(2); U16(0);
            U32(0);
            U32(dpi); U32(1); U32(dpi); U32(1);
            var payload = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray();
            var length = payload.Length + 2;
            return new byte[] { 0xFF, 0xE1, (byte)(length >> 8), (byte)length }.Concat(payload).ToArray();
        }
    }
}
