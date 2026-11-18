using System;
using Avalonia;
using Avalonia.Media;

namespace PrinterHelper
{
    static class Program
    {
        // STA: Windows drag and drop and the print dialog need it.
        [STAThread]
        public static int Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        public static AppBuilder BuildAvaloniaApp()
        {
            var builder = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
            // Hangul on macOS: the system font for Korean, also used when another font lacks a glyph.
            if (OperatingSystem.IsMacOS())
                builder = builder.With(new FontManagerOptions
                {
                    DefaultFamilyName = "Apple SD Gothic Neo",
                    FontFallbacks = new[] { new FontFallback { FontFamily = new FontFamily("Apple SD Gothic Neo") } },
                });
            return builder;
        }
    }
}
