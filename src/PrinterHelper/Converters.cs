using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PrinterHelper.Documents;

namespace PrinterHelper
{
    /// <summary>Turns a page's small JPEG into a bitmap for the page list, once per page.</summary>
    public sealed class ThumbnailConverter : IValueConverter
    {
        static readonly ConditionalWeakTable<PageImage, Bitmap> Cache = new ConditionalWeakTable<PageImage, Bitmap>();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is PageImage page)) return null;
            return Cache.GetValue(page, p =>
            {
                using (var stream = new MemoryStream(p.ThumbnailJpeg, writable: false))
                    return new Bitmap(stream);
            });
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Clockwise rotation in degrees to a transform (a transform cannot bind to the page itself).</summary>
    public sealed class RotationConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            new RotateTransform(value is int degrees ? degrees : 0);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
