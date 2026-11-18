using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace PrinterHelper
{
    public partial class App : Application
    {
        public const string Title = "프린터 도우미";

        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            Dispatcher.UIThread.UnhandledException += OnUnhandledException;
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var args = desktop.Args ?? Array.Empty<string>();
                var smokeDir = SmokeTest.DirectoryFromArgs(args);
                // The smoke test must not touch the user's settings.
                if (smokeDir != null) AppSettings.FilePath = Path.Combine(smokeDir, "settings.xml");
                var window = new MainWindow(smokeDir == null ? args : Array.Empty<string>(), offline: smokeDir != null);
                desktop.MainWindow = window;
                if (smokeDir != null) SmokeTest.Attach(window, smokeDir, desktop);

                // macOS: files opened with "Open With" or dropped on the Dock icon arrive here, not as arguments.
                if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
                    activatable.Activated += (s, e) =>
                    {
                        if (e is FileActivatedEventArgs opened)
                            window.OpenFiles(opened.Files.Select(f => f.TryGetLocalPath()).Where(p => p != null).ToList());
                    };
            }
            base.OnFrameworkInitializationCompleted();
        }

        async void About_Click(object sender, EventArgs e)
        {
            if ((ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is Window window)
            {
                var version = typeof(App).Assembly.GetName().Version;
                await MessageDialog.ShowAsync(window, $"{Title} {version.Major}.{version.Minor}.{version.Build}\n\n" +
                    "네트워크 복합기의 급지대 스캔, PDF 저장, 인쇄.\nhttps://github.com/yangharry/printer-helper\n\nMIT 라이선스. 함께 쓰는 라이브러리는 THIRD-PARTY-NOTICES.md를 보세요.");
            }
        }

        async void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            if ((ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is Window window && window.IsVisible)
                await MessageDialog.ShowAsync(window, "예상하지 못한 오류가 났습니다.\n\n" + e.Exception.GetBaseException().Message);
        }
    }
}
