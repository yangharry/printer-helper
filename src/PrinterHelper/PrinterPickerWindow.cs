using System;
using System.Collections.ObjectModel;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PrinterHelper.Discovery;

namespace PrinterHelper
{
    /// <summary>Lets the user pick a found scanner or type its IP address.</summary>
    sealed class PrinterPickerWindow : Window
    {
        readonly TextBox hostBox;
        readonly ListBox foundList;
        readonly TextBlock searchText;
        readonly ObservableCollection<Choice> found = new ObservableCollection<Choice>();
        readonly CancellationTokenSource searchCts = new CancellationTokenSource();

        public PrinterPickerWindow(string currentHost)
        {
            Title = "프린터 변경";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var gray = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
            var root = new StackPanel { Margin = new Thickness(20) };
            root.Children.Add(new TextBlock { Text = "찾은 스캐너", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
            searchText = new TextBlock { Text = "네트워크에서 찾는 중…", Foreground = gray, Margin = new Thickness(0, 0, 0, 6) };
            root.Children.Add(searchText);
            foundList = new ListBox { Height = 120, ItemsSource = found };
            foundList.SelectionChanged += (s, e) =>
            {
                if (foundList.SelectedItem is Choice c) hostBox.Text = c.Host;
            };
            foundList.DoubleTapped += (s, e) => { if (foundList.SelectedItem != null) Close(true); };
            root.Children.Add(foundList);

            root.Children.Add(new TextBlock { Text = "IP 주소", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 16, 0, 6) });
            hostBox = new TextBox { Text = currentHost ?? "" };
            root.Children.Add(hostBox);
            root.Children.Add(new TextBlock
            {
                Text = "프린터 화면이나 공유기에서 IP 주소를 확인할 수 있습니다. 비워 두면 자동으로 찾습니다.",
                Foreground = gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            var ok = new Button { Content = "확인", MinWidth = 80, IsDefault = true };
            ok.Click += (s, e) => Close(true);
            var cancel = new Button { Content = "취소", MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
            cancel.Click += (s, e) => Close(false);
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            Content = root;

            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { Close(false); e.Handled = true; }
            };
            Opened += async (s, e) =>
            {
                hostBox.Focus();
                hostBox.SelectAll();
                try
                {
                    var result = await PrinterDiscovery.DiscoverAsync(searchCts.Token);
                    foreach (var scanner in result.Scanners)
                        found.Add(new Choice { Host = scanner.Host, Label = $"{scanner.Model}  ({scanner.Host})" });
                    searchText.Text = result.Scanners.Count > 0 ? "고르거나 IP 주소를 직접 넣으세요."
                        : result.NetworkSearchBlocked && OperatingSystem.IsMacOS() ? MainWindow.LocalNetworkHint
                        : "찾지 못했습니다. 프린터가 절전 중이면 IP 주소를 직접 넣어 주세요.";
                    searchText.TextWrapping = TextWrapping.Wrap;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    searchText.Text = "찾는 중에 문제가 생겼습니다: " + ex.GetBaseException().Message;
                }
            };
            Closed += (s, e) => searchCts.Cancel();
        }

        /// <summary>The address to use; empty means "search automatically".</summary>
        public string SelectedHost => (hostBox.Text ?? "").Trim();

        sealed class Choice
        {
            public string Host { get; set; }
            public string Label { get; set; }
            public override string ToString() => Label;
        }
    }
}
