using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Printing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PrinterHelper.Documents;
using PrinterHelper.Escl;
using PrinterHelper.Ipp;
using PrinterHelper.Printing;

namespace PrinterHelper
{
    public partial class MainWindow : Window
    {
        const string AppTitle = "프린터 도우미";

        // Paper sizes in 1/300 inch.
        static readonly Dictionary<string, (int Width, int Height)> PaperSizes = new Dictionary<string, (int, int)>
        {
            ["A4"] = (2480, 3508),
            ["Letter"] = (2550, 3300),
            ["Legal"] = (2550, 4200),
        };

        static readonly Dictionary<string, string> AdfMessages = new Dictionary<string, string>
        {
            ["ScannerAdfEmpty"] = "급지대에 종이가 없습니다.",
            ["ScannerAdfJam"] = "급지대에 종이가 걸렸습니다. 걸린 종이를 빼고 다시 넣어 주세요.",
            ["ScannerAdfDoorOpen"] = "급지대 덮개가 열려 있습니다.",
            ["ScannerAdfHatchOpen"] = "급지대 덮개가 열려 있습니다.",
            ["ScannerAdfMispick"] = "급지대가 종이를 집지 못했습니다. 종이를 다시 넣어 주세요.",
            ["ScannerAdfMultipickDetected"] = "종이가 여러 장 한꺼번에 들어갔습니다. 다시 넣어 주세요.",
        };

        readonly ObservableCollection<PageImage> pages = new ObservableCollection<PageImage>();
        readonly HttpClient statusHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        readonly DispatcherTimer statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };

        AppSettings settings;
        EsclClient escl;
        ScannerCapabilities caps;
        ScannerStatus lastStatus;
        DateTime lastPrinterInfoAt = DateTime.MinValue;
        bool statusBusy;
        bool busy;
        bool saving;
        bool dirty;
        // Bumped on every change, so a save can tell whether pages changed while it was writing.
        int editVersion;
        // Bumped on every connection attempt, so an older, slower attempt cannot overwrite a newer one.
        int connectGeneration;
        CancellationTokenSource operationCts;
        string currentJobPath;
        EsclClient currentJobClient;

        // The most recent front-side batch, for "scan back sides".
        List<PageImage> lastBatch;
        // The most recent duplex merge, so its back-side order can be flipped.
        DuplexMerge lastMerge;

        sealed class DuplexMerge
        {
            public List<PageImage> Fronts;
            public List<PageImage> Backs;
            public bool BacksReversed;
        }

        public MainWindow()
        {
            InitializeComponent();
            // Version in the title bar, so it is easy to tell which build is running.
            var version = typeof(MainWindow).Assembly.GetName().Version;
            Title = $"{AppTitle} {version.Major}.{version.Minor}.{version.Build}";
            PageList.ItemsSource = pages;
            pages.CollectionChanged += (s, e) => UpdatePageState();
            statusTimer.Tick += async (s, e) => await RefreshStatusAsync();
        }

        // ───────────────────────────── startup / shutdown ─────────────────────────────

        async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            settings = AppSettings.Load();
            if (string.IsNullOrWhiteSpace(settings.SaveFolder)) settings.SaveFolder = AppSettings.DefaultSaveFolder;

            SelectByTag(SourceCombo, settings.Source, "Auto");
            SelectByTag(ColorCombo, settings.Color ? "Color" : "Gray", "Gray");
            SelectByTag(PaperCombo, settings.PaperSize, "A4");
            FillResolutions(new[] { 100, 200, 300 });
            OpenAfterSaveCheck.IsChecked = settings.OpenAfterSave;
            SaveFolderText.Text = settings.SaveFolder;
            SaveFolderText.ToolTip = settings.SaveFolder;
            UpdatePageState();

            // Files dropped on the desktop shortcut (or passed on the command line) are opened right away.
            var connecting = ConnectAsync(settings.PrinterHost);
            var files = Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).ToList();
            if (files.Count > 0) await ImportFilesAsync(files);
            await connecting;
            statusTimer.Start();
        }

        void Window_Closing(object sender, CancelEventArgs e)
        {
            if (saving)
            {
                e.Cancel = true;
                SetStatus("PDF를 저장하는 중입니다. 저장이 끝난 뒤에 닫아 주세요.", true);
                return;
            }
            if (dirty && pages.Count > 0)
            {
                var question = currentJobPath != null
                    ? $"스캔하는 중입니다. 지금 닫으면 스캔을 멈추고, 저장하지 않은 {pages.Count}쪽도 사라집니다.\n그래도 닫을까요?"
                    : $"저장하지 않은 페이지가 {pages.Count}쪽 있습니다.\n그래도 닫을까요?";
                var answer = MessageBox.Show(this, question, AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) { e.Cancel = true; return; }
            }

            statusTimer.Stop();
            operationCts?.Cancel();
            // Closing mid-scan: make sure the printer stops feeding paper.
            var job = currentJobPath;
            var client = currentJobClient;
            if (job != null && client != null)
                Task.Run(() => client.CancelJobAsync(job)).Wait(TimeSpan.FromSeconds(5));
            SaveSettings();
        }

        void MarkDirty()
        {
            dirty = true;
            editVersion++;
        }

        void SaveSettings()
        {
            if (settings == null) return;
            settings.Source = SelectedTag(SourceCombo) ?? "Auto";
            settings.Color = SelectedTag(ColorCombo) == "Color";
            settings.PaperSize = SelectedTag(PaperCombo) ?? "A4";
            if (DpiCombo.SelectedItem is ComboBoxItem dpiItem && dpiItem.Tag is int dpi) settings.Dpi = dpi;
            settings.OpenAfterSave = OpenAfterSaveCheck.IsChecked == true;
            settings.Save();
        }

        // ───────────────────────────── printer connection ─────────────────────────────

        async Task ConnectAsync(string preferredHost)
        {
            var generation = ++connectGeneration;
            SetPrinterHeader("프린터 찾는 중…", "", (Brush)FindResource("MutedBrush"));
            escl?.Dispose();
            escl = null;
            caps = null;
            UpdateCommands();

            string host = null;
            string model = null;
            if (!string.IsNullOrWhiteSpace(preferredHost))
            {
                model = await PrinterDiscovery.ProbeAsync(preferredHost, CancellationToken.None);
                if (generation != connectGeneration) return;
                if (model != null) host = preferredHost;
            }
            if (host == null)
            {
                var found = await PrinterDiscovery.FindScannersAsync(CancellationToken.None);
                if (generation != connectGeneration) return;
                var first = found.FirstOrDefault();
                if (first != null) { host = first.Host; model = first.Model; }
            }

            if (host == null)
            {
                SetPrinterHeader("스캐너를 찾지 못했습니다", preferredHost ?? "", (Brush)FindResource("ErrorBrush"));
                PrinterStateText.Text = "연결 안 됨";
                SetStatus("프린터가 켜져 있는지 확인하고, 위의 [프린터 변경]에서 IP 주소를 직접 넣어 보세요.", true);
                UpdateCommands();
                return;
            }

            var client = new EsclClient(host);
            ScannerCapabilities found_caps;
            try
            {
                found_caps = await client.GetCapabilitiesAsync(CancellationToken.None);
            }
            catch
            {
                found_caps = new ScannerCapabilities { MakeAndModel = model, HasFeeder = true, HasPlaten = true };
            }
            if (generation != connectGeneration)
            {
                client.Dispose();
                return;
            }
            escl = client;
            caps = found_caps;
            settings.PrinterHost = host;
            SetPrinterHeader(caps.MakeAndModel ?? model ?? "스캐너", host, (Brush)FindResource("OkBrush"));
            FillResolutions(caps.Resolutions);
            if (!caps.HasFeeder) SelectByTag(SourceCombo, "Platen", "Platen");
            lastPrinterInfoAt = DateTime.MinValue;
            UpdateCommands();
            await RefreshStatusAsync();
        }

        async void ChangePrinter_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            var picker = new PrinterPickerWindow(escl?.Host ?? settings.PrinterHost) { Owner = this };
            if (picker.ShowDialog() != true) return;
            await ConnectAsync(picker.SelectedHost);
        }

        async Task RefreshStatusAsync()
        {
            if (statusBusy || escl == null || busy) return;
            statusBusy = true;
            try
            {
                var client = escl;
                ScannerStatus status;
                try
                {
                    status = await client.GetStatusAsync(CancellationToken.None);
                }
                catch
                {
                    if (client != escl) return;
                    lastStatus = null;
                    StateDot.Fill = (Brush)FindResource("ErrorBrush");
                    PrinterStateText.Text = "연결 안 됨";
                    FeederText.Text = "급지대 -";
                    return;
                }
                if (client != escl) return;
                lastStatus = status;
                StateDot.Fill = (Brush)FindResource("OkBrush");
                FeederText.Text = FeederLabel(status);
                if (DateTime.UtcNow - lastPrinterInfoAt > TimeSpan.FromSeconds(15))
                {
                    lastPrinterInfoAt = DateTime.UtcNow;
                    try
                    {
                        var info = await IppClient.GetPrinterInfoAsync(statusHttp, client.Host, CancellationToken.None);
                        if (client == escl) ShowPrinterInfo(info);
                    }
                    catch
                    {
                        // Status from IPP is a nice extra; scanning works without it.
                        PrinterStateText.Text = status.IsIdle ? "대기 중" : "작업 중";
                    }
                }
            }
            finally
            {
                statusBusy = false;
            }
        }

        void ShowPrinterInfo(PrinterInfo info)
        {
            var problems = info.Reasons.Select(ReasonLabel).Where(r => r != null).Distinct().ToList();
            string state;
            switch (info.State)
            {
                case 4: state = "작업 중"; break;
                case 5: state = "멈춤"; break;
                default: state = "대기 중"; break;
            }
            PrinterStateText.Text = problems.Count > 0 ? string.Join(", ", problems) : state;
            PrinterStateText.Foreground = problems.Count > 0 ? (Brush)FindResource("WarnBrush") : (Brush)FindResource("TextBrush");

            if (info.TonerLevel.HasValue)
            {
                TonerText.Text = $"토너 {info.TonerLevel}%";
                var low = info.TonerLowLevel ?? 10;
                TonerText.Foreground = info.TonerLevel <= low ? (Brush)FindResource("WarnBrush") : (Brush)FindResource("TextBrush");
            }
            else
            {
                TonerText.Text = "토너 -";
            }
        }

        static string ReasonLabel(string reason)
        {
            var key = reason;
            foreach (var suffix in new[] { "-report", "-warning", "-error" })
                if (key.EndsWith(suffix, StringComparison.Ordinal)) key = key.Substring(0, key.Length - suffix.Length);
            switch (key)
            {
                case "none": return null;
                case "media-empty":
                case "media-needed": return "용지 없음";
                case "media-jam": return "용지 걸림";
                case "toner-low":
                case "marker-supply-low": return "토너 부족";
                case "toner-empty":
                case "marker-supply-empty": return "토너 없음";
                case "door-open":
                case "cover-open": return "덮개 열림";
                case "input-tray-missing": return "용지함 없음";
                case "paused": return "일시 정지";
                case "offline": return "오프라인";
                case "other": return null;
                default: return key;
            }
        }

        static string FeederLabel(ScannerStatus status)
        {
            switch (status.AdfState)
            {
                case null: return "급지대 없음";
                case "ScannerAdfLoaded": return "급지대: 종이 있음";
                case "ScannerAdfEmpty": return "급지대: 비어 있음";
                case "ScannerAdfJam": return "급지대: 종이 걸림";
                case "ScannerAdfDoorOpen":
                case "ScannerAdfHatchOpen": return "급지대: 덮개 열림";
                default: return "급지대: " + status.AdfState;
            }
        }

        void SetPrinterHeader(string name, string host, Brush dot)
        {
            PrinterNameText.Text = name;
            PrinterHostText.Text = host;
            StateDot.Fill = dot;
        }

        void FillResolutions(IEnumerable<int> resolutions)
        {
            var wanted = settings?.Dpi ?? 200;
            if (DpiCombo.SelectedItem is ComboBoxItem current && current.Tag is int selected) wanted = selected;
            DpiCombo.Items.Clear();
            foreach (var dpi in resolutions.Where(r => r >= 75 && r <= 600))
            {
                var label = dpi == 200 ? "200 dpi (문서에 알맞음)" : dpi == 300 ? "300 dpi (선명함)" : $"{dpi} dpi";
                DpiCombo.Items.Add(new ComboBoxItem { Content = label, Tag = dpi });
            }
            var match = DpiCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == wanted)
                        ?? DpiCombo.Items.Cast<ComboBoxItem>().OrderBy(i => Math.Abs((int)i.Tag - 200)).FirstOrDefault();
            DpiCombo.SelectedItem = match;
        }

        // ───────────────────────────── scanning ─────────────────────────────

        async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync(backSides: false);

        async void BackSide_Click(object sender, RoutedEventArgs e) => await ScanAsync(backSides: true);

        void Cancel_Click(object sender, RoutedEventArgs e)
        {
            operationCts?.Cancel();
            CancelButton.IsEnabled = false;
            SetStatus("멈추는 중…");
        }

        async Task ScanAsync(bool backSides)
        {
            if (busy) return;
            var client = escl;
            if (client == null)
            {
                SetStatus("스캐너에 연결되어 있지 않습니다. 위의 [프린터 변경]을 눌러 주세요.", true);
                return;
            }

            var dpi = DpiCombo.SelectedItem is ComboBoxItem dpiItem && dpiItem.Tag is int d ? d : 200;
            var color = SelectedTag(ColorCombo) == "Color";
            var paper = PaperSizes[SelectedTag(PaperCombo) ?? "A4"];
            var sourceChoice = backSides ? "Feeder" : SelectedTag(SourceCombo) ?? "Auto";
            var fronts = backSides ? lastBatch : null;

            var batch = new List<PageImage>();
            string failure = null;
            var canceled = false;
            ScanSource? usedSource = null;
            BeginOperation("스캔 멈추기");
            var ct = operationCts.Token;
            try
            {
                var status = await WaitForIdleAsync(client, ct);
                if (status == null) return;

                ScanSource source;
                if (sourceChoice == "Platen") source = ScanSource.Platen;
                else if (sourceChoice == "Feeder") source = ScanSource.Feeder;
                else if (status.AdfLoaded) source = ScanSource.Feeder;
                else if (status.AdfState != null && status.AdfState != "ScannerAdfEmpty" && AdfMessages.ContainsKey(status.AdfState))
                {
                    SetStatus(AdfMessages[status.AdfState], true);
                    return;
                }
                else
                {
                    if (caps?.HasPlaten == false)
                    {
                        SetStatus("급지대에 종이를 넣어 주세요.", true);
                        return;
                    }
                    var answer = MessageBox.Show(this, "급지대에 종이가 없습니다.\n유리판에 올린 1장을 스캔할까요?",
                        AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                    if (answer != MessageBoxResult.Yes) { SetStatus("급지대에 종이를 넣고 다시 [스캔]을 누르세요."); return; }
                    source = ScanSource.Platen;
                }

                if (source == ScanSource.Feeder && !status.AdfLoaded)
                {
                    SetStatus(status.AdfState != null && AdfMessages.TryGetValue(status.AdfState, out var message)
                        ? message : "급지대를 쓸 수 없는 상태입니다.", true);
                    return;
                }

                usedSource = source;
                var request = new ScanRequest
                {
                    Source = source,
                    Color = color,
                    Dpi = dpi,
                    Width = Math.Min(paper.Width, caps?.MaxWidth(source) ?? paper.Width),
                    Height = Math.Min(paper.Height, caps?.MaxHeight(source) ?? paper.Height),
                };
                var where = source == ScanSource.Feeder ? "급지대" : "유리판";
                SetStatus(backSides ? "뒷면 스캔 중…" : $"{where}에서 스캔 중…");

                currentJobClient = client;
                currentJobPath = await client.CreateJobAsync(request, ct);
                var finished = false;
                try
                {
                    while (true)
                    {
                        var jpeg = await client.NextDocumentAsync(currentJobPath, ct);
                        if (jpeg == null) { finished = true; break; }
                        var page = await Task.Run(() => PageImage.FromJpeg(jpeg, dpi, backSides ? "뒷면 스캔" : "스캔"));
                        batch.Add(page);
                        pages.Add(page);
                        MarkDirty();
                        PageList.ScrollIntoView(page);
                        SetStatus($"{batch.Count}쪽 스캔됨…");
                    }
                }
                finally
                {
                    if (!finished) await client.CancelJobAsync(currentJobPath);
                    currentJobPath = null;
                    currentJobClient = null;
                }

                if (batch.Count == 0)
                {
                    failure = "프린터가 아무 쪽도 보내지 않았습니다.";
                    // The feeder's state only explains a feeder scan; after a glass scan it would point at the wrong thing.
                    if (source == ScanSource.Feeder)
                    {
                        try
                        {
                            var after = await client.GetStatusAsync(CancellationToken.None);
                            if (after.AdfState != null && AdfMessages.TryGetValue(after.AdfState, out var why)) failure += " " + why;
                        }
                        catch { }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                canceled = true;
            }
            catch (Exception ex)
            {
                failure = ex is HttpRequestException || ex.GetBaseException() is System.Net.WebException
                    ? "프린터와 연결이 끊겼습니다."
                    : ex is TaskCanceledException ? "프린터가 응답하지 않습니다." : ex.GetBaseException().Message;
            }
            finally
            {
                EndOperation();
            }

            if (backSides)
            {
                var complete = !canceled && failure == null;
                if (complete && fronts != null && fronts.Count == batch.Count && FrontsStillInPlace(fronts))
                {
                    lastMerge = new DuplexMerge { Fronts = fronts, Backs = batch, BacksReversed = true };
                    ApplyMerge(lastMerge);
                    lastBatch = null;
                    SetStatus($"앞면 {fronts.Count}쪽과 뒷면 {batch.Count}쪽을 합쳤습니다. 순서가 어긋났으면 [양면 순서 바꾸기]를 누르세요.");
                    UpdateCommands();
                    return;
                }

                // An interrupted back-side pass can be tried again: keep the fronts as the pending batch.
                lastBatch = complete ? null : fronts;
                var kept = batch.Count > 0 ? $" 받은 뒷면 {batch.Count}쪽은 끝에 붙여 두었습니다." : "";
                if (canceled)
                    SetStatus("뒷면 스캔을 멈췄습니다." + kept);
                else if (failure != null)
                    SetStatus($"뒷면 스캔이 중간에 멈췄습니다: {failure}{kept} 종이를 정리하고 다시 [뒷면 스캔]을 누를 수 있습니다.", true);
                else if (fronts != null && fronts.Count != batch.Count)
                    SetStatus($"앞면은 {fronts.Count}쪽인데 뒷면이 {batch.Count}쪽이라 합치지 않고 끝에 붙였습니다.", true);
                else
                    SetStatus($"뒷면 {batch.Count}쪽을 끝에 붙였습니다.");
                UpdateCommands();
                return;
            }

            if (!backSides && batch.Count > 0)
            {
                // Only a complete feeder batch can later be matched with its back sides.
                lastBatch = usedSource == ScanSource.Feeder && !canceled && failure == null ? batch : null;
                lastMerge = null;
            }

            if (canceled)
                SetStatus(batch.Count > 0 ? $"스캔을 멈췄습니다. 받은 {batch.Count}쪽은 남겨 두었습니다." : "스캔을 멈췄습니다.");
            else if (failure != null)
                SetStatus(batch.Count > 0 ? $"스캔이 중간에 멈췄습니다: {failure} 받은 {batch.Count}쪽은 남겨 두었습니다." : "스캔하지 못했습니다. " + failure, true);
            else if (batch.Count > 0)
                SetStatus($"{batch.Count}쪽을 스캔했습니다. [PDF로 저장]을 누르면 파일로 저장됩니다.");
            UpdateCommands();
        }

        /// <summary>Waits up to a minute while the printer finishes something else (e.g. copying).</summary>
        async Task<ScannerStatus> WaitForIdleAsync(EsclClient client, CancellationToken ct)
        {
            var status = await client.GetStatusAsync(ct);
            var waitUntil = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (!status.IsIdle)
            {
                if (DateTime.UtcNow > waitUntil)
                {
                    SetStatus($"프린터가 다른 작업 중입니다 ({status.State}). 잠시 후 다시 해 보세요.", true);
                    return null;
                }
                SetStatus("프린터가 다른 작업을 끝내기를 기다리는 중…");
                await Task.Delay(2000, ct);
                status = await client.GetStatusAsync(ct);
            }
            return status;
        }

        bool FrontsStillInPlace(List<PageImage> fronts) => fronts.All(pages.Contains);

        void ApplyMerge(DuplexMerge merge)
        {
            var all = merge.Fronts.Concat(merge.Backs).ToList();
            var start = all.Select(p => pages.IndexOf(p)).Where(i => i >= 0).DefaultIfEmpty(pages.Count).Min();
            foreach (var p in all) pages.Remove(p);
            var ordered = Duplex.Interleave(merge.Fronts, merge.Backs, merge.BacksReversed);
            for (var i = 0; i < ordered.Count; i++) pages.Insert(Math.Min(start + i, pages.Count), ordered[i]);
            PageList.SelectedItems.Clear();
        }

        void SwapDuplex_Click(object sender, RoutedEventArgs e)
        {
            if (lastMerge == null || !lastMerge.Fronts.Concat(lastMerge.Backs).All(pages.Contains)) { lastMerge = null; UpdateCommands(); return; }
            lastMerge.BacksReversed = !lastMerge.BacksReversed;
            ApplyMerge(lastMerge);
            MarkDirty();
            SetStatus(lastMerge.BacksReversed ? "뒷면을 거꾸로 된 순서로 끼웠습니다." : "뒷면을 스캔한 순서대로 끼웠습니다.");
        }

        // ───────────────────────────── page editing ─────────────────────────────

        List<PageImage> SelectedPages() =>
            PageList.SelectedItems.Cast<PageImage>().OrderBy(p => pages.IndexOf(p)).ToList();

        void RotateLeft_Click(object sender, RoutedEventArgs e) => RotateSelected(-90);

        void RotateRight_Click(object sender, RoutedEventArgs e) => RotateSelected(90);

        void RotateSelected(int degrees)
        {
            var selected = SelectedPages();
            if (selected.Count == 0) { SetStatus("돌릴 페이지를 먼저 고르세요. (Ctrl이나 Shift를 누르고 여러 장 고를 수 있습니다)"); return; }
            foreach (var p in selected) p.Rotation += degrees;
            MarkDirty();
        }

        void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

        void DeleteSelected()
        {
            var selected = SelectedPages();
            if (selected.Count == 0 || busy) return;
            var index = pages.IndexOf(selected[0]);
            foreach (var p in selected) pages.Remove(p);
            // Deleting stray pages (e.g. a half-finished back-side pass) keeps a pending duplex merge possible.
            var touchesBatches = selected.Any(p => (lastBatch?.Contains(p) ?? false) ||
                                                   (lastMerge != null && (lastMerge.Fronts.Contains(p) || lastMerge.Backs.Contains(p))));
            if (touchesBatches) ForgetBatches();
            editVersion++;
            dirty = pages.Count > 0;
            if (pages.Count > 0) PageList.SelectedItem = pages[Math.Min(index, pages.Count - 1)];
            SetStatus($"{selected.Count}쪽을 지웠습니다.");
        }

        void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

        void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

        void MoveSelected(int direction)
        {
            var selected = SelectedPages();
            if (selected.Count == 0 || busy) return;
            if (direction < 0 && pages.IndexOf(selected[0]) == 0) return;
            if (direction > 0 && pages.IndexOf(selected[selected.Count - 1]) == pages.Count - 1) return;
            var ordered = direction < 0 ? selected : Enumerable.Reverse(selected).ToList();
            foreach (var p in ordered)
            {
                var i = pages.IndexOf(p);
                pages.Move(i, i + direction);
            }
            foreach (var p in selected) PageList.SelectedItems.Add(p);
            PageList.ScrollIntoView(selected[direction < 0 ? 0 : selected.Count - 1]);
            ForgetBatches();
            MarkDirty();
        }

        void Clear_Click(object sender, RoutedEventArgs e)
        {
            if (pages.Count == 0 || busy) return;
            if (dirty)
            {
                var answer = MessageBox.Show(this, $"저장하지 않은 {pages.Count}쪽을 모두 지울까요?", AppTitle,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return;
            }
            pages.Clear();
            ForgetBatches();
            editVersion++;
            dirty = false;
            SetStatus("페이지를 모두 지웠습니다.");
        }

        void ForgetBatches()
        {
            lastBatch = null;
            lastMerge = null;
            UpdateCommands();
        }

        void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCommands();

        void PageList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!(e.OriginalSource is DependencyObject source)) return;
            var item = ItemsControl.ContainerFromElement(PageList, source) as ListBoxItem;
            if (item?.DataContext is PageImage page)
                new PreviewWindow(pages, pages.IndexOf(page)) { Owner = this }.ShowDialog();
        }

        // ───────────────────────────── files ─────────────────────────────

        async void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            if (busy) { SetStatus("지금 하는 작업이 끝난 뒤에 다시 해 주세요."); return; }
            var dialog = new OpenFileDialog { Multiselect = true, Filter = PageImporter.OpenFileFilter, Title = "추가할 파일" };
            if (dialog.ShowDialog(this) == true) await ImportFilesAsync(dialog.FileNames);
        }

        void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        async void Window_Drop(object sender, DragEventArgs e)
        {
            if (busy || !(e.Data.GetData(DataFormats.FileDrop) is string[] files)) return;
            await ImportFilesAsync(files);
        }

        async Task ImportFilesAsync(IEnumerable<string> paths)
        {
            if (busy) { SetStatus("지금 하는 작업이 끝난 뒤에 다시 해 주세요."); return; }
            var files = paths.Where(File.Exists).ToList();
            var skipped = paths.Count() - files.Count;
            var supported = files.Where(PageImporter.IsSupported).ToList();
            skipped += files.Count - supported.Count;
            if (supported.Count == 0)
            {
                SetStatus("PDF나 그림 파일(JPG, PNG, BMP, GIF, TIFF)만 넣을 수 있습니다.", true);
                return;
            }

            BeginOperation("불러오기 멈추기");
            var ct = operationCts.Token;
            var added = 0;
            var errors = new List<string>();
            try
            {
                foreach (var path in supported)
                {
                    SetStatus($"불러오는 중: {Path.GetFileName(path)}");
                    try
                    {
                        var imported = await PageImporter.ImportAsync(path, ct);
                        foreach (var p in imported) pages.Add(p);
                        if (imported.Count > 0) MarkDirty();
                        added += imported.Count;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{Path.GetFileName(path)}: {ex.GetBaseException().Message}");
                    }
                }
            }
            finally
            {
                EndOperation();
            }

            if (added > 0)
            {
                ForgetBatches();
                PageList.ScrollIntoView(pages[pages.Count - 1]);
            }
            var message = $"{added}쪽을 추가했습니다.";
            if (skipped > 0) message += $" 지원하지 않는 파일 {skipped}개는 건너뛰었습니다.";
            SetStatus(message, errors.Count > 0 || skipped > 0);
            if (errors.Count > 0)
                MessageBox.Show(this, "다음 파일을 열지 못했습니다.\n\n" + string.Join("\n", errors), AppTitle,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

        async Task SaveAsync()
        {
            if (busy) return;
            if (pages.Count == 0) { SetStatus("저장할 페이지가 없습니다."); return; }

            try { Directory.CreateDirectory(settings.SaveFolder); }
            catch { settings.SaveFolder = AppSettings.DefaultSaveFolder; }

            var dialog = new SaveFileDialog
            {
                Title = "PDF로 저장",
                Filter = "PDF 파일|*.pdf",
                DefaultExt = ".pdf",
                AddExtension = true,
                InitialDirectory = settings.SaveFolder,
                FileName = $"scan_{DateTime.Now:yyyyMMdd_HHmmss}.pdf",
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(this) != true) return;

            var path = dialog.FileName;
            var snapshot = pages.ToList();
            var version = editVersion;
            BeginOperation(cancelLabel: null);
            saving = true;
            try
            {
                SetStatus("저장하는 중…");
                await Task.Run(() => PdfWriter.Save(path, snapshot));
            }
            catch (Exception ex)
            {
                SetStatus("저장하지 못했습니다: " + ex.GetBaseException().Message, true);
                MessageBox.Show(this, "PDF를 저장하지 못했습니다.\n\n" + ex.GetBaseException().Message, AppTitle,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally
            {
                saving = false;
                EndOperation();
            }

            // A page turned while the file was being written may not be in it: keep the document unsaved.
            dirty = editVersion != version;
            settings.SaveFolder = Path.GetDirectoryName(path);
            SaveFolderText.Text = settings.SaveFolder;
            SaveFolderText.ToolTip = settings.SaveFolder;
            SaveSettings();
            SetStatus($"저장했습니다: {path} ({snapshot.Count}쪽)");
            if (OpenAfterSaveCheck.IsChecked == true) OpenWithShell(path);
        }

        void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try { Directory.CreateDirectory(settings.SaveFolder); } catch { }
            OpenWithShell(settings.SaveFolder);
        }

        void OpenWithShell(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SetStatus($"열지 못했습니다: {ex.GetBaseException().Message}", true);
            }
        }

        // ───────────────────────────── printing ─────────────────────────────

        void Print_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            if (pages.Count == 0) { SetStatus("인쇄할 페이지가 없습니다. 스캔하거나 파일을 추가하세요."); return; }

            var dialog = new PrintDialog { UserPageRangeEnabled = false };
            try
            {
                var queueName = escl != null ? PrinterDiscovery.FindQueueForHost(escl.Host) : null;
                if (queueName != null) dialog.PrintQueue = new LocalPrintServer().GetPrintQueue(queueName);
            }
            catch
            {
                // Fall back to the default printer; the user can pick another in the dialog.
            }
            if (dialog.ShowDialog() != true) return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                SetStatus("인쇄를 보내는 중…");
                var pageSize = new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
                var printable = PrintableArea(dialog, pageSize);
                var paginator = new PagePaginator(pages.ToList(), pageSize, printable);
                dialog.PrintDocument(paginator, $"{AppTitle} ({pages.Count}쪽)");
                SetStatus($"{pages.Count}쪽을 {dialog.PrintQueue?.Name ?? "프린터"}로 보냈습니다.");
            }
            catch (Exception ex)
            {
                SetStatus("인쇄하지 못했습니다: " + ex.GetBaseException().Message, true);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        /// <summary>
        /// The printer cannot print right up to the edge. Use the largest of its margins on every
        /// side so the result is centered whatever orientation was chosen.
        /// </summary>
        static Rect PrintableArea(PrintDialog dialog, Size pageSize)
        {
            double margin = 0;
            try
            {
                var area = dialog.PrintQueue.GetPrintCapabilities(dialog.PrintTicket).PageImageableArea;
                if (area != null)
                {
                    var media = dialog.PrintTicket.PageMediaSize;
                    double mediaWidth = media?.Width ?? pageSize.Width, mediaHeight = media?.Height ?? pageSize.Height;
                    margin = new[]
                    {
                        area.OriginWidth, area.OriginHeight,
                        mediaWidth - area.OriginWidth - area.ExtentWidth,
                        mediaHeight - area.OriginHeight - area.ExtentHeight,
                    }.Where(m => m > 0 && m < 96).DefaultIfEmpty(0).Max();
                }
            }
            catch
            {
                margin = 0;
            }
            if (margin <= 0) margin = 0.17 * 96; // a typical laser printer margin
            return new Rect(margin, margin, Math.Max(1, pageSize.Width - 2 * margin), Math.Max(1, pageSize.Height - 2 * margin));
        }

        // ───────────────────────────── keyboard ─────────────────────────────

        async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (e.Key == Key.Delete && PageList.IsKeyboardFocusWithin) { DeleteSelected(); e.Handled = true; }
            else if (ctrl && e.Key == Key.S) { e.Handled = true; await SaveAsync(); }
            else if (ctrl && e.Key == Key.P) { e.Handled = true; Print_Click(sender, e); }
            else if (ctrl && e.Key == Key.O) { e.Handled = true; AddFiles_Click(sender, e); }
            else if (ctrl && e.Key == Key.A && PageList.IsKeyboardFocusWithin) { e.Handled = true; PageList.SelectAll(); }
            else if (e.Key == Key.Escape && busy && CancelButton.Visibility == Visibility.Visible) { e.Handled = true; Cancel_Click(sender, e); }
        }

        // ───────────────────────────── UI state ─────────────────────────────

        /// <param name="cancelLabel">Text of the stop button, or null when the operation cannot be stopped.</param>
        void BeginOperation(string cancelLabel)
        {
            busy = true;
            operationCts?.Dispose();
            operationCts = new CancellationTokenSource();
            CancelButton.Content = cancelLabel;
            CancelButton.IsEnabled = true;
            CancelButton.Visibility = cancelLabel != null ? Visibility.Visible : Visibility.Collapsed;
            UpdateCommands();
        }

        void EndOperation()
        {
            busy = false;
            CancelButton.Visibility = Visibility.Collapsed;
            UpdateCommands();
        }

        void UpdatePageState()
        {
            for (var i = 0; i < pages.Count; i++) pages[i].Number = i + 1;
            PageCountText.Text = pages.Count == 0 ? "페이지 없음" : $"{pages.Count}쪽";
            EmptyHint.Visibility = pages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateCommands();
        }

        void UpdateCommands()
        {
            var hasPages = pages.Count > 0;
            var hasSelection = PageList.SelectedItems.Count > 0;
            ScanButton.IsEnabled = !busy && escl != null;
            BackSideButton.Visibility = !busy && lastBatch != null && lastBatch.Count > 0 && (caps?.HasFeeder ?? true)
                ? Visibility.Visible : Visibility.Collapsed;
            SwapDuplexButton.Visibility = !busy && lastMerge != null ? Visibility.Visible : Visibility.Collapsed;
            SourceCombo.IsEnabled = ColorCombo.IsEnabled = DpiCombo.IsEnabled = PaperCombo.IsEnabled = !busy;
            AddFilesButton.IsEnabled = !busy;
            SaveButton.IsEnabled = PrintButton.IsEnabled = !busy && hasPages;
            ClearButton.IsEnabled = !busy && hasPages;
            RotateLeftButton.IsEnabled = RotateRightButton.IsEnabled = hasSelection;
            DeleteButton.IsEnabled = MoveUpButton.IsEnabled = MoveDownButton.IsEnabled = !busy && hasSelection;
        }

        void SetStatus(string text, bool warning = false)
        {
            StatusText.Text = text;
            StatusText.Foreground = warning ? (Brush)FindResource("ErrorBrush") : (Brush)FindResource("MutedBrush");
        }

        static void SelectByTag(ComboBox combo, string tag, string fallback)
        {
            var items = combo.Items.Cast<ComboBoxItem>().ToList();
            combo.SelectedItem = items.FirstOrDefault(i => (string)i.Tag == tag)
                                 ?? items.FirstOrDefault(i => (string)i.Tag == fallback)
                                 ?? items.FirstOrDefault();
        }

        static string SelectedTag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;
    }
}
