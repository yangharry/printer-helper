using System;
using System.Diagnostics;

namespace PrinterHelper.Platform
{
    /// <summary>Opens files and folders with whatever the system uses for them (Explorer, Finder, a PDF viewer).</summary>
    public static class Shell
    {
        public static void Open(string path)
        {
            if (OperatingSystem.IsMacOS())
                Run("/usr/bin/open", path);
            else if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            else
                Run("xdg-open", path);
        }

        static void Run(string program, string argument)
        {
            var info = new ProcessStartInfo(program) { UseShellExecute = false };
            info.ArgumentList.Add(argument);
            Process.Start(info)?.Dispose();
        }
    }
}
