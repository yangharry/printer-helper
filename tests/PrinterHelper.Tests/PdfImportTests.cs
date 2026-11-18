using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PrinterHelper.Documents;
using PrinterHelper.Pdf;
using static PrinterHelper.Tests.Assert;

namespace PrinterHelper.Tests
{
    static class PdfImportTests
    {
        const string PasswordMessage = "암호가 걸린 PDF는 열 수 없습니다.";
        const string DamagedMessage = "PDF 파일이 손상되었거나 읽을 수 없습니다.";
        const string NoPagesMessage = "PDF에 쪽이 없습니다.";

        static readonly string[] FriendlyMessages = { PasswordMessage, DamagedMessage, NoPagesMessage, "PDF를 열지 못했습니다." };

        [Test("PDF import matches 1.0.1")]
        static void ImportMatchesV1() => DocumentTests.CompareWithV1(name => name.EndsWith(".pdf"));

        [Test("PDF import: pages saved by PdfWriter come back at their size")]
        static void RoundTrip()
        {
            var source = new List<PageImage>
            {
                TestImages.Page(1654, 2339, false, 200),                // A4 portrait, gray
                TestImages.Page(1100, 850, true, 100),                  // 11 x 8.5 in, color
                TestImages.Page(800, 1000, false, 150, rotation: 90),   // 5.33 x 6.67 in, shown landscape
            };
            // A Korean file name: PDFium gets the bytes, never the path.
            var name = "PDF 가져오기 " + Guid.NewGuid().ToString("N").Substring(0, 6) + ".pdf";
            var path = Path.Combine(Program.OutDir, name);
            PdfWriter.Save(path, source);
            var pages = PageImporter.ImportAsync(path, CancellationToken.None).GetAwaiter().GetResult();

            Check(pages.Count == 3, $"3 pages (got {pages.Count})");
            for (var i = 0; i < 3; i++)
            {
                var p = pages[i];
                var s = source[i];
                var sideways = s.Rotation % 180 != 0;
                double inchesWide = (sideways ? s.PixelHeight : s.PixelWidth) / s.Dpi, inchesHigh = (sideways ? s.PixelWidth : s.PixelHeight) / s.Dpi;
                var where = $"page {i + 1}";
                Check(p.Dpi == PageImporter.PdfDpi, $"{where}: {p.Dpi} dpi");
                Check(Math.Abs(p.PixelWidth / p.Dpi - inchesWide) * p.Dpi <= 1 && Math.Abs(p.PixelHeight / p.Dpi - inchesHigh) * p.Dpi <= 1,
                    $"{where}: {p.PixelWidth}x{p.PixelHeight} is {inchesWide:0.###} x {inchesHigh:0.###} in");
                Check(p.Components == 3 && p.Rotation == 0, $"{where}: a color page with its rotation applied");
                Check(p.Source == name, $"{where}: source {p.Source}");
            }

            // The test pictures get lighter from left to right; turned a quarter clockwise, from top to bottom.
            Check(Luma(pages[0], 0.02, 0.5) < 100 && Luma(pages[0], 0.98, 0.5) > 150, "page 1 drawn the right way round");
            Check(Luma(pages[2], 0.5, 0.02) < 100 && Luma(pages[2], 0.5, 0.98) > 150, "page 3 turned clockwise");

            var cancelled = new CancellationToken(true);
            Throws<OperationCanceledException>(() => PdfRenderer.Render(File.ReadAllBytes(path), name, 200, PageImporter.MaxRenderPixels, cancelled), "cancellation");
        }

        [Test("PDF import: a password-protected PDF says so")]
        static void PasswordProtected()
        {
            Check(Convert.ToHexString(Rc4(Encoding.ASCII.GetBytes("Key"), Encoding.ASCII.GetBytes("Plaintext"))) == "BBF316E8D940AF0AD3", "RC4 test vector");
            // Without a user password the same construction must open; otherwise any broken file would pass below.
            var open = Render(EncryptedPdf("", "owner"));
            Check(open.Count == 1 && open[0].PixelWidth == 556 && open[0].PixelHeight == 278, "encrypted PDF with no user password opens");
            Check(ErrorOf(EncryptedPdf("secret", "owner")) == PasswordMessage, "password message");
        }

        [Test("PDF import: broken files give a friendly error")]
        static void BrokenFiles()
        {
            Check(ErrorOf(new byte[0]) == DamagedMessage, "empty file");
            var random = new byte[4096];
            new Random(1).NextBytes(random);
            Check(ErrorOf(random) == DamagedMessage, $"random bytes: {ErrorOf(random)}");
            Check(ErrorOf(Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<< /Type /Catalog")) == DamagedMessage, "header and half an object");
            Check(ErrorOf(Pdf(new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>" })) == NoPagesMessage, "no pages");

            // PDFium rebuilds what it can from a cut-off file: it may open, but must never crash or throw anything else.
            byte[] whole;
            using (var ms = new MemoryStream())
            {
                PdfWriter.Write(ms, new[] { TestImages.Page(300, 400, true, 100), TestImages.Page(300, 400, false, 100) }, DateTime.Now);
                whole = ms.ToArray();
            }
            foreach (var length in new[] { 20, 200, whole.Length / 3, whole.Length / 2, whole.Length - 40 })
            {
                var cut = whole.Take(length).ToArray();
                try
                {
                    Render(cut);
                }
                catch (InvalidOperationException ex)
                {
                    Check(FriendlyMessages.Contains(ex.Message), $"cut at {length}: {ex.Message}");
                }
            }
        }

        [Test("PDF import: a 100 x 100 inch page stays under the pixel limit at its real size")]
        static void HugePage()
        {
            // Left half blue, so the whole bitmap is known to be drawn at the lowered resolution.
            var page = Render(OnePage("/MediaBox [0 0 7200 7200] /Contents 4 0 R", Stream("", "0 0 1 rg 0 0 3600 7200 re f"))).Single();
            Check((long)page.PixelWidth * page.PixelHeight <= PageImporter.MaxRenderPixels, $"{page.PixelWidth}x{page.PixelHeight} is at most 40,000,000 pixels");
            Check(Math.Abs(page.PixelWidth / page.Dpi - 100) * page.Dpi <= 1 && Math.Abs(page.PixelHeight / page.Dpi - 100) * page.Dpi <= 1,
                $"still 100 x 100 inches ({page.PixelWidth} px at {page.Dpi:0.###} dpi)");
            var (b, g, r) = Pixel(page, 0.25, 0.5);
            Check(b > 200 && g < 60 && r < 60, $"left half blue (got {b},{g},{r})");
            (b, g, r) = Pixel(page, 0.75, 0.5);
            Check(b > 240 && g > 240 && r > 240, $"right half white (got {b},{g},{r})");

            // Sizes on their own, including pages too thin for a pixel.
            Check(PdfRenderer.PixelSize(595.44f, 842.04f, 200, 40_000_000) == (1654, 2339, 200), "A4 at 200 dpi");
            var thin = PdfRenderer.PixelSize(0.1, 1e9, 200, 40_000_000);
            Check((long)thin.Width * thin.Height <= 40_000_000 && thin.Width >= 1, $"thin strip {thin.Width}x{thin.Height}");
            var wide = PdfRenderer.PixelSize(1e9, 0.1, 200, 40_000_000);
            Check((long)wide.Width * wide.Height <= 40_000_000 && wide.Height >= 1, $"wide strip {wide.Width}x{wide.Height}");
        }

        [Test("PDF import: several imports at the same time all succeed")]
        static void ConcurrentImports()
        {
            var pdf = Fixtures.Bytes("pdf-3pages.pdf");
            var expected = Render(pdf);
            const int threadCount = 4;
            var results = new List<PageImage>[threadCount];
            var errors = new Exception[threadCount];
            using (var start = new Barrier(threadCount))
            {
                var threads = Enumerable.Range(0, threadCount).Select(n => new Thread(() =>
                {
                    try
                    {
                        start.SignalAndWait();
                        results[n] = PdfRenderer.Render(pdf, "thread.pdf", PageImporter.PdfDpi, PageImporter.MaxRenderPixels, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        errors[n] = ex;
                    }
                })).ToList();
                threads.ForEach(t => t.Start());
                threads.ForEach(t => t.Join());
            }
            for (var n = 0; n < threadCount; n++)
            {
                Check(errors[n] == null, $"thread {n + 1}: {errors[n]?.Message}");
                Check(results[n].Count == 3, $"thread {n + 1}: 3 pages");
                for (var i = 0; i < 3; i++)
                    Check(results[n][i].Jpeg.SequenceEqual(expected[i].Jpeg), $"thread {n + 1} page {i + 1} same as a lone import");
            }
        }

        [Test("PDF import: filled-in form fields and annotations are drawn")]
        static void FormFieldsAndAnnotations()
        {
            // A text field whose appearance fills the left half, and a square annotation in the bottom right.
            var pdf = Pdf(new[]
            {
                "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 144 144] /Annots [4 0 R 6 0 R] >>",
                "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (filled) /F 4 /P 3 0 R /Rect [0 0 72 144] /AP << /N 5 0 R >> >>",
                Stream("/Type /XObject /Subtype /Form /BBox [0 0 72 144]", "0 0 0 rg 0 0 72 144 re f"),
                "<< /Type /Annot /Subtype /Square /F 4 /P 3 0 R /Rect [72 0 144 72] /AP << /N 7 0 R >> >>",
                Stream("/Type /XObject /Subtype /Form /BBox [0 0 72 72]", "1 0 0 rg 0 0 72 72 re f"),
            });
            var page = Render(pdf).Single();
            var (b, g, r) = Pixel(page, 0.25, 0.5);
            Check(b < 40 && g < 40 && r < 40, $"form field drawn (got {b},{g},{r})");
            (b, g, r) = Pixel(page, 0.75, 0.75);
            Check(r > 200 && g < 60 && b < 60, $"annotation drawn (got {b},{g},{r})");
            (b, g, r) = Pixel(page, 0.75, 0.25);
            Check(b > 240 && g > 240 && r > 240, $"the rest is white paper (got {b},{g},{r})");
        }

        static List<PageImage> Render(byte[] pdf) =>
            PdfRenderer.Render(pdf, "test.pdf", PageImporter.PdfDpi, PageImporter.MaxRenderPixels, CancellationToken.None);

        static string ErrorOf(byte[] pdf)
        {
            try
            {
                Render(pdf);
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }
            throw new Exception("expected InvalidOperationException");
        }

        /// <summary>Reads a pixel at a fraction of the page's width and height.</summary>
        static (byte B, byte G, byte R) Pixel(PageImage page, double x, double y) =>
            TestImages.Pixel(page.Jpeg, (int)(x * (page.PixelWidth - 1)), (int)(y * (page.PixelHeight - 1)));

        static int Luma(PageImage page, double x, double y)
        {
            var (b, g, r) = Pixel(page, x, y);
            return (b + g + r) / 3;
        }

        // ---- Hand-made PDFs ----

        /// <summary>A PDF from object bodies (object 1 is the catalog) with a correct cross-reference table.</summary>
        static byte[] Pdf(IReadOnlyList<string> objects, string trailer = "")
        {
            // ASCII only, so string offsets are byte offsets.
            var sb = new StringBuilder("%PDF-1.4\n");
            var offsets = new List<int>();
            for (var i = 0; i < objects.Count; i++)
            {
                offsets.Add(sb.Length);
                sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
            }
            var xref = sb.Length;
            sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
            foreach (var offset in offsets) sb.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R{trailer} >>\nstartxref\n{xref}\n%%EOF\n");
            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        /// <summary>Catalog, page tree and one page with the given entries; extra objects are numbered from 4.</summary>
        static byte[] OnePage(string pageEntries, params string[] more) =>
            Pdf(new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", $"<< /Type /Page /Parent 2 0 R {pageEntries} >>" }.Concat(more).ToList());

        static string Stream(string entries, string content) => $"<< {entries} /Length {content.Length} >>\nstream\n{content}\nendstream";

        static readonly byte[] PasswordPadding =
        {
            0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
            0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
        };

        /// <summary>
        /// A one-page 200 x 100 point PDF encrypted with the original Standard security handler (revision 2,
        /// 40-bit RC4; PDF Reference algorithms 3.2 to 3.4). It has no strings or streams, so nothing else needs encrypting.
        /// </summary>
        static byte[] EncryptedPdf(string userPassword, string ownerPassword)
        {
            const int permissions = -4;
            var id = Enumerable.Range(0, 16).Select(i => (byte)(i * 17 + 3)).ToArray();
            // O: the padded user password, encrypted with a key made from the owner password.
            var o = Rc4(MD5.HashData(Pad(ownerPassword)).Take(5).ToArray(), Pad(userPassword));
            // The file key: MD5 of the padded user password, O, the permissions (little-endian) and the first file ID.
            var p = BitConverter.GetBytes(permissions);
            if (!BitConverter.IsLittleEndian) Array.Reverse(p);
            var key = MD5.HashData(Pad(userPassword).Concat(o).Concat(p).Concat(id).ToArray()).Take(5).ToArray();
            // U: the padding itself, encrypted with the file key; a reader checks a password by redoing this.
            var u = Rc4(key, PasswordPadding);
            return Pdf(new[]
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] >>",
                $"<< /Filter /Standard /V 1 /R 2 /Length 40 /O <{Convert.ToHexString(o)}> /U <{Convert.ToHexString(u)}> /P {permissions} >>",
            }, $" /Encrypt 4 0 R /ID [<{Convert.ToHexString(id)}> <{Convert.ToHexString(id)}>]");
        }

        /// <summary>The password's bytes, cut or filled up to 32 with the standard padding.</summary>
        static byte[] Pad(string password) =>
            Encoding.Latin1.GetBytes(password).Concat(PasswordPadding).Take(32).ToArray();

        static byte[] Rc4(byte[] key, byte[] data)
        {
            var s = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            for (int i = 0, j = 0; i < 256; i++)
            {
                j = (j + s[i] + key[i % key.Length]) & 255;
                (s[i], s[j]) = (s[j], s[i]);
            }
            var output = new byte[data.Length];
            for (int n = 0, i = 0, j = 0; n < data.Length; n++)
            {
                i = (i + 1) & 255;
                j = (j + s[i]) & 255;
                (s[i], s[j]) = (s[j], s[i]);
                output[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 255]);
            }
            return output;
        }
    }
}
