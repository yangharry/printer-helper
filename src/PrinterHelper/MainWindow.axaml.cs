using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PrinterHelper.Discovery;
using PrinterHelper.Documents;
using PrinterHelper.Escl;
using PrinterHelper.Ipp;
using PrinterHelper.Platform;
using PrinterHelper.Printing;

namespace PrinterHelper
{
    public partial class MainWindow : Window
    {
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

        // ⌘ on macOS, Ctrl elsewhere.
        static readonly KeyModifiers CommandKey = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        internal const string LocalNetworkHint =
            "macOS가 로컬 네트워크 사용을 막고 있습니다. 시스템 설정 > 개인정보 보호 및 보안 > 로컬 네트워크에서 '프린터 도우미'를 켜 주세요.";

        readonly ObservableCollection<PageImage> pages = new ObservableCollection<PageImage>();
        readonly HttpClient statusHttp = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(5),
            MaxResponseContentBufferSize = 4 * 1024 * 1024,
        };
        readonly DispatcherTimer statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        readonly IReadOnlyList<string> startupFiles;
        readonly bool offline;

        AppSettings settings;
        EsclClient escl;
        ScannerCapabilities caps;
        ScannerStatus lastStatus;
        DateTime lastPrinterInfoAt = DateTime.MinValue;
        bool statusBusy;
        bool busy;
        bool saving;
        bool printing;
        bool dirty;
        bool closeConfirmed;
        bool closePromptOpen;
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

        // For the XAML previewer.
        public MainWindow() : this(Array.Empty<string>(), offline: true) { }

        /// <param name="files">Files to open at start (command line, or dropped on the shortcut).</param>
        /// <param name="offline">Do not look for a printer (smoke test).</param>
        public MainWindow(IReadOnlyList<string> files, bool offline)
        {
            startupFiles = files;
            this.offline = offline;
            InitializeComponent();
            // Version in the title bar, so it is easy to tell which build is running.
            var version = typeof(MainWindow).Assembly.GetName().Version;
            Title = $"{App.Title} {version.Major}.{version.Minor}.{version.Build}";
            if (OperatingSystem.IsWindows()) FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
            if (OperatingSystem.IsMacOS())
                EmptyHintText.Text = "급지대에 종이를 넣고 [스캔]을 누르면 여러 장이 차례로 들어옵니다.\nPDF나 사진 파일을 이 창으로 끌어 놓아도 됩니다.";

            PageList.ItemsSource = pages;
            pages.CollectionChanged += (s, e) => UpdatePageState();
            statusTimer.Tick += async (s, e) => await RefreshStatusAsync();
            AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
            AddHandler(DragDrop.DragOverEvent, Window_DragOver);
            AddHandler(DragDrop.DropEvent, Window_Drop);
        }

        internal IReadOnlyList<PageImage> Pages => pages;

        // ───────────────────────────── startup / shutdown ─────────────────────────────

        protected override async void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            try
            {
                settings = AppSettings.Load();
                if (string.IsNullOrWhiteSpace(settings.SaveFolder)) settings.SaveFolder = AppSettings.DefaultSaveFolder;

                SelectByTag(SourceCombo, settings.Source, "Auto");
                SelectByTag(ColorCombo, settings.Color ? "Color" : "Gray", "Gray");
                SelectByTag(PaperCombo, settings.PaperSize, "A4");
                FillResolutions(new[] { 100, 200, 300 });
                OpenAfterSaveCheck.IsChecked = settings.OpenAfterSave;
                ShowSaveFolder();
                UpdatePageState();

                // Files dropped on the desktop shortcut (or passed on the command line) are opened right away.
                var connecting = offline ? Task.CompletedTask : ConnectAsync(settings.PrinterHost);
                var files = startupFiles.Where(File.Exists).ToList();
                if (files.Count > 0) await ImportFilesAsync(files);
                await connecting;
                if (!offline) statusTimer.Start();
            }
            catch (Exception ex)
            {
                SetStatus("시작하는 중에 문제가 생겼습니다: " + Describe(ex), true);
            }
        }

        protected override async void OnClosing(WindowClosingEventArgs e)
        {
            if (!closeConfirmed)
            {
                // ⌘Q or the Dock's Quit while the question is already up: one question is enough.
                if (closePromptOpen) { e.Cancel = true; return; }
                if (saving || printing)
                {
                    e.Cancel = true;
                    SetStatus(saving ? "PDF를 저장하는 중입니다. 저장이 끝난 뒤에 닫아 주세요." : "인쇄를 보내는 중입니다. 끝난 뒤에 닫아 주세요.", true);
                    return;
                }
                if (dirty && pages.Count > 0)
                {
                    // Avalonia cannot wait for an answer here: cancel, ask, then close again.
                    e.Cancel = true;
                    var question = currentJobPath != null
                        ? $"스캔하는 중입니다. 지금 닫으면 스캔을 멈추고, 저장하지 않은 {pages.Count}쪽도 사라집니다.\n그래도 닫을까요?"
                        : $"저장하지 않은 페이지가 {pages.Count}쪽 있습니다.\n그래도 닫을까요?";
                    closePromptOpen = true;
                    bool yes;
                    try { yes = await MessageDialog.AskAsync(this, question); }
                    finally { closePromptOpen = false; }
                    if (!yes) return;
                    closeConfirmed = true;
                    Close();
                    return;
                }
            }

            statusTimer.Stop();
            operationCts?.Cancel();
            // Closing mid-scan: make sure the printer stops feeding paper.
            var job = currentJobPath;
            var client = currentJobClient;
            if (job != null && client != null)
                Task.Run(() => client.CancelJobAsync(job)).Wait(TimeSpan.FromSeconds(5));
            SaveSettings();
            base.OnClosing(e);
        }

        void MarkDirty()
        {
            dirty = true;
            editVersion++;
        }

        void SaveSettings()
        {
            if (settings == null || offline) return;
            settings.Source = SelectedTag(SourceCombo) ?? "Auto";
            settings.Color = SelectedTag(ColorCombo) == "Color";
            settings.PaperSize = SelectedTag(PaperCombo) ?? "A4";
            if (DpiCombo.SelectedItem is ComboBoxItem dpiItem && dpiItem.Tag is int dpi) settings.Dpi = dpi;
            settings.OpenAfterSave = OpenAfterSaveCheck.IsChecked == true;
            settings.Save();
        }

        void ShowSaveFolder()
        {
            SaveFolderText.Text = settings.SaveFolder;
            ToolTip.SetTip(SaveFolderText, settings.SaveFolder);
        }

        // ───────────────────────────── printer connection ─────────────────────────────

        async Task ConnectAsync(string preferredHost)
        {
            var generation = ++connectGeneration;
            SetPrinterHeader("프린터 찾는 중…", "", Brush("MutedBrush"));
            escl?.Dispose();
            escl = null;
            caps = null;
            UpdateCommands();

            string host = null;
            string model = null;
            var blocked = false;
            List<FoundScanner> others = null;
            // macOS asks for permission to use the local network the first time; until the user answers, requests
            // fail. Keep trying for a minute so the app connects as soon as it is allowed.
            var attempts = OperatingSystem.IsMacOS() ? 12 : 1;
            // With a printer remembered, keep trying it even when other scanners answer; otherwise stop once any do.
            for (var attempt = 0; attempt < attempts && host == null &&
                                  (others == null || others.Count == 0 || !string.IsNullOrWhiteSpace(preferredHost)); attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(5000);
                    if (generation != connectGeneration) return;
                }
                if (!string.IsNullOrWhiteSpace(preferredHost))
                {
                    model = await PrinterDiscovery.ProbeAsync(preferredHost, CancellationToken.None);
                    if (generation != connectGeneration) return;
                    if (model != null) host = preferredHost;
                }
                if (host == null)
                {
                    var found = await PrinterDiscovery.DiscoverAsync(CancellationToken.None);
                    if (generation != connectGeneration) return;
                    blocked = found.NetworkSearchBlocked && OperatingSystem.IsMacOS();
                    var pick = PickScanner(found.Scanners, preferredHost);
                    if (pick != null) { host = pick.Host; model = pick.Model; }
                    others = found.Scanners;
                    if (blocked) SetStatus(LocalNetworkHint, true);
                    else if (host == null && others.Count == 0 && attempt == 0 && attempts > 1)
                        SetStatus("프린터를 찾는 중입니다… 프린터가 절전 중이면 깨어나는 데 잠시 걸립니다.");
                }
            }

            if (host == null)
            {
                PrinterStateText.Text = "연결 안 됨";
                if (blocked)
                {
                    SetPrinterHeader("로컬 네트워크 허용이 필요합니다", preferredHost ?? "", Brush("ErrorBrush"));
                    SetStatus(LocalNetworkHint, true);
                }
                else if (others != null && others.Count > 0)
                {
                    // Never switch to a scanner that is merely on the network: it may be the neighbour's.
                    SetPrinterHeader("스캐너를 골라 주세요", preferredHost ?? "", Brush("WarnBrush"));
                    SetStatus(string.IsNullOrWhiteSpace(preferredHost)
                        ? $"네트워크에서 스캐너를 {others.Count}대 찾았습니다. 위의 [프린터 변경]에서 쓸 스캐너를 고르세요."
                        : $"전에 쓰던 프린터({preferredHost})가 응답하지 않습니다. 프린터가 켜져 있는지 확인하거나 위의 [프린터 변경]에서 고르세요.", true);
                }
                else
                {
                    SetPrinterHeader("스캐너를 찾지 못했습니다", preferredHost ?? "", Brush("ErrorBrush"));
                    SetStatus("프린터가 켜져 있는지 확인하고, 위의 [프린터 변경]에서 IP 주소를 직접 넣어 보세요.", true);
                }
                UpdateCommands();
                return;
            }

            var client = new EsclClient(host);
            ScannerCapabilities foundCaps;
            try
            {
                foundCaps = await client.GetCapabilitiesAsync(CancellationToken.None);
            }
            catch
            {
                foundCaps = new ScannerCapabilities { MakeAndModel = model, HasFeeder = true, HasPlaten = true };
            }
            if (generation != connectGeneration)
            {
                client.Dispose();
                return;
            }
            escl = client;
            caps = foundCaps;
            settings.PrinterHost = host;
            SetPrinterHeader(caps.MakeAndModel ?? model ?? "스캐너", host, Brush("OkBrush"));
            FillResolutions(caps.Resolutions);
            if (!caps.HasFeeder) SelectByTag(SourceCombo, "Platen", "Platen");
            if (StatusText.Text == LocalNetworkHint || StatusText.Text.StartsWith("프린터를 찾는 중") ||
                StatusText.Text.StartsWith("네트워크에서 스캐너를") || StatusText.Text.StartsWith("전에 쓰던 프린터"))
                SetStatus("급지대에 종이를 넣고 [스캔]을 누르세요.");
            lastPrinterInfoAt = DateTime.MinValue;
            UpdateCommands();
            await RefreshStatusAsync();
        }

        /// <summary>
        /// The scanner to connect to without asking: a printer installed on this computer, or, on a first start with
        /// nothing remembered, the only scanner on the network. Otherwise null and the user picks one.
        /// </summary>
        static FoundScanner PickScanner(IReadOnlyList<FoundScanner> found, string preferredHost)
        {
            // The remembered printer itself, heard over the network after its direct probe failed (asleep, or macOS
            // granted network access only just now).
            if (!string.IsNullOrWhiteSpace(preferredHost))
            {
                var saved = PrinterDiscovery.InstalledHost(preferredHost);
                var same = found.FirstOrDefault(s => string.Equals(PrinterDiscovery.InstalledHost(s.Host), saved, StringComparison.OrdinalIgnoreCase));
                if (same != null) return same;
            }
            var installed = found.FirstOrDefault(s => s.QueueName != null);
            if (installed != null) return installed;
            return string.IsNullOrWhiteSpace(preferredHost) && found.Count == 1 ? found[0] : null;
        }

        async void ChangePrinter_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            var picker = new PrinterPickerWindow(escl?.Host ?? settings?.PrinterHost);
            if (!await picker.ShowDialog<bool>(this)) return;
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
                    StateDot.Fill = Brush("ErrorBrush");
                    PrinterStateText.Text = "연결 안 됨";
                    FeederText.Text = "급지대 -";
                    return;
                }
                if (client != escl) return;
                lastStatus = status;
                StateDot.Fill = Brush("OkBrush");
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
            PrinterStateText.Foreground = problems.Count > 0 ? Brush("WarnBrush") : Brush("TextBrush");

            if (info.TonerLevel.HasValue)
            {
                TonerText.Text = $"토너 {info.TonerLevel}%";
                var low = info.TonerLowLevel ?? 10;
                TonerText.Foreground = info.TonerLevel <= low ? Brush("WarnBrush") : Brush("TextBrush");
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

        void SetPrinterHeader(string name, string host, IBrush dot)
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
                    if (!await MessageDialog.AskAsync(this, "급지대에 종이가 없습니다.\n유리판에 올린 1장을 스캔할까요?"))
                    {
                        SetStatus("급지대에 종이를 넣고 다시 [스캔]을 누르세요.");
                        return;
                    }
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
                        ScrollIntoView(page);
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
                failure = ex is HttpRequestException
                    ? "프린터와 연결이 끊겼습니다."
                    : ex is TaskCanceledException ? "프린터가 응답하지 않습니다." : Describe(ex);
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
            if (selected.Count == 0)
            {
                SetStatus(OperatingSystem.IsMacOS()
                    ? "돌릴 페이지를 먼저 고르세요. (⌘나 Shift를 누르고 여러 장 고를 수 있습니다)"
                    : "돌릴 페이지를 먼저 고르세요. (Ctrl이나 Shift를 누르고 여러 장 고를 수 있습니다)");
                return;
            }
            foreach (var p in selected) p.Rotation += degrees;
            MarkDirty();
        }

        void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

        void DeleteSelected()
        {
            var selected = SelectedPages();
            if (selected.Count == 0 || busy) return;
            var index = pages.IndexOf(selected[0]);
            // The focused page goes away with it; Avalonia then drops the focus instead of keeping it in the list.
            var hadFocus = PageList.IsKeyboardFocusWithin;
            foreach (var p in selected) pages.Remove(p);
            // Deleting stray pages (e.g. a half-finished back-side pass) keeps a pending duplex merge possible.
            var touchesBatches = selected.Any(p => (lastBatch?.Contains(p) ?? false) ||
                                                   (lastMerge != null && (lastMerge.Fronts.Contains(p) || lastMerge.Backs.Contains(p))));
            if (touchesBatches) ForgetBatches();
            editVersion++;
            dirty = pages.Count > 0;
            if (pages.Count > 0)
            {
                var next = pages[Math.Min(index, pages.Count - 1)];
                PageList.SelectedItem = next;
                if (hadFocus) (PageList.ContainerFromItem(next) as Control ?? PageList).Focus(NavigationMethod.Directional);
            }
            else if (hadFocus)
            {
                PageList.Focus();
            }
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
            PageList.SelectedItems.Clear();
            foreach (var p in selected) PageList.SelectedItems.Add(p);
            ScrollIntoView(selected[direction < 0 ? 0 : selected.Count - 1]);
            ForgetBatches();
            MarkDirty();
        }

        async void Clear_Click(object sender, RoutedEventArgs e)
        {
            if (pages.Count == 0 || busy) return;
            if (dirty && !await MessageDialog.AskAsync(this, $"저장하지 않은 {pages.Count}쪽을 모두 지울까요?")) return;
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

        async void PageList_DoubleTapped(object sender, TappedEventArgs e)
        {
            var item = (e.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
            if (item?.DataContext is PageImage page)
                await new PreviewWindow(pages, pages.IndexOf(page)).ShowDialog(this);
        }

        void ScrollIntoView(PageImage page)
        {
            // New items have no position until the next layout pass.
            Dispatcher.UIThread.Post(() => PageList.ScrollIntoView(page), DispatcherPriority.Background);
        }

        // ───────────────────────────── files ─────────────────────────────

        async void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            if (busy) { SetStatus("지금 하는 작업이 끝난 뒤에 다시 해 주세요."); return; }
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "추가할 파일",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("PDF와 그림 파일")
                    {
                        Patterns = PageImporter.Patterns,
                        // Only what the importer reads: "public.image" would also offer HEIC photos it cannot open.
                        AppleUniformTypeIdentifiers = new[] { "com.adobe.pdf", "public.jpeg", "public.png", "com.microsoft.bmp", "com.compuserve.gif", "public.tiff", "org.webmproject.webp" },
                        MimeTypes = new[] { "application/pdf", "image/jpeg", "image/png", "image/bmp", "image/gif", "image/tiff", "image/webp" },
                    },
                    FilePickerFileTypes.All,
                },
            });
            var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p != null).ToList();
            if (paths.Count > 0) await ImportFilesAsync(paths);
        }

        void Window_DragOver(object sender, DragEventArgs e)
        {
            e.DragEffects = !busy && e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        async void Window_Drop(object sender, DragEventArgs e)
        {
            if (busy) return;
            var items = e.DataTransfer.TryGetValues(DataFormat.File);
            if (items == null) return;
            var paths = items.Select(i => i.TryGetLocalPath()).ToList();
            if (paths.Count > 0 && paths.All(p => p == null))
            {
                // Photos and Mail hand over "promised" files that are not on disk yet.
                SetStatus("끌어 놓은 항목을 파일로 읽지 못했습니다. 먼저 파일로 저장한 뒤 다시 끌어 놓아 주세요.", true);
                return;
            }
            await ImportFilesAsync(paths.Where(p => p != null).ToList());
        }

        /// <summary>Files opened from outside while the app runs (macOS "Open With", the Dock icon).</summary>
        internal async void OpenFiles(IReadOnlyList<string> paths)
        {
            if (paths.Count > 0) await ImportFilesAsync(paths);
        }

        internal async Task ImportFilesAsync(IReadOnlyList<string> paths)
        {
            if (busy) { SetStatus("지금 하는 작업이 끝난 뒤에 다시 해 주세요."); return; }
            var files = paths.Where(File.Exists).ToList();
            var skipped = paths.Count - files.Count;
            var supported = files.Where(PageImporter.IsSupported).ToList();
            skipped += files.Count - supported.Count;
            if (supported.Count == 0)
            {
                SetStatus("PDF나 그림 파일(JPG, PNG, BMP, GIF, TIFF, WEBP)만 넣을 수 있습니다.", true);
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
                    SetStatus($"불러오는 중: {Path.GetFileName(path).Normalize()}");
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
                        errors.Add($"{Path.GetFileName(path).Normalize()}: {Describe(ex)}");
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
                ScrollIntoView(pages[pages.Count - 1]);
            }
            var message = $"{added}쪽을 추가했습니다.";
            if (skipped > 0) message += $" 지원하지 않는 파일 {skipped}개는 건너뛰었습니다.";
            SetStatus(message, errors.Count > 0 || skipped > 0);
            if (errors.Count > 0)
                await MessageDialog.ShowAsync(this, "다음 파일을 열지 못했습니다.\n\n" + string.Join("\n", errors));
        }

        async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

        async Task SaveAsync()
        {
            if (busy) return;
            if (pages.Count == 0) { SetStatus("저장할 페이지가 없습니다."); return; }

            try { Directory.CreateDirectory(settings.SaveFolder); }
            catch { settings.SaveFolder = AppSettings.DefaultSaveFolder; }

            IStorageFolder startFolder = null;
            try { startFolder = await StorageProvider.TryGetFolderFromPathAsync(settings.SaveFolder); }
            catch { }
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "PDF로 저장",
                SuggestedFileName = $"scan_{DateTime.Now:yyyyMMdd_HHmmss}.pdf",
                DefaultExtension = "pdf",
                FileTypeChoices = new[] { FilePickerFileTypes.Pdf },
                SuggestedStartLocation = startFolder,
                ShowOverwritePrompt = true,
            });
            var path = file?.TryGetLocalPath();
            if (path == null) return;
            if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) path += ".pdf";

            var snapshot = pages.ToList();
            var version = editVersion;
            string error = null;
            BeginOperation(cancelLabel: null);
            saving = true;
            try
            {
                SetStatus("저장하는 중…");
                await Task.Run(() => PdfWriter.Save(path, snapshot));
            }
            catch (Exception ex)
            {
                error = ex.GetBaseException() is UnauthorizedAccessException && OperatingSystem.IsMacOS()
                    ? "이 폴더에 저장할 권한이 없습니다. 시스템 설정 > 개인정보 보호 및 보안 > 파일 및 폴더에서 '프린터 도우미'를 허용하거나 다른 폴더를 고르세요."
                    : Describe(ex);
            }
            finally
            {
                saving = false;
                EndOperation();
            }
            if (error != null)
            {
                SetStatus("저장하지 못했습니다: " + error, true);
                await MessageDialog.ShowAsync(this, "PDF를 저장하지 못했습니다.\n\n" + error);
                return;
            }

            // A page turned while the file was being written may not be in it: keep the document unsaved.
            dirty = editVersion != version;
            settings.SaveFolder = Path.GetDirectoryName(path);
            ShowSaveFolder();
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
                Shell.Open(path);
            }
            catch (Exception ex)
            {
                SetStatus($"열지 못했습니다: {ex.GetBaseException().Message}", true);
            }
        }

        // ───────────────────────────── printing ─────────────────────────────

        async void Print_Click(object sender, RoutedEventArgs e) => await PrintAsync();

        async Task PrintAsync()
        {
            if (busy) return;
            if (pages.Count == 0) { SetStatus("인쇄할 페이지가 없습니다. 스캔하거나 파일을 추가하세요."); return; }
            var snapshot = pages.ToList();
            var documentName = $"{App.Title} ({snapshot.Count}쪽)";
            // From the first click until the job is handed over: no second print, no scan, no closing.
            BeginOperation(cancelLabel: null);
            printing = true;
            try
            {
                var sentTo = OperatingSystem.IsWindows()
                    ? await PrintOnWindowsAsync(snapshot, documentName)
                    : await PrintWithCupsAsync(snapshot, documentName);
                if (sentTo != null) SetStatus($"{snapshot.Count}쪽을 {sentTo}(으)로 보냈습니다.");
            }
            catch (Exception ex)
            {
                SetStatus("인쇄하지 못했습니다: " + Describe(ex), true);
            }
            finally
            {
                printing = false;
                Cursor = null;
                EndOperation();
            }
        }

        /// <returns>The printer's name, or null when the user canceled.</returns>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        async Task<string> PrintOnWindowsAsync(List<PageImage> snapshot, string documentName)
        {
            var host = escl?.Host;
            string queue = null;
            try { queue = host != null ? await Task.Run(() => WindowsPrinting.FindQueueForHost(host)) : null; }
            catch { /* fall back to the default printer; the user can pick another in the dialog */ }
            var printer = WindowsPrinting.ShowPrintDialog(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, queue);
            if (printer == null) return null;
            Cursor = new Cursor(StandardCursorType.Wait);
            SetStatus("인쇄를 보내는 중…");
            await Task.Run(() => WindowsPrinting.Print(printer, snapshot, documentName));
            return printer.PrinterName;
        }

        /// <returns>The printer's name, or null when nothing was printed.</returns>
        async Task<string> PrintWithCupsAsync(List<PageImage> snapshot, string documentName)
        {
            SetStatus("프린터 목록을 읽는 중…");
            var queues = await Task.Run(() => CupsPrinting.ListQueues());
            if (queues.Count == 0)
            {
                SetStatus("인쇄할 프린터를 찾지 못했습니다.", true);
                await MessageDialog.ShowAsync(this, "설치된 프린터가 없거나 프린터 목록을 읽지 못했습니다.\n시스템 설정 > 프린터 및 스캐너에서 프린터를 확인한 뒤 다시 해 주세요.");
                return null;
            }
            var preferred = queues.FirstOrDefault(q => q.Name == settings.PrintQueue)
                            ?? CupsPrinting.FindQueueForScanner(queues, escl?.Host, caps?.MakeAndModel);
            var sheet = Enum.TryParse<SheetSize>(SelectedTag(PaperCombo), out var s) ? s : SheetSize.A4;
            var window = new PrintWindow(queues, preferred, sheet, snapshot.Count);
            if (!await window.ShowDialog<bool>(this) || window.Queue == null)
            {
                SetStatus("인쇄를 취소했습니다.");
                return null;
            }
            settings.PrintQueue = window.Queue.Name;
            Cursor = new Cursor(StandardCursorType.Wait);
            SetStatus("인쇄를 보내는 중…");
            await CupsPrinting.PrintAsync(window.Queue.Name, window.Copies, window.Sheet, snapshot, documentName, CancellationToken.None);
            return window.Queue.ToString();
        }

        /// <summary>
        /// What to tell the user about an error: our own messages are Korean InvalidOperationExceptions (which may
        /// wrap the technical cause); for anything else the innermost message.
        /// </summary>
        static string Describe(Exception ex)
        {
            while ((ex is AggregateException || ex is System.Reflection.TargetInvocationException) && ex.InnerException != null)
                ex = ex.InnerException;
            return ex is InvalidOperationException ? ex.Message : ex.GetBaseException().Message;
        }

        // ───────────────────────────── keyboard ─────────────────────────────

        async void Window_KeyDown(object sender, KeyEventArgs e)
        {
            var command = e.KeyModifiers.HasFlag(CommandKey);
            var inPages = PageList.IsKeyboardFocusWithin;
            // The Mac "delete" key sends Back; Delete is fn+delete.
            if ((e.Key == Key.Delete || OperatingSystem.IsMacOS() && e.Key == Key.Back) && inPages && !command) { e.Handled = true; DeleteSelected(); }
            else if (command && e.Key == Key.S) { e.Handled = true; await SaveAsync(); }
            else if (command && e.Key == Key.P) { e.Handled = true; await PrintAsync(); }
            else if (command && e.Key == Key.O) { e.Handled = true; AddFiles_Click(sender, e); }
            else if (command && e.Key == Key.A && inPages) { e.Handled = true; PageList.SelectAll(); }
            else if (command && e.Key == Key.W && OperatingSystem.IsMacOS()) { e.Handled = true; Close(); }
            else if (e.Key == Key.Escape && busy && CancelButton.IsVisible) { e.Handled = true; Cancel_Click(sender, e); }
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
            CancelButton.IsVisible = cancelLabel != null;
            UpdateCommands();
        }

        void EndOperation()
        {
            busy = false;
            CancelButton.IsVisible = false;
            UpdateCommands();
        }

        void UpdatePageState()
        {
            for (var i = 0; i < pages.Count; i++) pages[i].Number = i + 1;
            PageCountText.Text = pages.Count == 0 ? "페이지 없음" : $"{pages.Count}쪽";
            EmptyHint.IsVisible = pages.Count == 0;
            UpdateCommands();
        }

        void UpdateCommands()
        {
            var hasPages = pages.Count > 0;
            var hasSelection = PageList.SelectedItems?.Count > 0;
            ScanButton.IsEnabled = !busy && escl != null;
            BackSideButton.IsVisible = !busy && lastBatch != null && lastBatch.Count > 0 && (caps?.HasFeeder ?? true);
            SwapDuplexButton.IsVisible = !busy && lastMerge != null;
            SourceCombo.IsEnabled = ColorCombo.IsEnabled = DpiCombo.IsEnabled = PaperCombo.IsEnabled = !busy;
            AddFilesButton.IsEnabled = !busy;
            SaveButton.IsEnabled = PrintButton.IsEnabled = !busy && hasPages;
            ClearButton.IsEnabled = !busy && hasPages;
            RotateLeftButton.IsEnabled = RotateRightButton.IsEnabled = hasSelection;
            DeleteButton.IsEnabled = MoveUpButton.IsEnabled = MoveDownButton.IsEnabled = !busy && hasSelection;
        }

        internal void SetStatus(string text, bool warning = false)
        {
            StatusText.Text = text;
            StatusText.Foreground = warning ? Brush("ErrorBrush") : Brush("MutedBrush");
        }

        IBrush Brush(string key) => this.FindResource(key) as IBrush;

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
