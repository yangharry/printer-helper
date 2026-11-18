using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace PrinterHelper
{
    /// <summary>A small modal question or notice (Avalonia has no message box).</summary>
    sealed class MessageDialog : Window
    {
        MessageDialog(string text, bool question)
        {
            Title = App.Title;
            Width = 440;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
            root.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 21 });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            if (question)
            {
                var yes = new Button { Content = "예", MinWidth = 80 };
                var no = new Button { Content = "아니요", MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
                yes.Click += (s, e) => Close(true);
                no.Click += (s, e) => Close(false);
                buttons.Children.Add(yes);
                buttons.Children.Add(no);
                // "No" is the safe choice, as before: it has the focus and Enter/Esc both mean no.
                Opened += (s, e) => no.Focus();
            }
            else
            {
                var ok = new Button { Content = "확인", MinWidth = 80 };
                ok.Click += (s, e) => Close(true);
                buttons.Children.Add(ok);
                Opened += (s, e) => ok.Focus();
            }
            root.Children.Add(buttons);
            Content = root;
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { Close(false); e.Handled = true; }
            };
        }

        /// <summary>Asks a yes/no question; true means yes.</summary>
        public static Task<bool> AskAsync(Window owner, string text) => new MessageDialog(text, true).ShowDialog<bool>(owner);

        public static Task ShowAsync(Window owner, string text) => new MessageDialog(text, false).ShowDialog<bool>(owner);
    }
}
