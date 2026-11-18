using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SkiaSharp;
using static PrinterHelper.Tests.Assert;

namespace PrinterHelper.Tests
{
    /// <summary>
    /// Checks of the files that build the release zips (packaging/macos, the workflow, the notices). They read the
    /// repository the tests were built from, so a broken plist or a missing notice shows up before CI packages it.
    /// </summary>
    static class PackagingTests
    {
        /// <summary>The Korean name in ko.lproj/InfoPlist.strings; Info.plist itself keeps the bundle's file name.</summary>
        const string AppName = "프린터 도우미";

        [Test("Mac Info.plist has the bundle keys")]
        static void MacInfoPlist()
        {
            var plist = ReadPlist(RepoPath("packaging/macos/Info.plist"));
            string Text(string key) => plist.TryGetValue(key, out var v) ? v as string : null;
            bool Flag(string key) => plist.TryGetValue(key, out var v) && v is true;

            Check(Text("CFBundleIdentifier") == "io.github.yangharry.printerhelper", "bundle id");
            var assemblyName = LoadXml(RepoPath("src/PrinterHelper/PrinterHelper.csproj")).Descendants("AssemblyName").Single().Value;
            Check(Text("CFBundleExecutable") == assemblyName, "CFBundleExecutable is the apphost's name " + assemblyName);
            // Finder shows the localized name only when the base CFBundleDisplayName is the bundle's file name, and
            // make-app.sh names the bundle after the executable.
            var makeApp = File.ReadAllLines(RepoPath("packaging/macos/make-app.sh"));
            Check(makeApp.Contains("exe=" + assemblyName) && makeApp.Contains("app=$out/$exe.app"), "make-app.sh builds " + assemblyName + ".app");
            Check(Text("CFBundleName") == assemblyName && Text("CFBundleDisplayName") == assemblyName, "base names are the file name " + assemblyName);
            Check(Flag("LSHasLocalizedDisplayName"), "LSHasLocalizedDisplayName");
            Check(Text("CFBundleDevelopmentRegion") == "ko", "development region ko");
            Check(Strings(plist, "CFBundleLocalizations").SequenceEqual(new[] { "ko" }), "localizations [ko]");
            Check(Text("CFBundlePackageType") == "APPL" && Text("CFBundleInfoDictionaryVersion") == "6.0", "package type, dictionary version");
            Check(Text("CFBundleIconFile") == "PrinterHelper", "icon file (make-app.sh copies PrinterHelper.icns)");
            Check(Version.TryParse(Text("LSMinimumSystemVersion"), out var minimum) && minimum.Major >= 14, "LSMinimumSystemVersion 14 or later");
            Check(Flag("NSHighResolutionCapable") && Flag("LSAppNapIsDisabled"), "high resolution, no App Nap");
            Check(Text("NSPrincipalClass") == "NSApplication", "principal class");
            Check(Text("NSLocalNetworkUsageDescription") == "같은 네트워크의 복합기를 찾아 스캔하고 상태를 읽습니다.", "local network text");
            Check(Text("NSDocumentsFolderUsageDescription") == "스캔한 PDF를 문서/Scans 폴더에 저장합니다.", "documents folder text");
            Check(Strings(plist, "NSBonjourServices").SequenceEqual(new[] { "_uscan._tcp", "_uscans._tcp", "_ipp._tcp", "_ipps._tcp" }), "Bonjour services");

            // make-app.sh stamps both versions; a placeholder here keeps the template obviously unstamped.
            Check(Text("CFBundleShortVersionString") == "0.0.0" && Text("CFBundleVersion") == "0.0.0", "version placeholders");
            var version = LoadXml(RepoPath("Directory.Build.props")).Descendants("Version").Single().Value;
            Check(Regex.IsMatch(Regex.Replace(version, "[-+].*$", ""), @"^\d+(\.\d+){0,2}$"), $"version {version} fits CFBundleVersion");

            var types = ((List<object>)plist["CFBundleDocumentTypes"]).Cast<Dictionary<string, object>>().ToList();
            Check(types.All(t => (string)t["CFBundleTypeRole"] == "Viewer" && (string)t["LSHandlerRank"] == "Alternate"), "documents: Viewer, Alternate");
            Check(types.All(t => !string.IsNullOrEmpty(t["CFBundleTypeName"] as string)), "every document type has a name");
            var utis = types.SelectMany(t => Strings(t, "LSItemContentTypes")).OrderBy(u => u, StringComparer.Ordinal);
            var expected = new[] { "com.adobe.pdf", "com.compuserve.gif", "com.microsoft.bmp", "org.webmproject.webp", "public.jpeg", "public.png", "public.tiff" };
            Check(utis.SequenceEqual(expected), "document UTIs: " + string.Join(", ", utis));
        }

        [Test("Mac InfoPlist.strings names the app")]
        static void MacInfoPlistStrings()
        {
            var path = RepoPath("packaging/macos/ko.lproj/InfoPlist.strings");
            // UTF-8 without a BOM, which plutil -lint (make-app.sh) and CFBundle accept for strings files.
            var bytes = File.ReadAllBytes(path);
            Check(!bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) && !bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }), "no BOM");
            string text = null;
            try { text = new UTF8Encoding(false, true).GetString(bytes); } catch (DecoderFallbackException) { }
            Check(text != null, "valid UTF-8");
            var entries = new Dictionary<string, string>();
            foreach (var line in text.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith("/*") && t.EndsWith("*/") || t.StartsWith("//")) continue;
                var m = Regex.Match(t, @"^""?(?<key>[A-Za-z0-9_]+)""?\s*=\s*""(?<value>(?:[^""\\]|\\.)*)"";$");
                Check(m.Success, "strings syntax: " + t);
                entries[m.Groups["key"].Value] = m.Groups["value"].Value;
            }
            Check(entries.GetValueOrDefault("CFBundleDisplayName") == AppName, "CFBundleDisplayName");
            Check(entries.GetValueOrDefault("CFBundleName") == AppName, "CFBundleName");

            var makeApp = File.ReadAllLines(RepoPath("packaging/macos/make-app.sh")).Select(l => l.Trim()).ToList();
            Check(makeApp.Contains("cp \"$here/ko.lproj/InfoPlist.strings\" \"$resources/ko.lproj/InfoPlist.strings\""), "make-app.sh copies it into the bundle");
            Check(makeApp.Contains("plutil -lint \"$resources/ko.lproj/InfoPlist.strings\""), "make-app.sh lints it");
        }

        [Test("Mac icon is the Windows artwork on Apple's icon grid")]
        static void MacIcon()
        {
            using var mac = SKBitmap.Decode(RepoPath("packaging/macos/icon-1024.png"));
            Check(mac != null && mac.Width == 1024 && mac.Height == 1024, "1024 x 1024 PNG");
            Check(mac.AlphaType != SKAlphaType.Opaque, "has transparency");
            foreach (var (x, y) in new[] { (0, 0), (1023, 0), (0, 1023), (1023, 1023), (512, 20), (20, 512) })
                Check(mac.GetPixel(x, y).Alpha == 0, $"transparent outside the artwork at {x},{y}");
            Check(mac.GetPixel(108, 108).Alpha < 128, "rounded corner");

            // The opaque square spans 100..923 both ways (a shadow may fade out around it).
            int First(Func<int, SKColor> at) => Enumerable.Range(0, 1024).First(i => at(i).Alpha >= 250);
            int Last(Func<int, SKColor> at) => Enumerable.Range(0, 1024).Last(i => at(i).Alpha >= 250);
            var box = new[] { First(x => mac.GetPixel(x, 512)), First(y => mac.GetPixel(512, y)), Last(x => mac.GetPixel(x, 512)), Last(y => mac.GetPixel(512, y)) };
            Check(Math.Abs(box[0] - 100) <= 1 && Math.Abs(box[1] - 100) <= 1 && Math.Abs(box[2] - 923) <= 1 && Math.Abs(box[3] - 923) <= 1,
                "824 px artwork centred on the canvas: " + string.Join(",", box));

            // Same picture as the 256 px image in app.ico: compare spots inside each shape (fractions of the artwork).
            using var windows = SKBitmap.Decode(IcoImage(File.ReadAllBytes(RepoPath("src/PrinterHelper/app.ico")), 256));
            Check(windows != null && windows.Width == 256, "app.ico has a 256 px image");
            var blue = new SKColor(0x25, 0x63, 0xEB);
            var spots = new (double X, double Y, SKColor Color, string What)[]
            {
                (0.06, 0.50, blue, "background"),
                (0.85, 0.30, blue, "background beside the paper"),
                (0.50, 0.25, SKColors.White, "paper going in"),
                (0.18, 0.45, new SKColor(0xDB, 0xEA, 0xFE), "printer body"),
                (0.50, 0.57, new SKColor(0x1E, 0x3A, 0x8A), "output slot"),
                (0.30, 0.62, SKColors.White, "page coming out"),
                (0.50, 0.73, blue, "text line"),
                (0.50, 0.93, blue, "background below the page"),
            };
            foreach (var s in spots)
            {
                var w = windows.GetPixel((int)(s.X * 256), (int)(s.Y * 256));
                var m = mac.GetPixel(100 + (int)(s.X * 824), 100 + (int)(s.Y * 824));
                Check(Near(w, s.Color), $"Windows icon {s.What} is {s.Color} (got {w})");
                Check(Near(m, s.Color), $"Mac icon {s.What} is {s.Color} (got {m})");
            }
        }

        [Test("Packaging scripts and workflow keep LF and strict bash")]
        static void PackagingScripts()
        {
            var attributes = File.ReadAllLines(RepoPath(".gitattributes")).Select(l => Regex.Replace(l.Trim(), @"\s+", " ")).ToList();
            foreach (var pattern in new[] { "*.sh", "*.plist", "*.strings", "*.yml" })
                Check(attributes.Contains(pattern + " text eol=lf"), ".gitattributes keeps " + pattern + " LF");
            var packaging = File.ReadAllLines(RepoPath("packaging/.gitattributes")).Select(l => Regex.Replace(l.Trim(), @"\s+", " ")).ToList();
            Check(packaging.Contains("*.py text eol=lf"), "packaging/.gitattributes keeps *.py LF");
            Check(packaging.Contains("licenses/** -text"), "packaging/.gitattributes leaves the license texts as they are");

            var files = new[]
            {
                "packaging/macos/make-app.sh", "packaging/macos/make-icns.sh", "packaging/macos/Info.plist",
                "packaging/macos/ko.lproj/InfoPlist.strings", ".github/workflows/build.yml", "packaging/copy-notices.py",
            };
            foreach (var file in files)
            {
                var bytes = File.ReadAllBytes(RepoPath(file));
                Check(!bytes.Contains((byte)'\r'), file + " has LF line endings");
                Check(!bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), file + " has no BOM");
            }
            foreach (var script in files.Where(f => f.EndsWith(".sh")))
            {
                var lines = File.ReadAllLines(RepoPath(script));
                Check(lines[0] == "#!/usr/bin/env bash", script + " starts with a bash shebang");
                Check(lines.Contains("set -euo pipefail"), script + " stops on errors");
            }
        }

        [Test("Workflow refers to files that exist")]
        static void WorkflowPaths()
        {
            var workflow = File.ReadAllText(RepoPath(".github/workflows/build.yml"));
            var paths = Regex.Matches(workflow, @"(?<![\w$/.-])(?:src|tests|packaging|cli)/[\w./-]*[\w]")
                .Select(m => m.Value).Distinct().ToList();
            Check(paths.Count >= 5, "found the repository paths in the workflow");
            foreach (var path in paths)
                Check(File.Exists(RepoPath(path)) || Directory.Exists(RepoPath(path)), "workflow path exists: " + path);
            foreach (var file in new[] { "PrinterHelper.slnx", "LICENSE", "README.md", "THIRD-PARTY-NOTICES.md" })
                Check(workflow.Contains(file) && File.Exists(RepoPath(file)), "workflow uses " + file);
        }

        [Test("Workflow pins its actions and opts out of build telemetry")]
        static void WorkflowSupplyChain()
        {
            var workflow = File.ReadAllLines(RepoPath(".github/workflows/build.yml"));
            var uses = workflow.Select(l => Regex.Match(l, @"^\s*(?:- )?uses:\s*(?<action>\S+)(?<rest>.*)$")).Where(m => m.Success).ToList();
            Check(uses.Count >= 4, "found the actions");
            foreach (var m in uses)
            {
                var action = m.Groups["action"].Value;
                Check(Regex.IsMatch(action, @"^[\w.-]+/[\w.-]+@[0-9a-f]{40}$"), action + " is pinned to a full commit SHA");
                Check(Regex.IsMatch(m.Groups["rest"].Value, @"^ # v\d+\.\d+\.\d+$"), action + " names its release in a comment");
            }
            // The same action at one version everywhere.
            foreach (var group in uses.GroupBy(m => m.Groups["action"].Value.Split('@')[0]))
                Check(group.Select(m => m.Groups["action"].Value + m.Groups["rest"].Value).Distinct().Count() == 1, group.Key + " is pinned once");

            Check(workflow.Contains("  AVALONIA_TELEMETRY_OPTOUT: '1'"), "workflow env sets AVALONIA_TELEMETRY_OPTOUT");
            var install = File.ReadAllLines(RepoPath("install.ps1")).Select(l => l.Trim()).ToList();
            var optOut = install.IndexOf("$env:AVALONIA_TELEMETRY_OPTOUT = '1'");
            var publish = install.FindIndex(l => l.StartsWith("& dotnet publish", StringComparison.Ordinal));
            Check(optOut >= 0 && publish > optOut, "install.ps1 opts out before dotnet publish");
        }

        [Test("Third-party notices name every shipped package")]
        static void ThirdPartyNotices()
        {
            var notices = File.ReadAllText(RepoPath("THIRD-PARTY-NOTICES.md"));
            bool Named(string id) => notices.Contains(id, StringComparison.OrdinalIgnoreCase);
            // A family named once covers its parts (SkiaSharp.NativeAssets.macOS, Avalonia.Win32...).
            var families = new[] { "Avalonia.", "SkiaSharp.", "HarfBuzzSharp." };
            bool Covered(string id) => Named(id) || families.Any(f => id.StartsWith(f, StringComparison.OrdinalIgnoreCase) && Named(f.TrimEnd('.')));

            var direct = Directory.GetFiles(RepoPath("src"), "*.csproj", SearchOption.AllDirectories)
                .SelectMany(p => LoadXml(p).Descendants("PackageReference"))
                .Select(r => (string)r.Attribute("Include")).Distinct().ToList();
            Check(direct.Count > 0, "found package references");
            foreach (var id in direct)
                Check(Named(id), "THIRD-PARTY-NOTICES.md names " + id);

            // Transitive packages too, once restore has run (CI restores before testing): everything that puts a
            // runtime or native file into a Windows or Mac publish.
            var assets = RepoPath("src/PrinterHelper/obj/project.assets.json");
            if (!File.Exists(assets)) return;
            using var json = JsonDocument.Parse(File.ReadAllBytes(assets));
            var shipped = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in json.RootElement.GetProperty("targets").EnumerateObject().Where(t => t.Name.Contains('/')))
                foreach (var library in target.Value.EnumerateObject())
                {
                    if (library.Value.GetProperty("type").GetString() != "package") continue;
                    var files = new[] { "runtime", "native" }
                        .Where(kind => library.Value.TryGetProperty(kind, out _))
                        .SelectMany(kind => library.Value.GetProperty(kind).EnumerateObject().Select(f => f.Name));
                    if (files.Any(f => !f.EndsWith("/_._"))) shipped.Add(library.Name.Split('/')[0]);
                }
            Check(shipped.Contains("SkiaSharp") && shipped.Contains("Avalonia"), "read the restored packages");
            foreach (var id in shipped)
                Check(Covered(id), "THIRD-PARTY-NOTICES.md covers " + id);
        }

        [Test("Third-party license files ship with both releases")]
        static void ShippedLicenseFiles()
        {
            var notices = File.ReadAllText(RepoPath("THIRD-PARTY-NOTICES.md"));
            Check(!Regex.IsMatch(notices, @"/(blob|tree)/(main|master)/"), "no links to a moving branch");
            Check(notices.Contains("licenses/pdfium/") && notices.Contains("packaging/licenses/pdfium"), "THIRD-PARTY-NOTICES.md points to the shipped PDFium texts");

            // The PDFium packages carry no license files; the repository keeps the texts for the version referenced.
            var folder = RepoPath("packaging/licenses/pdfium");
            Check(Directory.Exists(folder), "packaging/licenses/pdfium exists");
            var expected = new[]
            {
                "README.txt", "pdfium.txt", "pdfium-binaries.txt", "abseil.txt", "agg23.txt", "brotli.txt", "dragonbox-Apache2-LLVM.txt",
                "dragonbox-Boost.txt", "fast_float.txt", "freetype.txt", "harfbuzz.txt", "highway.txt", "icu.txt", "lcms.txt", "libcxx.txt",
                "libcxxabi.txt", "libjpeg_turbo.txt", "libjpeg_turbo-ijg.txt", "libopenjpeg.txt", "libpng.txt", "libyuv.txt", "llvm-libc.txt",
                "simdutf.txt", "zlib.txt",
            };
            var present = Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Check(present.SequenceEqual(expected.OrderBy(n => n, StringComparer.Ordinal)), "PDFium license files: " + string.Join(", ", present));
            foreach (var name in expected)
                Check(new FileInfo(Path.Combine(folder, name)).Length > 200, name + " has text");
            var readme = File.ReadAllText(Path.Combine(folder, "README.txt"));
            foreach (var name in expected.Where(n => n != "README.txt"))
                Check(Regex.IsMatch(readme, @"(?m)^" + Regex.Escape(name) + " "), "README.txt names the source of " + name);
            var pdfium = File.ReadAllText(Path.Combine(folder, "pdfium.txt"));
            Check(pdfium.StartsWith("Copyright 2014 The PDFium Authors") && pdfium.Contains("Apache License") && !pdfium.Contains("\n//"), "pdfium.txt is PDFium's LICENSE");
            foreach (var (file, words) in new[] { ("libjpeg_turbo-ijg.txt", "Independent JPEG Group"), ("freetype.txt", "FreeType"), ("zlib.txt", "Jean-loup Gailly"), ("lcms.txt", "Little Color Management System"), ("libopenjpeg.txt", "BSD"), ("agg23.txt", "Anti-Grain Geometry") })
                Check(File.ReadAllText(Path.Combine(folder, file), Encoding.Latin1).Contains(words), file + " is the " + words + " license");

            var version = LoadXml(RepoPath("src/PrinterHelper.Core/PrinterHelper.Core.csproj")).Descendants("PackageReference")
                .Where(r => ((string)r.Attribute("Include")).StartsWith("bblanchon.PDFium.", StringComparison.Ordinal))
                .Select(r => (string)r.Attribute("Version")).Distinct().Single();
            var branch = "chromium/" + version.Split('.').Last();
            Check(readme.Contains(version) && readme.Contains(branch), $"README.txt is for bblanchon.PDFium {version} ({branch}); remake the texts when the package changes");

            // Both releases run packaging/copy-notices.py: the Windows zip step and make-app.sh for the Mac bundle.
            var workflow = File.ReadAllText(RepoPath(".github/workflows/build.yml"));
            Check(workflow.Contains("python packaging/copy-notices.py win-x64 \"$stage/licenses\""), "the Windows zip gets a licenses folder");
            var makeApp = File.ReadAllText(RepoPath("packaging/macos/make-app.sh"));
            Check(makeApp.Contains("python3 -I \"$repo/packaging/copy-notices.py\" \"$rid\" \"$resources/licenses\""), "the Mac bundle gets a licenses folder");
            var script = File.ReadAllText(RepoPath("packaging/copy-notices.py"));
            Check(script.Contains("'packaging' / 'licenses' / 'pdfium'") && script.Contains("project.assets.json"), "copy-notices.py copies the PDFium texts and reads the restore");
        }

        static bool Near(SKColor a, SKColor b) =>
            a.Alpha == 255 && Math.Abs(a.Red - b.Red) <= 6 && Math.Abs(a.Green - b.Green) <= 6 && Math.Abs(a.Blue - b.Blue) <= 6;

        /// <summary>The PNG stored for one size in an .ico file (tools/make-icon.ps1 writes PNG entries).</summary>
        static byte[] IcoImage(byte[] ico, int size)
        {
            var count = BitConverter.ToUInt16(ico, 4);
            for (var i = 0; i < count; i++)
            {
                var entry = 6 + 16 * i;
                var width = ico[entry] == 0 ? 256 : ico[entry];
                if (width != size) continue;
                var length = (int)BitConverter.ToUInt32(ico, entry + 8);
                var offset = (int)BitConverter.ToUInt32(ico, entry + 12);
                return ico.AsSpan(offset, length).ToArray();
            }
            throw new Exception($"no {size} px image in the icon");
        }

        static IEnumerable<string> Strings(Dictionary<string, object> dict, string key) =>
            dict.TryGetValue(key, out var v) && v is List<object> list ? list.Cast<string>() : Enumerable.Empty<string>();

        static Dictionary<string, object> ReadPlist(string path)
        {
            var root = LoadXml(path).Root;
            Check(root.Name == "plist" && (string)root.Attribute("version") == "1.0", "plist root");
            return (Dictionary<string, object>)PlistValue(root.Elements().Single());
        }

        /// <summary>From a stream, so the path is never read as a URI; Apple's DOCTYPE names a DTD on the web, never fetched.</summary>
        static XDocument LoadXml(string path)
        {
            using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            return XDocument.Load(reader);
        }

        static object PlistValue(XElement e)
        {
            switch (e.Name.LocalName)
            {
                case "string": return e.Value;
                case "true": return true;
                case "false": return false;
                case "integer": return long.Parse(e.Value, CultureInfo.InvariantCulture);
                case "array": return e.Elements().Select(PlistValue).ToList();
                case "dict":
                    var dict = new Dictionary<string, object>();
                    var items = e.Elements().ToList();
                    Check(items.Count % 2 == 0, "dict holds key/value pairs");
                    for (var i = 0; i < items.Count; i += 2)
                    {
                        Check(items[i].Name.LocalName == "key", "key before each value");
                        Check(dict.TryAdd(items[i].Value, PlistValue(items[i + 1])), "no duplicate key " + items[i].Value);
                    }
                    return dict;
                default: throw new Exception("unexpected plist element " + e.Name);
            }
        }

        /// <summary>A path in the repository the tests were built from (the folder holding PrinterHelper.slnx).</summary>
        static string RepoPath(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PrinterHelper.slnx"))) dir = dir.Parent;
            Check(dir != null, "the tests run from inside the repository (PrinterHelper.slnx above " + AppContext.BaseDirectory + ")");
            return Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
