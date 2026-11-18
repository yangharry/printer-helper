using System;
using PrinterHelper.Documents;

namespace PrinterHelper.Printing
{
    /// <summary>A rectangle in inches, measured from the sheet's top-left corner (y grows downwards).</summary>
    public readonly struct PrintRect
    {
        public PrintRect(double x, double y, double width, double height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public double X { get; }
        public double Y { get; }
        public double Width { get; }
        public double Height { get; }
        public double Right => X + Width;
        public double Bottom => Y + Height;

        public override string ToString() => $"({X:0.###}, {Y:0.###}, {Width:0.###} x {Height:0.###})";
    }

    /// <summary>Where one page goes on the sheet.</summary>
    public readonly struct PrintPlacement
    {
        public PrintPlacement(int rotation, PrintRect destination, double scale)
        {
            Rotation = rotation;
            Destination = destination;
            Scale = scale;
        }

        /// <summary>Clockwise rotation to apply to the stored image: the page's own rotation, plus a quarter turn when it was turned sideways.</summary>
        public int Rotation { get; }
        /// <summary>The box the rotated image fills, in inches from the sheet's top-left corner.</summary>
        public PrintRect Destination { get; }
        /// <summary>1 = real size; less when the page had to be shrunk to fit.</summary>
        public double Scale { get; }
    }

    /// <summary>
    /// How a page is put on a sheet, the same on every platform (taken from 1.0.1): real size, shrunk
    /// only when it does not fit inside the printable area, turned sideways when that fits the paper
    /// better, and centred.
    /// </summary>
    public static class PrintLayout
    {
        /// <summary>A typical laser printer margin, used when the printer does not report a usable one.</summary>
        public const double DefaultMargin = 0.17;

        // Pages within a pixel at 96 dpi of square are left as they are.
        const double SquareTolerance = 1.0 / 96;

        /// <summary>
        /// One margin for all four sides: the largest of the printer's margins that is between 0 and
        /// 1 inch, so the result is centred whatever orientation was chosen; otherwise 0.17 inch.
        /// </summary>
        public static double UniformMargin(double left, double top, double right, double bottom)
        {
            double margin = 0;
            foreach (var m in new[] { left, top, right, bottom })
                if (m > 0 && m < 1 && m > margin) margin = m;
            return margin > 0 ? margin : DefaultMargin;
        }

        /// <summary>The sheet minus the same margin on every side.</summary>
        public static PrintRect PrintableArea(double sheetWidth, double sheetHeight, double margin) =>
            new PrintRect(margin, margin, Math.Max(SquareTolerance, sheetWidth - 2 * margin), Math.Max(SquareTolerance, sheetHeight - 2 * margin));

        /// <summary>The page's size in inches as it is shown, after its own rotation.</summary>
        public static (double Width, double Height) PageSize(PageImage page)
        {
            var dpi = page.Dpi > 0 ? page.Dpi : 96;
            double width = page.PixelWidth / dpi, height = page.PixelHeight / dpi;
            return page.Rotation == 90 || page.Rotation == 270 ? (height, width) : (width, height);
        }

        public static PrintPlacement Place(PageImage page, PrintRect printable)
        {
            var dpi = page.Dpi > 0 ? page.Dpi : 96;
            return Place(page.PixelWidth / dpi, page.PixelHeight / dpi, page.Rotation, printable);
        }

        /// <param name="imageWidth">Width of the stored image in inches (pixels / dpi), before rotation.</param>
        /// <param name="imageHeight">Height of the stored image in inches, before rotation.</param>
        /// <param name="rotation">The page's clockwise rotation: 0, 90, 180 or 270.</param>
        /// <param name="printable">The area to fill, in inches from the sheet's top-left corner.</param>
        public static PrintPlacement Place(double imageWidth, double imageHeight, int rotation, PrintRect printable)
        {
            if (!(imageWidth > 0) || !(imageHeight > 0)) throw new ArgumentOutOfRangeException(nameof(imageWidth), "페이지 크기가 올바르지 않습니다.");
            rotation = ((rotation % 360) + 360) % 360 / 90 * 90;
            double width = imageWidth, height = imageHeight;
            if (rotation == 90 || rotation == 270) (width, height) = (height, width);

            // Turn landscape pages sideways on portrait paper (and the other way round).
            if ((width > height) != (printable.Width > printable.Height) && Math.Abs(width - height) > SquareTolerance)
            {
                rotation = (rotation + 90) % 360;
                (width, height) = (height, width);
            }

            // Never enlarge: a small receipt prints at its real size, not blown up to the sheet.
            var scale = Math.Min(1.0, Math.Min(printable.Width / width, printable.Height / height));
            width *= scale;
            height *= scale;
            var x = printable.X + (printable.Width - width) / 2;
            var y = printable.Y + (printable.Height - height) / 2;
            return new PrintPlacement(rotation, new PrintRect(x, y, width, height), scale);
        }
    }
}
