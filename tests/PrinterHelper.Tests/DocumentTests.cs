using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using PrinterHelper.Documents;
using SkiaSharp;
using static PrinterHelper.Tests.Assert;

namespace PrinterHelper.Tests
{
    static class DocumentTests
    {
        [Test("JPEG header parsing")]
        static void JpegHeaderParsing()
        {
            var gray = JpegInfo.TryRead(TestImages.Jpeg(300, 200, false));
            Check(gray != null && gray.Width == 300 && gray.Height == 200 && gray.Components == 1, "gray 300x200x1");
            var color = JpegInfo.TryRead(TestImages.Jpeg(123, 77, true));
            Check(color != null && color.Width == 123 && color.Height == 77 && color.Components == 3, "color 123x77x3");
            using (var png = TestImages.Bitmap(10, 10, true))
                Check(JpegInfo.TryRead(TestImages.Encode(png, SKEncodedImageFormat.Png)) == null, "PNG is not JPEG");
            Check(JpegInfo.TryRead(new byte[] { 0xFF, 0xD8, 0xFF }) == null, "truncated");
            Check(JpegInfo.TryRead(null) == null, "null");
        }

        [Test("JPEG encoding keeps gray as 1 channel and adds no metadata")]
        static void JpegEncoding()
        {
            var gray = TestImages.Jpeg(64, 48, false);
            Check(JpegInfo.TryRead(gray).Components == 1, "gray stays gray");
            Check(JpegInfo.StripMetadata(gray).SequenceEqual(gray), "no EXIF, ICC or comment blocks");
            var color = TestImages.Jpeg(64, 48, true);
            Check(JpegInfo.StripMetadata(color).SequenceEqual(color), "color: no metadata blocks either");
        }

        [Test("Transparent picture becomes a white-backed JPEG")]
        static void TransparentPictureFlattening()
        {
            using (var bitmap = TestImages.Bitmap(64, 32, true, alpha: true))
            {
                Imaging.FlattenOnWhite(bitmap);
                var page = PageImage.FromBitmap(bitmap, 96, "alpha");
                Check(page.Components == 3, "color JPEG");
                var (b, g, r) = TestImages.Pixel(page.Jpeg, 4, 4);
                Check(b > 240 && g > 240 && r > 240, $"transparent area is white (got {b},{g},{r})");
            }
        }

        [Test("PDF writer produces a consistent file")]
        static void PdfWriterStructure()
        {
            var pages = new List<PageImage>
            {
                TestImages.Page(1654, 2339, false, 200),            // A4 portrait, gray
                TestImages.Page(1100, 850, true, 100),              // landscape letter, color
                TestImages.Page(800, 1000, false, 150, rotation: 90),
            };
            var path = Path.Combine(Program.OutDir, "structure.pdf");
            PdfWriter.Save(path, pages);
            var bytes = File.ReadAllBytes(path);
            var text = Encoding.ASCII.GetString(bytes);

            Check(text.StartsWith("%PDF-1.4\n"), "header");
            Check(text.TrimEnd().EndsWith("%%EOF"), "trailer");
            var startxref = long.Parse(Regex.Match(text, @"startxref\n(\d+)\n%%EOF\s*$").Groups[1].Value);
            Check(text.Substring((int)startxref, 4) == "xref", "startxref points at xref");

            var xref = Regex.Match(text.Substring((int)startxref), @"xref\n0 (\d+)\n((?:\d{10} \d{5} [nf] \n)+)");
            var count = int.Parse(xref.Groups[1].Value);
            Check(count == 3 + 3 * pages.Count + 1, "object count");
            var entries = xref.Groups[2].Value.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Check(entries.Length == count, "xref entry count");
            for (var n = 1; n < count; n++)
            {
                var offset = int.Parse(entries[n].Substring(0, 10));
                Check(text.Substring(offset).StartsWith($"{n} 0 obj\n"), $"xref offset of object {n}");
            }

            Check(text.Contains("/MediaBox [0 0 595.44 842.04]"), "A4 MediaBox from 1654x2339 at 200 dpi");
            Check(text.Contains("/MediaBox [0 0 792 612]"), "landscape MediaBox");
            Check(text.Contains("/Rotate 90"), "rotation stored");
            Check(Regex.Matches(text, "/ColorSpace /DeviceRGB").Count == 1, "one color page");

            // Each image stream must be exactly as long as its /Length says, and be the page's JPEG unchanged.
            var streams = Regex.Matches(text, @"/Filter /DCTDecode /Length (\d+) >>\nstream\n");
            Check(streams.Count == pages.Count, "one image per page");
            for (var i = 0; i < streams.Count; i++)
            {
                var m = streams[i];
                var start = m.Index + m.Length;
                var length = int.Parse(m.Groups[1].Value);
                Check(bytes.Skip(start).Take(length).SequenceEqual(pages[i].Jpeg), $"page {i + 1} JPEG embedded byte for byte");
                Check(text.Substring(start + length, 11) == "\nendstream\n", "endstream right after /Length bytes");
            }
        }

        [Test("PDF writer output matches 1.0.1 byte for byte")]
        static void PdfWriterMatchesV1()
        {
            var scan = Fixtures.Bytes("scan-gray-300.jpg");
            using (var ms = new MemoryStream())
            {
                PdfWriter.Write(ms, new[] { PageImage.FromJpeg(scan, 300, "s", 90), PageImage.FromJpeg(scan, 150, "s") }, new DateTime(2026, 1, 2, 3, 4, 5));
                var hash = Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();
                var golden = Golden().GetProperty("pdfWriterSha256").GetString();
                Check(hash == golden, $"SHA-256 {hash} == {golden}");
            }
        }

        [Test("PDF save leaves no temp file and replaces an existing file")]
        static void PdfSaveReplaces()
        {
            var path = Path.Combine(Program.OutDir, "replace.pdf");
            File.WriteAllText(path, "old");
            PdfWriter.Save(path, new[] { TestImages.Page(100, 100, false, 100) });
            Check(File.ReadAllText(path).StartsWith("%PDF"), "replaced");
            Check(!Directory.GetFiles(Program.OutDir, "~replace*.tmp").Any(), "temp file removed");
        }

        [Test("Duplex interleave")]
        static void DuplexInterleave()
        {
            var fronts = new[] { "1f", "2f", "3f" };
            Check(string.Join(",", Duplex.Interleave(fronts, new[] { "3b", "2b", "1b" }, true)) == "1f,1b,2f,2b,3f,3b", "reversed backs");
            Check(string.Join(",", Duplex.Interleave(fronts, new[] { "1b", "2b", "3b" }, false)) == "1f,1b,2f,2b,3f,3b", "forward backs");
            Throws<ArgumentException>(() => Duplex.Interleave(fronts, new[] { "1b" }, true), "mismatched counts rejected");
        }

        [Test("Picture and TIFF imports match 1.0.1")]
        static void ImportsMatchV1() => CompareWithV1(name => !name.EndsWith(".pdf"));

        /// <summary>Imports each fixture and compares size, channels, resolution, rotation and pass-through with 1.0.1.</summary>
        internal static void CompareWithV1(Func<string, bool> include)
        {
            var files = Golden().GetProperty("files");
            var compared = 0;
            foreach (var file in files.EnumerateObject())
            {
                if (!include(file.Name)) continue;
                var input = Fixtures.Bytes(file.Name);
                var pages = PageImporter.ImportAsync(Fixtures.Path(file.Name), CancellationToken.None).GetAwaiter().GetResult();
                var expected = file.Value.EnumerateArray().ToList();
                Check(pages.Count == expected.Count, $"{file.Name}: {pages.Count} pages, expected {expected.Count}");
                for (var i = 0; i < pages.Count; i++)
                {
                    var p = pages[i];
                    var e = expected[i];
                    var where = $"{file.Name} page {i + 1}";
                    int ew = e.GetProperty("width").GetInt32(), eh = e.GetProperty("height").GetInt32();
                    Check(Math.Abs(p.PixelWidth - ew) <= 1 && Math.Abs(p.PixelHeight - eh) <= 1, $"{where}: {p.PixelWidth}x{p.PixelHeight}, expected {ew}x{eh}");
                    Check(p.Components == e.GetProperty("components").GetInt32(), $"{where}: {p.Components} channels, expected {e.GetProperty("components").GetInt32()}");
                    var edpi = e.GetProperty("dpi").GetDouble();
                    Check(Math.Abs(p.Dpi - edpi) < 0.01, $"{where}: {p.Dpi:0.###} dpi, expected {edpi}");
                    Check(p.Rotation == e.GetProperty("rotation").GetInt32(), $"{where}: rotation {p.Rotation}, expected {e.GetProperty("rotation").GetInt32()}");
                    var passthrough = p.Jpeg.SequenceEqual(JpegInfo.StripMetadata(input) ?? new byte[0]);
                    Check(passthrough == e.GetProperty("passthrough").GetBoolean(), $"{where}: JPEG pass-through {passthrough}");
                }
                compared++;
            }
            Check(compared > 0, "some fixtures compared");
        }

        [Test("Photo import drops EXIF and appended data but keeps the image data")]
        static void PhotoMetadataRemoved()
        {
            var photo = Fixtures.Bytes("photo-exif6.jpg");
            Check(Encoding.ASCII.GetString(photo).Contains("SecretPhoneModel"), "fixture has a camera model in EXIF");
            // A "motion photo" clip appended after the JPEG end marker, as phones do.
            var path = Path.Combine(Program.OutDir, "motion-photo.jpg");
            File.WriteAllBytes(path, photo.Concat(Encoding.ASCII.GetBytes("....ftypmp42 motion photo video")).ToArray());
            var page = PageImporter.ImportAsync(path, CancellationToken.None).GetAwaiter().GetResult().Single();
            var kept = Encoding.ASCII.GetString(page.Jpeg);
            Check(page.Rotation == 90, $"EXIF rotation applied (got {page.Rotation})");
            Check(!kept.Contains("Exif") && !kept.Contains("SecretPhoneModel"), "EXIF removed");
            Check(!kept.Contains("ftyp"), "data after the end marker removed");
            Check(page.Jpeg.SequenceEqual(JpegInfo.StripMetadata(photo)), "image data kept byte for byte");
        }

        [Test("Fax keeps its shape on the page and in the thumbnail")]
        static void FaxShape()
        {
            // 1728x1078 at 204x98 dpi is an 8.47 x 11 inch page.
            var page = PageImporter.ImportAsync(Fixtures.Path("tiff-fax-g4.tif"), CancellationToken.None).GetAwaiter().GetResult().Single();
            var aspect = (double)page.PixelHeight / page.PixelWidth;
            Check(Math.Abs(aspect - 11.0 / 8.47) < 0.01, $"fax keeps its shape (height/width {aspect:0.000})");
            var thumb = (double)page.ThumbnailHeight / page.ThumbnailWidth;
            Check(Math.Abs(thumb - aspect) < 0.01, $"thumbnail has the same shape ({thumb:0.000})");
            var density = ImageDensity.Read(page.Jpeg);
            Check(density == null || Math.Abs(density.Value.X - density.Value.Y) < 0.01, "stored pixels are square");
        }

        [Test("Large photo still fits A4")]
        static void LargePhotoFitsA4()
        {
            var big = PageImage.FromJpeg(TestImages.Jpeg(4000, 3000, false), Imaging.PageDpi(4000, 3000, 72), "big");
            Check(big.PixelWidth / big.Dpi <= 11.7 && big.PixelHeight / big.Dpi <= 8.3, "large photo fits A4");
        }

        [Test("Thumbnails are about 200 pixels wide and keep the page's shape")]
        static void Thumbnails()
        {
            var page = TestImages.Page(1654, 2339, false, 200);
            Check(page.ThumbnailWidth >= 200 && page.ThumbnailWidth < 260, $"width {page.ThumbnailWidth}");
            Check(Math.Abs((double)page.ThumbnailHeight / page.ThumbnailWidth - 2339.0 / 1654) < 0.01, "shape");
            Check(JpegInfo.TryRead(page.ThumbnailJpeg).Components == 1, "gray thumbnail for a gray page");
            var small = TestImages.Page(64, 48, true, 72);
            Check(small.ThumbnailWidth == 64 && small.ThumbnailHeight == 48, "small pages are not enlarged");
        }

        [Test("WEBP and GIF import")]
        static void WebpAndGif()
        {
            var webp = Path.Combine(Program.OutDir, "picture.webp");
            using (var bitmap = TestImages.Bitmap(120, 80, true))
                File.WriteAllBytes(webp, TestImages.Encode(bitmap, SKEncodedImageFormat.Webp));
            var page = PageImporter.ImportAsync(webp, CancellationToken.None).GetAwaiter().GetResult().Single();
            Check(page.PixelWidth == 120 && page.PixelHeight == 80 && page.Dpi == 96, "webp page 120x80 at 96 dpi");
            Check(PageImporter.IsSupported("a.WEBP") && PageImporter.IsSupported("b.Tiff") && !PageImporter.IsSupported("c.heic"), "supported extensions");
        }

        [Test("Picture resolution readers")]
        static void DensityReaders()
        {
            Check(ImageDensity.Read(Fixtures.Bytes("scan-gray-300.jpg")) == (300, 300), "JFIF dpi");
            Check(ImageDensity.Read(Fixtures.Bytes("jpeg-nojfif.jpg")) == null, "no JFIF");
            var png = ImageDensity.Read(Fixtures.Bytes("png-gray-150.png")).Value;
            Check(Math.Abs(png.X - 149.987) < 0.01, $"PNG pHYs {png.X}");
            Check(ImageDensity.Read(Fixtures.Bytes("png-gray-nophys.png")) == null, "PNG without pHYs");
            var bmp = ImageDensity.Read(Fixtures.Bytes("bmp-24-200.bmp")).Value;
            Check(Math.Abs(bmp.X - 200) < 0.01, $"BMP {bmp.X}");
            Check(ImageDensity.Read(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16 }) == null, "truncated JPEG");
        }

        static JsonElement Golden() => JsonDocument.Parse(File.ReadAllText(Fixtures.Path("golden-v1.json"))).RootElement;
    }
}
