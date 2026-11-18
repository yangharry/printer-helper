using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PrinterHelper.Printing;

namespace PrinterHelper
{
    /// <summary>
    /// The print window on macOS: printer, copies and paper. (Windows uses its own print dialog instead.)
    /// Returns true when the user chose 인쇄.
    /// </summary>
    sealed class PrintWindow : Window
    {
        readonly ComboBox queueCombo;
        readonly NumericUpDown copiesBox;
        readonly ComboBox sheetCombo;

        public PrintWindow(IReadOnlyList<CupsQueue> queues, CupsQueue preferred, SheetSize sheet, int pageCount)
        {
            Title = "인쇄";
            Width = 400;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new StackPanel { Margin = new Thickness(20) };
            root.Children.Add(Label("프린터"));
            queueCombo = new ComboBox { ItemsSource = queues, SelectedItem = preferred ?? queues.FirstOrDefault(q => q.IsDefault) ?? queues.FirstOrDefault(), HorizontalAlignment = HorizontalAlignment.Stretch };
            root.Children.Add(queueCombo);

            root.Children.Add(Label("매수"));
            // ClipValueToMinMax: a typed 150 becomes 99 in the box too, so what is shown is what prints.
            copiesBox = new NumericUpDown { Minimum = 1, Maximum = 99, Value = 1, Increment = 1, FormatString = "0", ClipValueToMinMax = true, HorizontalAlignment = HorizontalAlignment.Stretch };
            root.Children.Add(copiesBox);

            root.Children.Add(Label("용지"));
            sheetCombo = new ComboBox { ItemsSource = new[] { SheetSize.A4, SheetSize.Letter, SheetSize.Legal }, SelectedItem = sheet, HorizontalAlignment = HorizontalAlignment.Stretch };
            root.Children.Add(sheetCombo);

            root.Children.Add(new TextBlock
            {
                Text = $"{pageCount}쪽을 실제 크기로 인쇄합니다. 용지보다 큰 쪽만 줄여서 맞춥니다.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0),
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            var print = new Button { Content = "인쇄", MinWidth = 80, IsDefault = true, IsEnabled = queues.Count > 0 };
            print.Click += (s, e) => Close(true);
            var cancel = new Button { Content = "취소", MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
            cancel.Click += (s, e) => Close(false);
            buttons.Children.Add(print);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            Content = root;
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { Close(false); e.Handled = true; }
            };
        }

        public CupsQueue Queue => queueCombo.SelectedItem as CupsQueue;
        public int Copies => (int)Math.Clamp(copiesBox.Value ?? 1, 1, 99);
        public SheetSize Sheet => sheetCombo.SelectedItem is SheetSize s ? s : SheetSize.A4;

        static TextBlock Label(string text) => new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 4) };
    }
}
