using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace PrinterHelper.Tests
{
    /// <summary>A self-contained check. The method is static and returns void or Task.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    sealed class TestAttribute : Attribute
    {
        public TestAttribute(string name) => Name = name;
        public string Name { get; }
        /// <summary>"windows" or "macos" to run only there.</summary>
        public string Only { get; set; }
    }

    /// <summary>A check against a real printer; read-only, it never starts a scan. Takes the printer's host.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    sealed class LiveTestAttribute : Attribute
    {
        public LiveTestAttribute(string name) => Name = name;
        public string Name { get; }
    }

    /// <summary>
    /// Runs every [Test] in this assembly. Arguments:
    ///   --live HOST   also run the [LiveTest] checks against a real printer (read-only)
    ///   --out DIR     keep generated files (PDFs, renders) in DIR for external checks
    ///   --filter TEXT run only tests whose name contains TEXT
    /// </summary>
    static class Program
    {
        static int failures;
        public static string OutDir { get; private set; }

        static int Main(string[] args)
        {
            OutDir = ArgValue(args, "--out") ?? Path.Combine(Path.GetTempPath(), "PrinterHelperTests");
            Directory.CreateDirectory(OutDir);
            var filter = ArgValue(args, "--filter");
            var live = ArgValue(args, "--live");
            if (args.Contains("--live") && live == null) live = "192.168.0.48";

            var methods = typeof(Program).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                .OrderBy(m => m.DeclaringType.Name, StringComparer.Ordinal).ThenBy(m => m.MetadataToken)
                .ToList();

            var ran = 0;
            foreach (var m in methods)
            {
                var test = m.GetCustomAttribute<TestAttribute>();
                if (test == null || (filter != null && test.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                if (test.Only == "windows" && !OperatingSystem.IsWindows() || test.Only == "macos" && !OperatingSystem.IsMacOS())
                {
                    Console.WriteLine($"SKIP  {test.Name} (only on {test.Only})");
                    continue;
                }
                Run(test.Name, () => m.Invoke(null, null));
                ran++;
            }
            if (live != null)
                foreach (var m in methods)
                {
                    var test = m.GetCustomAttribute<LiveTestAttribute>();
                    if (test == null) continue;
                    Run($"{test.Name} ({live})", () => m.Invoke(null, new object[] { live }));
                    ran++;
                }

            Console.WriteLine(failures == 0 ? $"ALL PASSED ({ran})" : $"{failures} FAILED of {ran}");
            Console.WriteLine("output: " + OutDir);
            return failures == 0 ? 0 : 1;
        }

        static string ArgValue(string[] args, string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;
        }

        static void Run(string name, Func<object> test)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (test() is Task task) task.GetAwaiter().GetResult();
                Console.WriteLine($"PASS  {name} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                failures++;
                var e = ex is TargetInvocationException tie ? tie.InnerException : ex;
                e = e.GetBaseException();
                Console.WriteLine($"FAIL  {name}: {e.GetType().Name}: {e.Message}");
                Console.WriteLine(e.StackTrace);
            }
        }
    }

    static class Assert
    {
        public static void Check(bool condition, string what)
        {
            if (!condition) throw new Exception("check failed: " + what);
        }

        public static void Throws<T>(Action action, string what) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new Exception("expected " + typeof(T).Name + ": " + what);
        }
    }

    static class Fixtures
    {
        public static string Dir => System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures");
        public static string Path(string name) => System.IO.Path.Combine(Dir, name);
        public static byte[] Bytes(string name) => File.ReadAllBytes(Path(name));
    }
}
