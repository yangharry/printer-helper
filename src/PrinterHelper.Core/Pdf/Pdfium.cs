using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PrinterHelper.Pdf
{
    /// <summary>
    /// The PDFium calls PDF import needs (public/fpdfview.h and fpdf_formfill.h). The native library comes from
    /// the bblanchon.PDFium packages: pdfium.dll on Windows, libpdfium.dylib on macOS.
    /// PDFium is not thread-safe at all, not even across documents: every call must be made while holding
    /// <see cref="Gate"/>.
    /// </summary>
    static unsafe partial class Pdfium
    {
        const string Library = "pdfium";

        public const int FPDF_ERR_FORMAT = 3;
        public const int FPDF_ERR_PASSWORD = 4;
        public const int FPDF_ERR_SECURITY = 5;

        /// <summary>Blue, green, red, alpha: the byte order of an opaque Skia Bgra8888 bitmap.</summary>
        public const int FPDFBitmap_BGRA = 4;

        /// <summary>Draw annotations (stamps, highlights, ink) too, as a viewer shows them.</summary>
        public const int FPDF_ANNOT = 0x01;

        public static readonly Lock Gate = new Lock();

        static bool initialized;

        /// <summary>Initializes the library on first use; it is never torn down. Call while holding <see cref="Gate"/>.</summary>
        public static void EnsureInitialized()
        {
            if (initialized) return;
            FPDF_InitLibrary();
            initialized = true;
        }

        [LibraryImport(Library)]
        private static partial void FPDF_InitLibrary();

        /// <param name="data">Must stay valid and unmoved until the document is closed: PDFium reads it lazily.</param>
        [LibraryImport(Library)]
        public static partial IntPtr FPDF_LoadMemDocument64(byte* data, nuint size, byte* password);

        /// <summary>The reason the last load failed. C "unsigned long": 32-bit on Windows, 64-bit on macOS.</summary>
        [LibraryImport(Library)]
        public static partial CULong FPDF_GetLastError();

        [LibraryImport(Library)]
        public static partial int FPDF_GetPageCount(IntPtr document);

        [LibraryImport(Library)]
        public static partial IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);

        /// <summary>In points, with the page's own /Rotate already applied.</summary>
        [LibraryImport(Library)]
        public static partial float FPDF_GetPageWidthF(IntPtr page);

        [LibraryImport(Library)]
        public static partial float FPDF_GetPageHeightF(IntPtr page);

        /// <param name="firstScan">Our own pixel buffer; PDFium draws into it and does not free it.</param>
        [LibraryImport(Library)]
        public static partial IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);

        /// <param name="color">0xAARRGGBB; FPDF_DWORD is a C "unsigned long".</param>
        [LibraryImport(Library)]
        public static partial int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, CULong color);

        /// <param name="rotate">Extra rotation on top of the page's own; always 0 here.</param>
        [LibraryImport(Library)]
        public static partial void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

        [LibraryImport(Library)]
        public static partial void FPDFBitmap_Destroy(IntPtr bitmap);

        [LibraryImport(Library)]
        public static partial void FPDF_ClosePage(IntPtr page);

        [LibraryImport(Library)]
        public static partial void FPDF_CloseDocument(IntPtr document);

        // Filled-in form fields are widget annotations, which FPDF_RenderPageBitmap leaves out even with
        // FPDF_ANNOT; only the form-fill module draws them (FPDF_FFLDraw).

        /// <summary>
        /// Size of the zeroed FPDF_FORMFILLINFO we pass: version 1 and no callbacks (PDFium checks each one for
        /// null). Larger than the real struct (about 40 pointers) so PDFium never reads past it.
        /// </summary>
        public const int FormFillInfoSize = 512;

        /// <param name="formInfo">Must stay valid until <see cref="FPDFDOC_ExitFormFillEnvironment"/>.</param>
        [LibraryImport(Library)]
        public static partial IntPtr FPDFDOC_InitFormFillEnvironment(IntPtr document, void* formInfo);

        /// <summary>A no-op for a null handle.</summary>
        [LibraryImport(Library)]
        public static partial void FPDFDOC_ExitFormFillEnvironment(IntPtr form);

        [LibraryImport(Library)]
        public static partial void FORM_OnAfterLoadPage(IntPtr page, IntPtr form);

        [LibraryImport(Library)]
        public static partial void FORM_OnBeforeClosePage(IntPtr page, IntPtr form);

        [LibraryImport(Library)]
        public static partial void FPDF_FFLDraw(IntPtr form, IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    }
}
