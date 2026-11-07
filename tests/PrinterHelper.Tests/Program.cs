using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;
using PrinterHelper.Documents;
using PrinterHelper.Escl;
using PrinterHelper.Ipp;
using PrinterHelper.Printing;

namespace PrinterHelper.Tests
{
    /// <summary>
    /// Self-contained checks. Run with no arguments for offline tests, add --live to also read
    /// status from a real printer (read-only: never starts a scan), e.g. --live 192.168.0.48.
    /// Pass --out DIR to keep the generated PDFs for external validation.
    /// </summary>
    static class Program
    {
        static int failures;
        static string outDir;

        [STAThread]
        static int Main(string[] args)
        {
            outDir = ArgValue(args, "--out") ?? Path.Combine(Path.GetTempPath(), "PrinterHelperTests");
            Directory.CreateDirectory(outDir);

            Run("JPEG header parsing", JpegHeaderParsing);
            Run("Transparent PNG becomes white-backed JPEG", TransparentPictureFlattening);
            Run("PDF writer produces a consistent file", PdfWriterStructure);
            Run("PDF save leaves no temp file and replaces existing", PdfSaveReplaces);
            Run("Duplex interleave", DuplexInterleave);
            Run("Picture import: PNG, multi-page TIFF, EXIF rotation", PictureImport);
            Run("PDF import renders every page", PdfImport);
            Run("Print layout fits pages and turns landscape", PrintLayout);
            Run("IPP request/response round trip", IppRoundTrip);
            Run("eSCL job path normalization", JobPathNormalization);
            Run("eSCL scan against fake scanner", () => FakeScan().GetAwaiter().GetResult());
            Run("eSCL cancel sends DELETE", () => FakeScanCancel().GetAwaiter().GetResult());

            var live = ArgValue(args, "--live");
            if (live != null) Run($"Live read-only status from {live}", () => LiveStatus(live).GetAwaiter().GetResult());

            Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
            Console.WriteLine("output: " + outDir);
            return failures == 0 ? 0 : 1;
        }

        static string ArgValue(string[] args, string name)
        {
            var i = Array.IndexOf(args, name);
            if (i < 0) return null;
            return i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : "192.168.0.48";
        }

        static void Run(string name, Action test)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                test();
                Console.WriteLine($"PASS  {name} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"FAIL  {name}: {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}");
                Console.WriteLine(ex.GetBaseException().StackTrace);
            }
        }

        static void Check(bool condition, string what)
        {
            if (!condition) throw new Exception("check failed: " + what);
        }

        // ───────────── helpers ─────────────

        static BitmapSource MakeBitmap(int width, int height, bool color, bool alpha = false)
        {
            var format = alpha ? PixelFormats.Bgra32 : color ? PixelFormats.Bgr24 : PixelFormats.Gray8;
            var bpp = format.BitsPerPixel / 8;
            var stride = width * bpp;
            var data = new byte[stride * height];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var i = y * stride + x * bpp;
                    var v = (byte)((x * 255 / Math.Max(1, width - 1)) ^ (y % 32 < 16 ? 0 : 0x40));
                    for (var c = 0; c < bpp; c++) data[i + c] = v;
                    if (alpha) data[i + 3] = (byte)(x < width / 2 ? 0 : 255); // left half fully transparent
                }
            var bmp = BitmapSource.Create(width, height, 96, 96, format, null, data, stride);
            bmp.Freeze();
            return bmp;
        }

        static byte[] Encode(BitmapEncoder encoder, params BitmapSource[] frames)
        {
            foreach (var f in frames) encoder.Frames.Add(BitmapFrame.Create(f));
            using (var ms = new MemoryStream())
            {
                encoder.Save(ms);
                return ms.ToArray();
            }
        }

        static bool SamePixels(byte[] jpegA, byte[] jpegB)
        {
            byte[] Pixels(byte[] jpeg)
            {
                var bmp = new FormatConvertedBitmap(Imaging.Decode(jpeg), PixelFormats.Bgr24, null, 0);
                var data = new byte[bmp.PixelWidth * bmp.PixelHeight * 3];
                bmp.CopyPixels(data, bmp.PixelWidth * 3, 0);
                return data;
            }
            return Pixels(jpegA).SequenceEqual(Pixels(jpegB));
        }

        static PageImage Page(int width, int height, bool color, double dpi, int rotation = 0) =>
            PageImage.FromJpeg(Encode(new JpegBitmapEncoder(), MakeBitmap(width, height, color)), dpi, "test", rotation);

        // ───────────── tests ─────────────

        static void JpegHeaderParsing()
        {
            var gray = JpegInfo.TryRead(Encode(new JpegBitmapEncoder(), MakeBitmap(300, 200, false)));
            Check(gray != null && gray.Width == 300 && gray.Height == 200 && gray.Components == 1, "gray 300x200x1");
            var color = JpegInfo.TryRead(Encode(new JpegBitmapEncoder(), MakeBitmap(123, 77, true)));
            Check(color != null && color.Width == 123 && color.Height == 77 && color.Components == 3, "color 123x77x3");
            Check(JpegInfo.TryRead(Encode(new PngBitmapEncoder(), MakeBitmap(10, 10, true))) == null, "PNG is not JPEG");
            Check(JpegInfo.TryRead(new byte[] { 0xFF, 0xD8, 0xFF }) == null, "truncated");
            Check(JpegInfo.TryRead(null) == null, "null");
        }

        static void TransparentPictureFlattening()
        {
            var page = PageImage.FromBitmap(MakeBitmap(64, 32, true, alpha: true), 96, "alpha");
            Check(page.Components == 3, "color JPEG");
            var decoded = new FormatConvertedBitmap(Imaging.Decode(page.Jpeg), PixelFormats.Bgr24, null, 0);
            var pixel = new byte[3];
            decoded.CopyPixels(new Int32Rect(4, 4, 1, 1), pixel, 3, 0);
            Check(pixel.All(v => v > 240), $"transparent area is white (got {pixel[0]},{pixel[1]},{pixel[2]})");
        }

        static void PdfWriterStructure()
        {
            var pages = new List<PageImage>
            {
                Page(1654, 2339, false, 200),            // A4 portrait, gray
                Page(1100, 850, true, 100),              // landscape letter, color
                Page(800, 1000, false, 150, rotation: 90),
            };
            var path = Path.Combine(outDir, "structure.pdf");
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

            // Each image stream must be exactly as long as its /Length says.
            foreach (Match m in Regex.Matches(text, @"/Filter /DCTDecode /Length (\d+) >>\nstream\n"))
            {
                var start = m.Index + m.Length;
                var length = int.Parse(m.Groups[1].Value);
                Check(bytes[start] == 0xFF && bytes[start + 1] == 0xD8, "stream starts with JPEG SOI");
                Check(bytes[start + length - 2] == 0xFF && bytes[start + length - 1] == 0xD9, "stream ends with JPEG EOI");
                Check(text.Substring(start + length, 11) == "\nendstream\n", "endstream right after /Length bytes");
            }
        }

        static void PdfSaveReplaces()
        {
            var path = Path.Combine(outDir, "replace.pdf");
            File.WriteAllText(path, "old");
            PdfWriter.Save(path, new[] { Page(100, 100, false, 100) });
            Check(File.ReadAllText(path).StartsWith("%PDF"), "replaced");
            Check(!Directory.GetFiles(outDir, "~replace*.tmp").Any(), "temp file removed");
        }

        static void DuplexInterleave()
        {
            var fronts = new[] { "1f", "2f", "3f" };
            Check(string.Join(",", Duplex.Interleave(fronts, new[] { "3b", "2b", "1b" }, true)) == "1f,1b,2f,2b,3f,3b", "reversed backs");
            Check(string.Join(",", Duplex.Interleave(fronts, new[] { "1b", "2b", "3b" }, false)) == "1f,1b,2f,2b,3f,3b", "forward backs");
            var threw = false;
            try { Duplex.Interleave(fronts, new[] { "1b" }, true); } catch (ArgumentException) { threw = true; }
            Check(threw, "mismatched counts rejected");
        }

        static void PictureImport()
        {
            var png = Path.Combine(outDir, "picture.png");
            File.WriteAllBytes(png, Encode(new PngBitmapEncoder(), MakeBitmap(400, 300, true, alpha: true)));
            var pngPages = PageImporter.ImportAsync(png, CancellationToken.None).GetAwaiter().GetResult();
            Check(pngPages.Count == 1 && pngPages[0].PixelWidth == 400 && pngPages[0].Components == 3, "PNG page");

            var tiff = Path.Combine(outDir, "pages.tif");
            File.WriteAllBytes(tiff, Encode(new TiffBitmapEncoder(), MakeBitmap(200, 280, false), MakeBitmap(280, 200, false)));
            var tiffPages = PageImporter.ImportAsync(tiff, CancellationToken.None).GetAwaiter().GetResult();
            Check(tiffPages.Count == 2, "two TIFF pages");
            Check(tiffPages[1].PixelWidth == 280, "second TIFF page size");

            // A phone photo stored sideways with EXIF orientation 6 (= rotate 90° clockwise), a camera
            // model in EXIF, and a "motion photo" clip appended after the JPEG end marker.
            var encoder = new JpegBitmapEncoder();
            var metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
            metadata.SetQuery("/app1/ifd/{ushort=272}", "SecretPhoneModel");
            encoder.Frames.Add(BitmapFrame.Create(MakeBitmap(640, 480, true), null, metadata, null));
            var photo = Path.Combine(outDir, "photo.jpg");
            byte[] photoBytes;
            using (var ms = new MemoryStream()) { encoder.Save(ms); photoBytes = ms.ToArray(); }
            var trailer = Encoding.ASCII.GetBytes("....ftypmp42 motion photo video");
            File.WriteAllBytes(photo, photoBytes.Concat(trailer).ToArray());
            var photoPages = PageImporter.ImportAsync(photo, CancellationToken.None).GetAwaiter().GetResult();
            Check(photoPages.Count == 1 && photoPages[0].Rotation == 90, $"EXIF rotation applied (got {photoPages[0].Rotation})");
            var kept = Encoding.ASCII.GetString(photoPages[0].Jpeg);
            Check(!kept.Contains("Exif") && !kept.Contains("SecretPhoneModel"), "EXIF removed");
            Check(!kept.Contains("ftyp"), "data after the end marker removed");
            var jpegEnd = photoPages[0].Jpeg;
            Check(jpegEnd[jpegEnd.Length - 2] == 0xFF && jpegEnd[jpegEnd.Length - 1] == 0xD9, "ends at EOI");
            Check(SamePixels(photoBytes, photoPages[0].Jpeg), "image data kept without re-compression");

            // Standard-resolution fax: 204x98 dpi, 1728x1078 pixels = an 8.47 x 11 inch page.
            var fax = Path.Combine(outDir, "fax.tif");
            var faxBitmap = BitmapSource.Create(1728, 1078, 204, 98, PixelFormats.Gray8, null, new byte[1728 * 1078], 1728);
            File.WriteAllBytes(fax, Encode(new TiffBitmapEncoder(), faxBitmap));
            var faxPage = PageImporter.ImportAsync(fax, CancellationToken.None).GetAwaiter().GetResult().Single();
            var aspect = (double)faxPage.PixelHeight / faxPage.PixelWidth;
            Check(Math.Abs(aspect - 11.0 / 8.47) < 0.01, $"fax keeps its shape (height/width {aspect:0.000})");
            var shown = Imaging.Decode(faxPage.Jpeg);
            Check(Math.Abs(shown.Width / shown.Height - 1 / aspect) < 0.01, $"fax shown with the same shape on screen ({shown.DpiX}x{shown.DpiY} dpi)");

            // 4000-pixel photo at 72 dpi must still fit on A4.
            var big = PageImage.FromJpeg(Encode(new JpegBitmapEncoder(), MakeBitmap(4000, 3000, false)), Imaging.PageDpi(4000, 3000, 72), "big");
            Check(big.PixelWidth / big.Dpi <= 11.7 && big.PixelHeight / big.Dpi <= 8.3, "large photo fits A4");
        }

        static void PdfImport()
        {
            var source = Path.Combine(outDir, "import-source.pdf");
            PdfWriter.Save(source, new[] { Page(1654, 2339, false, 200), Page(1100, 850, true, 100), Page(800, 1000, false, 100, rotation: 90) });
            var pages = PageImporter.ImportAsync(source, CancellationToken.None).GetAwaiter().GetResult();
            Check(pages.Count == 3, $"three pages (got {pages.Count})");
            Check(Math.Abs(pages[0].PixelWidth / pages[0].Dpi - 8.27) < 0.05, $"page 1 width {pages[0].PixelWidth / pages[0].Dpi:0.00} in");
            Check(pages[1].PixelWidth > pages[1].PixelHeight, "page 2 landscape");
            // The /Rotate 90 page is rendered upright by Windows, so it comes back landscape with no extra rotation.
            Check(pages[2].PixelWidth > pages[2].PixelHeight && pages[2].Rotation == 0, "rotated page rendered upright");

            var roundTrip = Path.Combine(outDir, "import-roundtrip.pdf");
            PdfWriter.Save(roundTrip, pages);
        }

        static void PrintLayout()
        {
            var a4 = new Size(8.27 * 96, 11.69 * 96);
            var printable = new Rect(16, 16, a4.Width - 32, a4.Height - 32);
            var pages = new List<PageImage>
            {
                Page(1654, 2339, false, 200),            // A4: slightly larger than the printable area
                Page(1100, 850, true, 100),              // landscape: should be turned
                Page(400, 400, false, 200),              // 2x2 inch: printed at real size
            };
            var paginator = new PagePaginator(pages, a4, printable);
            Check(paginator.PageCount == 3, "page count");

            Rect ImageBounds(int n)
            {
                var visual = (System.Windows.Controls.Canvas)paginator.GetPage(n).Visual;
                var image = (System.Windows.Controls.Image)visual.Children[0];
                return new Rect(System.Windows.Controls.Canvas.GetLeft(image), System.Windows.Controls.Canvas.GetTop(image), image.Width, image.Height);
            }

            var first = ImageBounds(0);
            Check(printable.Contains(first) || Inflate(printable).Contains(first), "A4 scan inside printable area");
            Check(first.Width < 8.27 * 96 && first.Width > 7.5 * 96, "A4 scan shrunk only a little");
            var second = ImageBounds(1);
            Check(second.Height > second.Width, "landscape page turned to portrait");
            var third = ImageBounds(2);
            Check(Math.Abs(third.Width - 192) < 0.5 && Math.Abs(third.Height - 192) < 0.5, "small page printed at real size");

            // Write a real XPS file the way the print path does, and count its pages.
            var xpsPath = Path.Combine(outDir, "print.xps");
            if (File.Exists(xpsPath)) File.Delete(xpsPath);
            using (var xps = new XpsDocument(xpsPath, FileAccess.ReadWrite))
                XpsDocument.CreateXpsDocumentWriter(xps).Write(new PagePaginator(pages, a4, printable));
            using (var xps = new XpsDocument(xpsPath, FileAccess.Read))
            {
                var count = xps.GetFixedDocumentSequence().References.Sum(r => r.GetDocument(false).Pages.Count);
                Check(count == 3, $"XPS pages {count}");
            }
        }

        static Rect Inflate(Rect r)
        {
            r.Inflate(0.5, 0.5);
            return r;
        }

        static void IppRoundTrip()
        {
            var request = IppClient.BuildGetPrinterAttributes("ipp://10.0.0.5:631/ipp/print");
            Check(request[0] == 2 && request[1] == 0 && request[2] == 0 && request[3] == 0x0B, "IPP 2.0 Get-Printer-Attributes");
            Check(request[request.Length - 1] == 3, "ends with end-of-attributes");

            // Hand-built response: status ok, printer group with a few attributes.
            var ms = new MemoryStream();
            void Short(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
            void Attr(byte tag, string name, byte[] value)
            {
                var n = Encoding.UTF8.GetBytes(name);
                ms.WriteByte(tag); Short(n.Length); ms.Write(n, 0, n.Length); Short(value.Length); ms.Write(value, 0, value.Length);
            }
            byte[] Int(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
            ms.Write(new byte[] { 2, 0, 0, 0, 0, 0, 0, 1 }, 0, 8);
            ms.WriteByte(0x01);
            Attr(0x47, "attributes-charset", Encoding.ASCII.GetBytes("utf-8"));
            ms.WriteByte(0x04);
            Attr(0x41, "printer-make-and-model", Encoding.UTF8.GetBytes("M208x Series"));
            Attr(0x23, "printer-state", Int(3));
            Attr(0x44, "printer-state-reasons", Encoding.ASCII.GetBytes("toner-low-report"));
            Attr(0x44, "", Encoding.ASCII.GetBytes("media-empty-warning"));
            Attr(0x44, "marker-types", Encoding.ASCII.GetBytes("toner"));
            Attr(0x21, "marker-levels", Int(40));
            Attr(0x21, "marker-low-levels", Int(5));
            Attr(0x34, "media-col-ready", new byte[0]);     // a collection we must skip
            Attr(0x4A, "", Encoding.ASCII.GetBytes("media-size"));
            Attr(0x37, "", new byte[0]);
            Attr(0x44, "media-ready", Encoding.ASCII.GetBytes("iso_a4_210x297mm"));
            ms.WriteByte(0x03);

            var info = IppClient.ToPrinterInfo(IppClient.Parse(ms.ToArray()));
            Check(info.MakeAndModel == "M208x Series", "model");
            Check(info.State == 3, "state");
            Check(info.Reasons.SequenceEqual(new[] { "toner-low-report", "media-empty-warning" }), "reasons");
            Check(info.TonerLevel == 40 && info.TonerLowLevel == 5, "toner");
            Check(info.MediaReady.SequenceEqual(new[] { "iso_a4_210x297mm" }), "media-ready after collection");
        }

        static void JobPathNormalization()
        {
            Check(EsclClient.NormalizeJobPath("http://SEC001122334455.local./eSCL/ScanJobs/5") == "/eSCL/ScanJobs/5", "absolute .local");
            Check(EsclClient.NormalizeJobPath("/eSCL/ScanJobs/5/") == "/eSCL/ScanJobs/5", "trailing slash");
            Check(EsclClient.NormalizeJobPath("eSCL/ScanJobs/5") == "/eSCL/ScanJobs/5", "relative");
        }

        static byte[][] FakePages(int n) =>
            Enumerable.Range(0, n).Select(i => Encode(new JpegBitmapEncoder(), MakeBitmap(170 + i, 220, false))).ToArray();

        static async Task FakeScan()
        {
            using (var server = new FakeEsclServer(FakePages(3), busyAnswersPerPage: 2))
            using (var client = new EsclClient(server.Host))
            {
                var job = await client.CreateJobAsync(new ScanRequest { Source = ScanSource.Feeder, Dpi = 200, Width = 2480, Height = 3508 }, CancellationToken.None);
                Check(job == "/eSCL/ScanJobs/42", "job path from absolute .local Location");
                Check(server.LastScanSettings.Contains("<pwg:InputSource>Feeder</pwg:InputSource>"), "feeder requested");
                Check(server.LastScanSettings.Contains("<scan:ColorMode>Grayscale8</scan:ColorMode>"), "gray requested");

                var received = new List<byte[]>();
                byte[] page;
                while ((page = await client.NextDocumentAsync(job, CancellationToken.None)) != null) received.Add(page);
                Check(received.Count == 3, $"three pages (got {received.Count})");
                Check(received.Select(p => JpegInfo.TryRead(p).Width).SequenceEqual(new[] { 170, 171, 172 }), "pages in order");
                Check(server.Requests.Count(r => r.EndsWith("/NextDocument")) == 3 * 3 + 1, "retried on 503 then stopped at 404");

                var status = await client.GetStatusAsync(CancellationToken.None);
                Check(status.IsIdle && status.AdfLoaded, "status parsed");
                Check(status.FindJob(job)?.JobState == "Completed", "job found in status");
            }
        }

        static async Task FakeScanCancel()
        {
            using (var server = new FakeEsclServer(FakePages(1), busyAnswersPerPage: 0) { AlwaysBusy = true })
            using (var client = new EsclClient(server.Host))
            {
                var job = await client.CreateJobAsync(new ScanRequest { Source = ScanSource.Feeder, Dpi = 200, Width = 2480, Height = 3508 }, CancellationToken.None);
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5)))
                {
                    var canceled = false;
                    try { await client.NextDocumentAsync(job, cts.Token); }
                    catch (OperationCanceledException) { canceled = true; }
                    Check(canceled, "waiting for a busy page can be canceled");
                }
                await client.CancelJobAsync(job);
                Check(server.Requests.Contains("DELETE /eSCL/ScanJobs/42"), "DELETE sent");
            }
        }

        static async Task LiveStatus(string host)
        {
            using (var client = new EsclClient(host))
            {
                var caps = await client.GetCapabilitiesAsync(CancellationToken.None);
                Console.WriteLine($"      eSCL: {caps.MakeAndModel}, feeder={caps.HasFeeder} ({caps.FeederCapacity}), platen={caps.HasPlaten}, dpi={string.Join("/", caps.Resolutions)}, feeder max {caps.FeederMaxWidth}x{caps.FeederMaxHeight}");
                Check(caps.HasFeeder && caps.HasPlaten, "feeder and platen");
                var status = await client.GetStatusAsync(CancellationToken.None);
                Console.WriteLine($"      status: {status.State}, ADF {status.AdfState}, {status.Jobs.Count} jobs");
            }
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                var info = await IppClient.GetPrinterInfoAsync(http, host, CancellationToken.None);
                Console.WriteLine($"      IPP: {info.MakeAndModel}, state {info.State}, toner {info.TonerLevel}% (low {info.TonerLowLevel}), reasons [{string.Join(",", info.Reasons)}], media [{string.Join(",", info.MediaReady)}]");
                Check(info.TonerLevel.HasValue, "toner level");
            }
            var found = await PrinterDiscovery.FindScannersAsync(CancellationToken.None);
            Console.WriteLine("      discovery: " + string.Join("; ", found.Select(f => $"{f.Model} @ {f.Host} (queue '{f.QueueName}')")));
            Check(found.Any(f => f.Host == host), "discovered through Windows printer ports");
            Check(PrinterDiscovery.FindQueueForHost(host) != null, "Windows print queue found for host");
        }
    }
}
