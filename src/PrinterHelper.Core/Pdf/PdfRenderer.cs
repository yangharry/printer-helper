using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using PrinterHelper.Documents;
using SkiaSharp;

namespace PrinterHelper.Pdf
{
    /// <summary>Renders the pages of a PDF into page images (PDFium).</summary>
    public static class PdfRenderer
    {
        const string DamagedMessage = "PDF 파일이 손상되었거나 읽을 수 없습니다.";
        const string NoMemoryMessage = "PDF 쪽을 그릴 메모리가 부족합니다.";

        /// <param name="pdf">The whole file.</param>
        /// <param name="sourceName">File name shown under each page.</param>
        /// <param name="dpi">Render resolution (200).</param>
        /// <param name="maxPixels">A page larger than this is rendered at a lower resolution so it keeps its size.</param>
        public static List<PageImage> Render(byte[] pdf, string sourceName, double dpi, long maxPixels, CancellationToken ct)
        {
            if (pdf == null) throw new ArgumentNullException(nameof(pdf));
            if (!(dpi > 0)) throw new ArgumentOutOfRangeException(nameof(dpi));
            if (maxPixels < 1) throw new ArgumentOutOfRangeException(nameof(maxPixels));
            ct.ThrowIfCancellationRequested();
            if (pdf.Length == 0) throw new InvalidOperationException(DamagedMessage);
            try
            {
                return RenderPages(pdf, sourceName, dpi, maxPixels, ct);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException)
            {
                // Missing, or built for another processor or an older PDFium.
                throw new InvalidOperationException("PDF 엔진(pdfium)을 찾지 못했습니다.", ex);
            }
        }

        static unsafe List<PageImage> RenderPages(byte[] pdf, string sourceName, double dpi, long maxPixels, CancellationToken ct)
        {
            // PDFium reads the file lazily while the document is open, so it gets a copy that the GC never moves
            // and the caller cannot change. Loading from memory also keeps file names away from native code.
            var data = GC.AllocateUninitializedArray<byte>(pdf.Length, pinned: true);
            Buffer.BlockCopy(pdf, 0, data, 0, pdf.Length);
            var formInfo = NativeMemory.AllocZeroed(Pdfium.FormFillInfoSize);
            *(int*)formInfo = 1; // FPDF_FORMFILLINFO.version
            IntPtr document = IntPtr.Zero, form = IntPtr.Zero;
            try
            {
                int count;
                lock (Pdfium.Gate)
                {
                    Pdfium.EnsureInitialized();
                    document = Pdfium.FPDF_LoadMemDocument64((byte*)Marshal.UnsafeAddrOfPinnedArrayElement(data, 0), (nuint)data.Length, null);
                    if (document == IntPtr.Zero) throw new InvalidOperationException(OpenError(Pdfium.FPDF_GetLastError().Value));
                    count = Pdfium.FPDF_GetPageCount(document);
                    // Without it the pages still render, only without their filled-in form fields.
                    if (count > 0) form = Pdfium.FPDFDOC_InitFormFillEnvironment(document, formInfo);
                }
                if (count <= 0) throw new InvalidOperationException("PDF에 쪽이 없습니다.");

                var pages = new List<PageImage>(count);
                for (var i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    // JPEG encoding happens outside the lock, so another import can render meanwhile.
                    using (var bitmap = RenderPage(document, form, i, dpi, maxPixels, out var pageDpi))
                        pages.Add(PageImage.FromBitmap(bitmap, pageDpi, sourceName));
                }
                return pages;
            }
            finally
            {
                if (document != IntPtr.Zero)
                    lock (Pdfium.Gate)
                    {
                        if (form != IntPtr.Zero) Pdfium.FPDFDOC_ExitFormFillEnvironment(form);
                        Pdfium.FPDF_CloseDocument(document);
                    }
                NativeMemory.Free(formInfo);
                GC.KeepAlive(data);
            }
        }

        /// <summary>Draws one page on white, the way a viewer shows it (annotations and form fields included).</summary>
        static SKBitmap RenderPage(IntPtr document, IntPtr form, int index, double dpi, long maxPixels, out double pageDpi)
        {
            lock (Pdfium.Gate)
            {
                var page = Pdfium.FPDF_LoadPage(document, index);
                if (page == IntPtr.Zero) throw new InvalidOperationException($"PDF의 {index + 1}쪽을 읽지 못했습니다.");
                try
                {
                    Pdfium.FORM_OnAfterLoadPage(page, form);
                    var (width, height, scaledDpi) = PixelSize(Pdfium.FPDF_GetPageWidthF(page), Pdfium.FPDF_GetPageHeightF(page), dpi, maxPixels);
                    pageDpi = scaledDpi;

                    var bitmap = new SKBitmap();
                    try
                    {
                        if (!bitmap.TryAllocPixels(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque)))
                            throw new InvalidOperationException(NoMemoryMessage);
                        // PDFium draws straight into the Skia bitmap: same BGRA byte order, no copy.
                        var target = Pdfium.FPDFBitmap_CreateEx(width, height, Pdfium.FPDFBitmap_BGRA, bitmap.GetPixels(), bitmap.RowBytes);
                        if (target == IntPtr.Zero) throw new InvalidOperationException(NoMemoryMessage);
                        try
                        {
                            Pdfium.FPDFBitmap_FillRect(target, 0, 0, width, height, new CULong(0xFFFFFFFF));
                            Pdfium.FPDF_RenderPageBitmap(target, page, 0, 0, width, height, 0, Pdfium.FPDF_ANNOT);
                            Pdfium.FPDF_FFLDraw(form, target, page, 0, 0, width, height, 0, Pdfium.FPDF_ANNOT);
                        }
                        finally
                        {
                            Pdfium.FPDFBitmap_Destroy(target);
                        }
                        return bitmap;
                    }
                    catch
                    {
                        bitmap.Dispose();
                        throw;
                    }
                }
                finally
                {
                    Pdfium.FORM_OnBeforeClosePage(page, form);
                    Pdfium.FPDF_ClosePage(page);
                }
            }
        }

        /// <summary>
        /// Pixel size of a page (in points, rotation applied) at the given resolution. A page over the pixel limit
        /// (a poster, a drawing) gets a lower resolution instead, so it keeps its physical size: the returned dpi
        /// goes down by the same factor.
        /// </summary>
        internal static (int Width, int Height, double Dpi) PixelSize(double widthPoints, double heightPoints, double dpi, long maxPixels)
        {
            if (!(widthPoints > 0 && heightPoints > 0) || double.IsInfinity(widthPoints) || double.IsInfinity(heightPoints))
                throw new InvalidOperationException(DamagedMessage);
            var width = widthPoints / 72 * dpi;
            var height = heightPoints / 72 * dpi;
            if (width * height > maxPixels)
            {
                var scale = Math.Sqrt(maxPixels / (width * height));
                width *= scale;
                height *= scale;
                dpi *= scale;
            }
            double w = Math.Max(1, Math.Round(width)), h = Math.Max(1, Math.Round(height));
            // Rounding both sides up can land just over the limit (6325 x 6325 for a 100-inch square).
            if (w * h > maxPixels)
            {
                w = Math.Max(1, Math.Floor(width));
                h = Math.Max(1, Math.Floor(height));
            }
            // Still over only for a strip less than a pixel wide: shorten it rather than break the limit.
            if (w * h > maxPixels)
            {
                if (w >= h) w = Math.Max(1, Math.Floor(maxPixels / h));
                else h = Math.Max(1, Math.Floor(maxPixels / w));
            }
            return ((int)w, (int)h, dpi);
        }

        static string OpenError(ulong code)
        {
            switch (code)
            {
                case Pdfium.FPDF_ERR_PASSWORD:
                case Pdfium.FPDF_ERR_SECURITY:
                    return "암호가 걸린 PDF는 열 수 없습니다.";
                case Pdfium.FPDF_ERR_FORMAT:
                    return DamagedMessage;
                default:
                    return "PDF를 열지 못했습니다.";
            }
        }
    }
}
