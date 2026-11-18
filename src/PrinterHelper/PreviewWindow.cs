using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PrinterHelper.Documents;

namespace PrinterHelper
{
    /// <summary>Shows one page large. ←/→ moves between pages, Esc closes.</summary>
    sealed class PreviewWindow : Window
    {
        readonly IList<PageImage> pages;
        readonly Image image = new Image { Stretch = Stretch.Uniform };
        readonly LayoutTransformControl turn = new LayoutTransformControl();
        int index;

        public PreviewWindow(IList<PageImage> pages, int index)
        {
            this.pages = pages;
            Width = 820;
            Height = 980;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51));
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
            turn.Child = image;
            turn.Margin = new Thickness(16);
            turn.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            turn.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            Content = turn;
            KeyDown += OnKeyDown;
            Show(index);
        }

        void Show(int i)
        {
            index = i;
            var page = pages[i];
            var old = image.Source as Bitmap;
            using (var stream = new MemoryStream(page.Jpeg, writable: false))
                image.Source = Bitmap.DecodeToWidth(stream, 1600, BitmapInterpolationMode.HighQuality);
            old?.Dispose();
            turn.LayoutTransform = new RotateTransform(page.Rotation);
            Title = $"{i + 1} / {pages.Count}쪽 — ←, → 로 넘기기, Esc로 닫기";
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
            else if ((e.Key == Key.Right || e.Key == Key.PageDown || e.Key == Key.Space) && index < pages.Count - 1) Show(index + 1);
            else if ((e.Key == Key.Left || e.Key == Key.PageUp) && index > 0) Show(index - 1);
            else return;
            e.Handled = true;
        }

        protected override void OnClosed(System.EventArgs e)
        {
            (image.Source as Bitmap)?.Dispose();
            base.OnClosed(e);
        }
    }
}
