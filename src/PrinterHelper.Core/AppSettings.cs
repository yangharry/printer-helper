using System;
using System.IO;
using System.Xml.Serialization;

namespace PrinterHelper
{
    /// <summary>
    /// User choices kept between runs: %APPDATA%\PrinterHelper\settings.xml on Windows (the same file 1.x used),
    /// ~/Library/Application Support/PrinterHelper/settings.xml on macOS.
    /// </summary>
    public sealed class AppSettings
    {
        public string PrinterHost { get; set; }
        public string SaveFolder { get; set; }
        /// <summary>Auto, Feeder or Platen.</summary>
        public string Source { get; set; } = "Auto";
        public bool Color { get; set; }
        public int Dpi { get; set; } = 200;
        public string PaperSize { get; set; } = "A4";
        public bool OpenAfterSave { get; set; } = true;
        /// <summary>The print queue last used on macOS (Windows remembers it in its own print dialog).</summary>
        public string PrintQueue { get; set; }

        /// <summary>Where settings.xml lives; tests point it somewhere else.</summary>
        public static string FilePath { get; set; } = DefaultFilePath();

        static string DefaultFilePath()
        {
            var folder = OperatingSystem.IsMacOS()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
                : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(folder, "PrinterHelper", "settings.xml");
        }

        public static string DefaultSaveFolder => Path.Combine(
            OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents"),
            "Scans");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    using (var stream = File.OpenRead(FilePath))
                        return (AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(stream);
                }
            }
            catch
            {
                // A damaged settings file just means starting with defaults.
            }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                using (var stream = File.Create(FilePath))
                    new XmlSerializer(typeof(AppSettings)).Serialize(stream, this);
            }
            catch
            {
                // Not being able to remember settings is not worth interrupting the user.
            }
        }
    }
}
