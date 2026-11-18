using System;
using System.Drawing.Printing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PrinterHelper.Printing
{
    /// <summary>
    /// The native Windows print dialog (PrintDlgEx): printer choice, copies and the driver's own
    /// preferences button, without WinForms or WPF.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class WindowsPrintDialog
    {
        const uint PD_NOSELECTION = 0x00000004;
        const uint PD_NOPAGENUMS = 0x00000008;
        const uint PD_USEDEVMODECOPIESANDCOLLATE = 0x00040000;
        const uint PD_HIDEPRINTTOFILE = 0x00100000;
        const uint PD_NOCURRENTPAGE = 0x00800000;
        const uint START_PAGE_GENERAL = 0xFFFFFFFF;
        const uint PD_RESULT_PRINT = 1;
        const uint PDERR_NODEFAULTPRN = 0x1008;

        /// <summary>
        /// PRINTDLGEXW. commdlg.h packs it to 1 byte only on 32-bit Windows, where every field is
        /// 4 bytes anyway, so natural alignment is right on x86, x64 and ARM64 (136 bytes on 64-bit).
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct PRINTDLGEX
        {
            public uint lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hDevMode;
            public IntPtr hDevNames;
            public IntPtr hDC;
            public uint Flags;
            public uint Flags2;
            public uint ExclusionFlags;
            public uint nPageRanges;
            public uint nMaxPageRanges;
            public IntPtr lpPageRanges;
            public uint nMinPage;
            public uint nMaxPage;
            public uint nCopies;
            public IntPtr hInstance;
            public IntPtr lpPrintTemplateName;
            public IntPtr lpCallback;
            public uint nPropertyPages;
            public IntPtr lphPropertyPages;
            public uint nStartPage;
            public uint dwResultAction;
        }

        [DllImport("comdlg32.dll", EntryPoint = "PrintDlgExW")]
        static extern int PrintDlgEx(ref PRINTDLGEX pd);

        [DllImport("comdlg32.dll")]
        static extern uint CommDlgExtendedError();

        [DllImport("kernel32.dll")]
        static extern IntPtr GlobalFree(IntPtr hMem);

        [DllImport("user32.dll")]
        static extern IntPtr GetActiveWindow();

        /// <summary>Must run on an STA thread (the UI thread). Returns null when the user cancels.</summary>
        public static PrinterSettings Show(IntPtr owner, string preferredQueue)
        {
            // PrintDlgEx refuses a null owner, and the desktop window must not be one (it would be disabled).
            if (owner == IntPtr.Zero) owner = GetActiveWindow();
            if (owner == IntPtr.Zero) throw new ArgumentException("인쇄 창을 띄울 창이 없습니다.", nameof(owner));

            var pd = new PRINTDLGEX
            {
                lStructSize = (uint)Marshal.SizeOf<PRINTDLGEX>(),
                hwndOwner = owner,
                Flags = PD_NOPAGENUMS | PD_NOSELECTION | PD_NOCURRENTPAGE | PD_USEDEVMODECOPIESANDCOLLATE | PD_HIDEPRINTTOFILE,
                nCopies = 1,
                nStartPage = START_PAGE_GENERAL,
            };
            try
            {
                (pd.hDevNames, pd.hDevMode) = InitialHandles(preferredQueue);
                var hr = PrintDlgEx(ref pd);
                if (hr != 0)
                {
                    if (CommDlgExtendedError() == PDERR_NODEFAULTPRN)
                        throw new InvalidOperationException("설치된 프린터가 없습니다. Windows 설정에서 프린터를 추가해 주세요.");
                    throw new InvalidOperationException($"인쇄 창을 열지 못했습니다 (0x{hr:X8}).");
                }
                if (pd.dwResultAction != PD_RESULT_PRINT || pd.hDevNames == IntPtr.Zero || pd.hDevMode == IntPtr.Zero) return null;
                return SettingsFromHandles(pd.hDevNames, pd.hDevMode, pd.nCopies);
            }
            finally
            {
                // The dialog may have replaced the handles we gave it; free whatever it left in the struct.
                FreeHandles(pd.hDevNames, pd.hDevMode);
            }
        }

        /// <summary>What the user chose: printer, copies, paper, orientation and the driver's private settings.</summary>
        internal static PrinterSettings SettingsFromHandles(IntPtr devNames, IntPtr devMode, uint copies)
        {
            var settings = new PrinterSettings();
            settings.SetHdevnames(devNames);
            settings.SetHdevmode(devMode);
            settings.DefaultPageSettings.SetHdevmode(devMode);
            // With PD_USEDEVMODECOPIESANDCOLLATE the driver makes the copies and nCopies stays 1,
            // but keep the count in case a driver hands it back here instead (as WinForms does).
            if (settings.Copies == 1 && copies > 1) settings.Copies = (short)Math.Min(copies, (uint)short.MaxValue);
            return settings;
        }

        internal static void FreeHandles(IntPtr devNames, IntPtr devMode)
        {
            if (devMode != IntPtr.Zero) GlobalFree(devMode);
            if (devNames != IntPtr.Zero) GlobalFree(devNames);
        }

        /// <summary>The preferred queue when it is installed, else the default printer; zero handles when there is none.</summary>
        internal static (IntPtr DevNames, IntPtr DevMode) InitialHandles(string preferredQueue)
        {
            PrinterSettings settings = null;
            try
            {
                var installed = string.IsNullOrEmpty(preferredQueue) ? null :
                    PrinterSettings.InstalledPrinters.Cast<string>().FirstOrDefault(name => string.Equals(name, preferredQueue, StringComparison.OrdinalIgnoreCase));
                if (installed != null)
                {
                    settings = new PrinterSettings { PrinterName = installed };
                    if (!settings.IsValid) settings = null;
                }
                settings ??= new PrinterSettings();
                if (!settings.IsValid) return (IntPtr.Zero, IntPtr.Zero);

                var devNames = settings.GetHdevnames();
                try
                {
                    return (devNames, settings.GetHdevmode());
                }
                catch
                {
                    GlobalFree(devNames);
                    throw;
                }
            }
            catch (Exception ex) when (ex is InvalidPrinterException || ex is System.ComponentModel.Win32Exception)
            {
                // Let the dialog pick a printer itself.
                return (IntPtr.Zero, IntPtr.Zero);
            }
        }
    }
}
