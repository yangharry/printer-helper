using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using BitMiracle.LibTiff.Classic;
using PrinterHelper.Documents;
using SkiaSharp;

namespace PrinterHelper
{
    /// <summary>
    /// <c>PrinterHelper --smoke-test DIR</c>: opens the window without settings or network, checks that the
    /// native parts load and work on this machine (Skia, HarfBuzz, PDFium, LibTiff, a Korean font), writes
    /// DIR/result.json and DIR/ok, and exits 0 (1 on failure, 3 if it hangs). Used by CI on Windows and macOS.
    /// </summary>
    static class SmokeTest
    {
        public static string DirectoryFromArgs(string[] args)
        {
            var i = Array.IndexOf(args, "--smoke-test");
            if (i < 0) return null;
            return i + 1 < args.Length && !args[i + 1].StartsWith("--")
                ? Path.GetFullPath(args[i + 1])
                : Path.Combine(Path.GetTempPath(), "PrinterHelperSmoke");
        }

        public static void Attach(MainWindow window, string dir, IClassicDesktopStyleApplicationLifetime desktop)
        {
            Directory.CreateDirectory(dir);
            File.Delete(Path.Combine(dir, "ok"));
            // A stray dialog must not hang CI.
            new Thread(() => { Thread.Sleep(TimeSpan.FromSeconds(60)); Environment.Exit(3); }) { IsBackground = true }.Start();

            window.Opened += async (s, e) =>
            {
                var result = new Dictionary<string, object>
                {
                    ["os"] = RuntimeInformation.OSDescription,
                    ["arch"] = RuntimeInformation.ProcessArchitecture.ToString(),
                    ["version"] = typeof(SmokeTest).Assembly.GetName().Version.ToString(),
                };
                var ok = false;
                try
                {
                    await Task.Delay(2000);
                    ok = await RunChecks(window, dir, result);
                }
                catch (Exception ex)
                {
                    result["error"] = ex.ToString();
                }
                var json = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                File.WriteAllText(Path.Combine(dir, "result.json"), JsonSerializer.Serialize(result, json));
                if (ok) File.WriteAllText(Path.Combine(dir, "ok"), "ok\n");
                desktop.Shutdown(ok ? 0 : 1);
            };
        }

        static async Task<bool> RunChecks(MainWindow window, string dir, Dictionary<string, object> result)
        {
            // Skia: encode a gray and a color page, and read them back.
            var gray = Page(400, 560, gray: true);
            var color = Page(560, 400, gray: false);
            result["skiaJpeg"] = $"gray {gray.Components} channel(s), color {color.Components} channels";

            // PDFium: a PDF written by our own writer, read back page by page.
            var pdf = Path.Combine(dir, "smoke.pdf");
            PdfWriter.Save(pdf, new[] { gray, color });
            var pdfPages = await PageImporter.ImportAsync(pdf, CancellationToken.None);
            result["pdfium"] = string.Join(", ", pdfPages.Select(p => $"{p.PixelWidth}x{p.PixelHeight}"));

            // LibTiff: a two-page TIFF.
            var tiff = Path.Combine(dir, "smoke.tif");
            File.WriteAllBytes(tiff, TwoPageTiff());
            var tiffPages = await PageImporter.ImportAsync(tiff, CancellationToken.None);
            result["libtiff"] = $"{tiffPages.Count} pages";

            // The window: import through the UI path, so thumbnails and the page list are exercised too.
            await window.ImportFilesAsync(new[] { pdf, tiff });
            result["windowPages"] = window.Pages.Count;
            await Task.Delay(500);

            // A Korean font must be found, or every label would show boxes.
            var korean = FontManager.Current.TryMatchCharacter('한', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, null, CultureInfo.CurrentUICulture, out var typeface);
            result["koreanFont"] = korean ? typeface.FontFamily.Name : null;

            var size = new PixelSize(Math.Max(1, (int)window.Bounds.Width), Math.Max(1, (int)window.Bounds.Height));
            using (var render = new RenderTargetBitmap(size, new Vector(96, 96)))
            {
                render.Render(window);
                render.Save(Path.Combine(dir, "window.png"));
            }
            result["render"] = $"{size.Width}x{size.Height}";

            return pdfPages.Count == 2 && tiffPages.Count == 2 && window.Pages.Count == 4 && korean;
        }

        static PageImage Page(int width, int height, bool gray)
        {
            var info = new SKImageInfo(width, height, gray ? SKColorType.Gray8 : SKColorType.Bgra8888, SKAlphaType.Opaque);
            using (var bitmap = new SKBitmap(info))
            using (var canvas = new SKCanvas(bitmap))
            {
                if (gray)
                {
                    bitmap.Erase(SKColors.White);
                }
                else
                {
                    canvas.Clear(SKColors.White);
                    using (var paint = new SKPaint { Color = SKColors.SteelBlue })
                        canvas.DrawRect(40, 40, width - 80, height - 80, paint);
                }
                return PageImage.FromBitmap(bitmap, 100, "smoke");
            }
        }

        static byte[] TwoPageTiff()
        {
            using (var stream = new MemoryStream())
            {
                using (var tif = Tiff.ClientOpen("smoke.tif", "w", stream, new TiffStream()))
                {
                    for (short page = 0; page < 2; page++)
                    {
                        const int width = 64, height = 48;
                        tif.SetField(TiffTag.IMAGEWIDTH, width);
                        tif.SetField(TiffTag.IMAGELENGTH, height);
                        tif.SetField(TiffTag.BITSPERSAMPLE, 8);
                        tif.SetField(TiffTag.SAMPLESPERPIXEL, 1);
                        tif.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);
                        tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
                        tif.SetField(TiffTag.ROWSPERSTRIP, height);
                        tif.SetField(TiffTag.XRESOLUTION, 100.0);
                        tif.SetField(TiffTag.YRESOLUTION, 100.0);
                        tif.SetField(TiffTag.RESOLUTIONUNIT, ResUnit.INCH);
                        tif.SetField(TiffTag.SUBFILETYPE, FileType.PAGE);
                        tif.SetField(TiffTag.PAGENUMBER, page, (short)2);
                        var row = new byte[width];
                        for (var y = 0; y < height; y++)
                        {
                            for (var x = 0; x < width; x++) row[x] = (byte)(x * 4 + y + page * 60);
                            tif.WriteScanline(row, y);
                        }
                        tif.WriteDirectory();
                    }
                }
                return stream.ToArray();
            }
        }
    }
}
