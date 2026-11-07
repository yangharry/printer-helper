using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace PrinterHelper.Documents
{
    /// <summary>One page of the working document: a JPEG plus its rotation.</summary>
    public sealed class PageImage : INotifyPropertyChanged
    {
        int rotation;
        int number;

        PageImage(byte[] jpeg, JpegInfo info, double dpi, string source, int rotation)
        {
            Jpeg = jpeg;
            PixelWidth = info.Width;
            PixelHeight = info.Height;
            Components = info.Components;
            Dpi = dpi;
            Source = source;
            Rotation = rotation;
            Thumbnail = Imaging.Decode(jpeg, 200);
        }

        public byte[] Jpeg { get; }
        public int PixelWidth { get; }
        public int PixelHeight { get; }
        /// <summary>1 = gray, 3 = color.</summary>
        public int Components { get; }
        public double Dpi { get; }
        /// <summary>Where the page came from: "스캔" or a file name.</summary>
        public string Source { get; }
        public BitmapSource Thumbnail { get; }

        /// <summary>Clockwise rotation in degrees: 0, 90, 180 or 270.</summary>
        public int Rotation
        {
            get => rotation;
            set
            {
                var normalized = ((value % 360) + 360) % 360 / 90 * 90;
                if (normalized == rotation) return;
                rotation = normalized;
                OnPropertyChanged();
            }
        }

        public int Number
        {
            get => number;
            set
            {
                if (value == number) return;
                number = value;
                OnPropertyChanged();
            }
        }

        public static PageImage FromJpeg(byte[] jpeg, double dpi, string source, int rotation = 0)
        {
            var info = JpegInfo.TryRead(jpeg);
            if (info == null)
            {
                // Not embeddable as-is (e.g. CMYK or unknown height): decode and re-encode.
                jpeg = Imaging.EncodeJpeg(Imaging.Decode(jpeg));
                info = JpegInfo.TryRead(jpeg) ?? throw new InvalidOperationException("그림을 JPEG로 바꾸지 못했습니다.");
            }
            return new PageImage(jpeg, info, dpi, source, rotation);
        }

        public static PageImage FromBitmap(BitmapSource bitmap, double dpi, string source, int rotation = 0)
        {
            var jpeg = Imaging.EncodeJpeg(bitmap);
            var info = JpegInfo.TryRead(jpeg) ?? throw new InvalidOperationException("그림을 JPEG로 바꾸지 못했습니다.");
            return new PageImage(jpeg, info, dpi, source, rotation);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
