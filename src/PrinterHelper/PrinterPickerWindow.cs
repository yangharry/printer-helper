using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PrinterHelper.Printing;

namespace PrinterHelper
{
    /// <summary>Lets the user pick a found scanner or type its IP address.</summary>
    sealed class PrinterPickerWindow : Window
    {
        readonly TextBox hostBox;
        readonly ListBox foundList;
        readonly TextBlock searchText;
        readonly CancellationTokenSource searchCts = new CancellationTokenSource();

        public PrinterPickerWindow(string currentHost)
        {
            Title = "프린터 변경";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
            FontSize = 13;

            var root = new StackPanel { Margin = new Thickness(20) };
            root.Children.Add(new TextBlock { Text = "찾은 스캐너", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
            searchText = new TextBlock { Text = "Windows에 설치된 프린터에서 찾는 중…", Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 6) };
            root.Children.Add(searchText);
            foundList = new ListBox { Height = 120, DisplayMemberPath = "Label" };
            foundList.SelectionChanged += (s, e) =>
            {
                if (foundList.SelectedItem is Choice c) hostBox.Text = c.Host;
            };
            foundList.MouseDoubleClick += (s, e) => { if (foundList.SelectedItem != null) DialogResult = true; };
            root.Children.Add(foundList);

            root.Children.Add(new TextBlock { Text = "IP 주소", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) });
            hostBox = new TextBox { Text = currentHost ?? "", Height = 30, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 0, 6, 0) };
            root.Children.Add(hostBox);
            root.Children.Add(new TextBlock
            {
                Text = "프린터 화면이나 공유기에서 IP 주소를 확인할 수 있습니다. 비워 두면 자동으로 찾습니다.",
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            var ok = new Button { Content = "확인", IsDefault = true, MinWidth = 80 };
            ok.Click += (s, e) => DialogResult = true;
            var cancel = new Button { Content = "취소", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            Content = root;

            Loaded += async (s, e) =>
            {
                hostBox.Focus();
                hostBox.SelectAll();
                try
                {
                    var found = await PrinterDiscovery.FindScannersAsync(searchCts.Token);
                    foreach (var scanner in found)
                        foundList.Items.Add(new Choice { Host = scanner.Host, Label = $"{scanner.Model}  ({scanner.Host})" });
                    searchText.Text = found.Count == 0 ? "찾지 못했습니다. IP 주소를 직접 넣어 주세요." : "고르거나 IP 주소를 직접 넣으세요.";
                }
                catch (System.OperationCanceledException)
                {
                }
            };
            Closed += (s, e) => searchCts.Cancel();
        }

        /// <summary>The address to use; empty means "search automatically".</summary>
        public string SelectedHost => hostBox.Text.Trim();

        sealed class Choice
        {
            public string Host { get; set; }
            public string Label { get; set; }
        }
    }
}
