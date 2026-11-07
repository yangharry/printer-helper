using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PrinterHelper.Documents;

namespace PrinterHelper
{
    /// <summary>Shows one page large. ←/→ moves between pages, Esc closes.</summary>
    sealed class PreviewWindow : Window
    {
        readonly IList<PageImage> pages;
        readonly Image image = new Image { Stretch = Stretch.Uniform };
        readonly RotateTransform rotation = new RotateTransform();
        int index;

        public PreviewWindow(IList<PageImage> pages, int index)
        {
            this.pages = pages;
            this.index = index;
            Width = 820;
            Height = 980;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51));
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            image.LayoutTransform = rotation;
            image.Margin = new Thickness(16);
            Content = image;
            KeyDown += OnKeyDown;
            Show(index);
        }

        void Show(int i)
        {
            index = i;
            var page = pages[i];
            image.Source = Imaging.Decode(page.Jpeg, 1600);
            rotation.Angle = page.Rotation;
            Title = $"{i + 1} / {pages.Count}쪽 — ←, → 로 넘기기, Esc로 닫기";
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
            else if ((e.Key == Key.Right || e.Key == Key.Next || e.Key == Key.Space) && index < pages.Count - 1) Show(index + 1);
            else if ((e.Key == Key.Left || e.Key == Key.Prior) && index > 0) Show(index - 1);
        }
    }
}
