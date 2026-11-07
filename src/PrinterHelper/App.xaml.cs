using System.Net;
using System.Windows;
using System.Windows.Threading;

namespace PrinterHelper
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Some embedded web servers stall on "Expect: 100-continue"; printers never need it.
            ServicePointManager.Expect100Continue = false;
            base.OnStartup(e);
        }

        void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show("예상하지 못한 오류가 났습니다.\n\n" + e.Exception.GetBaseException().Message,
                "프린터 도우미", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
