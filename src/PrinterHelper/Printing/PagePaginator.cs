using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PrinterHelper.Documents;

namespace PrinterHelper.Printing
{
    /// <summary>
    /// Prints one image per sheet. Pages are drawn at their real size (a scanned A4 page prints
    /// as A4), shrunk only when they do not fit inside the printable area, and turned sideways
    /// when that fits the paper better.
    /// </summary>
    public sealed class PagePaginator : DocumentPaginator
    {
        readonly IReadOnlyList<PageImage> pages;
        readonly Rect printable;
        Size pageSize;

        /// <param name="pageSize">Sheet size in 1/96 inch.</param>
        /// <param name="printable">Area the printer can reach, in 1/96 inch from the sheet's top-left corner.</param>
        public PagePaginator(IReadOnlyList<PageImage> pages, Size pageSize, Rect printable)
        {
            this.pages = pages;
            this.pageSize = pageSize;
            this.printable = printable.IsEmpty || printable.Width <= 0 || printable.Height <= 0
                ? new Rect(pageSize)
                : printable;
        }

        public override bool IsPageCountValid => true;
        public override int PageCount => pages.Count;
        public override Size PageSize { get => pageSize; set => pageSize = value; }
        public override IDocumentPaginatorSource Source => null;

        public override DocumentPage GetPage(int pageNumber)
        {
            var page = pages[pageNumber];
            BitmapSource bitmap = Imaging.Decode(page.Jpeg);

            // Logical size of the page in 1/96 inch, after the user's rotation.
            var rotation = page.Rotation;
            double width = page.PixelWidth * 96.0 / page.Dpi;
            double height = page.PixelHeight * 96.0 / page.Dpi;
            if (rotation == 90 || rotation == 270) (width, height) = (height, width);

            // Turn landscape pages sideways on portrait paper (and the other way round).
            if ((width > height) != (printable.Width > printable.Height) && Math.Abs(width - height) > 1)
            {
                rotation = (rotation + 90) % 360;
                (width, height) = (height, width);
            }
            if (rotation != 0)
            {
                var turned = new TransformedBitmap(bitmap, new RotateTransform(rotation));
                turned.Freeze();
                bitmap = turned;
            }

            var scale = Math.Min(1.0, Math.Min(printable.Width / width, printable.Height / height));
            width *= scale;
            height *= scale;

            var image = new Image { Source = bitmap, Stretch = Stretch.Fill, Width = width, Height = height };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var canvas = new Canvas { Width = pageSize.Width, Height = pageSize.Height, Background = Brushes.White };
            Canvas.SetLeft(image, printable.X + (printable.Width - width) / 2);
            Canvas.SetTop(image, printable.Y + (printable.Height - height) / 2);
            canvas.Children.Add(image);

            var box = new Rect(pageSize);
            canvas.Measure(pageSize);
            canvas.Arrange(box);
            canvas.UpdateLayout();
            return new DocumentPage(canvas, pageSize, box, box);
        }
    }
}
