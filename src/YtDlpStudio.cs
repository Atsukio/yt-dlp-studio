using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("YT DLP Studio")]
[assembly: System.Reflection.AssemblyProduct("YT DLP Studio")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.0.0")]
// Opts in to .NET 4.8 behaviour, including OS-default TLS for downloading thumbnails.
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace YtDlpStudio
{
    static class AppInfo
    {
        public const string Name = "YT DLP Studio";
        public const string FileName = "yt-dlp-studio"; // settings, logs and the per-user tools folder
    }

    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Notify(string p) { if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(p)); }
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Notify(name);
            return true;
        }
    }

    public class Cmd : ICommand
    {
        readonly Action action;
        public Cmd(Action action) { this.action = action; }
        public bool CanExecute(object p) { return true; }
        public void Execute(object p) { action(); }
        public event EventHandler CanExecuteChanged { add { } remove { } }
    }

    // One option parsed from `yt-dlp --help`, bound to a row of the options grid.
    public class OptionRow : Observable
    {
        public string Name { get; set; }        // long form, e.g. --format
        public string Aliases { get; set; }     // short forms, e.g. -f
        public string Metavar { get; set; }     // argument placeholder, null for plain flags
        public string Section { get; set; }
        public string Description { get; set; }

        public bool TakesValue { get { return Metavar != null; } }
        // Options like --replace-in-metadata FIELDS REGEX REPLACE take several arguments;
        // their value is passed through as raw command-line text.
        public bool MultiArg { get { return Metavar != null && Metavar.Contains(" "); } }
        public string MetavarText { get { return Metavar == null ? "" : " " + Metavar; } }

        bool enabled;
        string value = "";

        public bool Enabled
        {
            get { return enabled; }
            set { if (Set(ref enabled, value) && EnabledChanged != null) EnabledChanged(this, EventArgs.Empty); }
        }

        public string Value
        {
            get { return value; }
            set
            {
                // Typing a value switches the option on; clearing it switches it off.
                if (Set(ref this.value, value ?? "")) Enabled = this.value.Trim().Length > 0;
            }
        }

        public event EventHandler EnabledChanged;
    }

    // An entry in a drop-down: a quality preset, or one of a video's actual formats.
    public class Choice
    {
        public string Label, Value;   // Value: preset value or yt-dlp format id; null means "automatic"
        public double Size;           // bytes, 0 when unknown
        public double Rank, Rank2;    // for sorting formats: height and fps, or audio bitrate
        public bool HasVideo, HasAudio;
        public Choice(string label, string value) { Label = label; Value = value; }
        public override string ToString() { return Label; }
    }

    public class SectionItem : Observable
    {
        public string Key { get; set; }   // section name from the help text, or a special key
        public string Name { get; set; }  // display name
        int count;
        public int Count { get { return count; } set { Set(ref count, value); } }
    }

    // One video on the Downloads page. A playlist link produces one item per video.
    public class DownloadItem : Observable
    {
        internal Job Job;
        internal bool HasMeta;

        string title, uploader = "", duration = "", formatLabel, statusText = "", speed = "", sizeText = "", etaText = "", file;
        string state = "queued"; // queued, fetching, downloading, converting, done, failed, cancelled
        double percent;
        ImageSource thumb;

        public string Title { get { return title; } set { Set(ref title, value); } }
        public string Uploader { get { return uploader; } set { if (Set(ref uploader, value)) Notify("HasUploader"); } }
        public string Duration { get { return duration; } set { if (Set(ref duration, value)) Notify("HasDuration"); } }
        public string FormatLabel { get { return formatLabel; } set { Set(ref formatLabel, value); } }
        public string StatusText { get { return statusText; } set { Set(ref statusText, value); } }
        public string Speed { get { return speed; } set { Set(ref speed, value); } }
        public string SizeText { get { return sizeText; } set { Set(ref sizeText, value); } }
        public string EtaText { get { return etaText; } set { Set(ref etaText, value); } }
        public ImageSource Thumb { get { return thumb; } set { Set(ref thumb, value); } }
        public string File { get { return file; } set { if (Set(ref file, value)) Notify("HasFile"); } }

        public double Percent
        {
            get { return percent; }
            set { if (Set(ref percent, value)) { Notify("PercentText"); Notify("IsIndeterminate"); } }
        }
        public string PercentText { get { return percent.ToString("0.0", CultureInfo.InvariantCulture) + "%"; } }

        public string State
        {
            get { return state; }
            set
            {
                if (!Set(ref state, value)) return;
                foreach (string p in new[] { "ShowProgress", "ShowStats", "ShowStatus", "IsIndeterminate", "CanCancel", "CanRetry", "IsFinished" })
                    Notify(p);
            }
        }

        public bool HasUploader { get { return !string.IsNullOrEmpty(uploader); } }
        public bool HasDuration { get { return !string.IsNullOrEmpty(duration); } }
        public bool HasFile { get { return file != null && State == "done"; } }
        public bool ShowProgress { get { return state == "fetching" || state == "downloading" || state == "converting"; } }
        public bool ShowStats { get { return state == "downloading"; } }
        public bool ShowStatus { get { return !ShowStats; } }
        public bool IsIndeterminate { get { return state == "fetching" || state == "converting" || (state == "downloading" && percent <= 0); } }
        public bool CanCancel { get { return state == "queued" || ShowProgress; } }
        public bool CanRetry { get { return state == "failed" || state == "cancelled"; } }
        public bool IsFinished { get { return state == "done" || state == "failed" || state == "cancelled"; } }

        public ICommand CancelCommand { get; set; }
        public ICommand RemoveCommand { get; set; }
        public ICommand OpenCommand { get; set; }
        public ICommand RetryCommand { get; set; }

        internal void NotifyFile() { Notify("HasFile"); }
    }

    // One yt-dlp run for one link.
    class Job
    {
        public string Url, Args, FormatKey, FormatLabel;
        public Process Proc;
        public readonly List<DownloadItem> Items = new List<DownloadItem>();
        public DownloadItem Current;
        public string LastError;
        public bool Cancelled;
    }

    class Controller
    {
        static readonly Regex SectionRegex = new Regex(@"^  (\S.*):\s*$");
        static readonly Regex SpecRegex = new Regex(@"^((?:-[^\s,]+, )*)(--?[^\s,]+)(?: (.+))?$");
        // Handled by the app itself, so not shown as options.
        static readonly HashSet<string> Hidden = new HashSet<string> { "--help", "--version", "--update" };
        const string AllKey = "*all", EnabledKey = "*enabled";

        // Format presets: radio button name, settings key, card label. Their arguments come from FormatArgs().
        static readonly string[][] Formats =
        {
            new[] { "FmtMp3", "mp3", "MP3" },
            new[] { "FmtM4a", "m4a", "M4A" },
            new[] { "FmtFlac", "flac", "FLAC" },
            new[] { "FmtWav", "wav", "WAV" },
            new[] { "FmtVideo", "video", "MP4" },
            new[] { "FmtCustom", "custom", "CUSTOM" },
        };

        // Audio quality for lossy formats: --audio-quality value ("0" is the best VBR setting).
        static readonly Choice[] AudioQualities =
        {
            new Choice("Best (VBR, ~245 kbps)", "0"), new Choice("320 kbps", "320K"), new Choice("256 kbps", "256K"),
            new Choice("192 kbps", "192K"), new Choice("128 kbps", "128K"), new Choice("96 kbps", "96K"),
        };
        // Video quality: "" keeps the H.264-first sort that plays everywhere, "best" lets yt-dlp pick
        // the best stream in any codec, and a number caps the height.
        static readonly Choice[] VideoQualities =
        {
            new Choice("Best compatible (H.264, up to 1080p)", ""), new Choice("Best available (any codec)", "best"),
            new Choice("Up to 2160p (4K)", "2160"), new Choice("Up to 1440p", "1440"), new Choice("Up to 1080p", "1080"),
            new Choice("Up to 720p", "720"), new Choice("Up to 480p", "480"), new Choice("Up to 360p", "360"),
        };

        // Machine-readable output, so each video gets its own card with live progress.
        static readonly string MachineArgs =
            " --newline --no-simulate --progress" +
            " --progress-template \"download:__PROG__%(progress._percent_str)s\t%(progress._speed_str)s\t%(progress._eta_str)s\t%(progress._downloaded_bytes_str)s\t%(progress._total_bytes_str)s\t%(progress._total_bytes_estimate_str)s\"" +
            " --print \"before_dl:__META__%(title)s\t%(uploader|)s\t%(thumbnail|)s\t%(duration_string|)s\t%(id)s\t%(extractor_key)s\"" +
            " --print \"post_process:__STAGE__\"" +
            " --print \"after_move:__FILE__%(filepath)s\"";

        readonly string appDir = AppDomain.CurrentDomain.BaseDirectory;
        readonly string settingsPath, helpCachePath;
        readonly Window w;

        TextBox urlBox, folderBox, logBox, searchBox, extraBox;
        CheckBox embedCheck;
        Button updateButton;
        TextBlock versionText, countText, descTitle, descMeta, descBody, optBadgeText, dlBadgeText, activeCount, doneCount, formatHint, logToggleText;
        Border optBadge, dlBadge, activeEmpty, logPanel;
        FrameworkElement doneEmpty;
        RadioButton navDownloads;
        DataGrid grid;
        ListBox sectionList;
        readonly Dictionary<string, RadioButton> formatButtons = new Dictionary<string, RadioButton>();

        ObservableCollection<OptionRow> rows = new ObservableCollection<OptionRow>();
        readonly ObservableCollection<SectionItem> sections = new ObservableCollection<SectionItem>();
        SectionItem enabledSection;
        // name -> {enabled "1"/"0", value}; survives option-list reloads and app restarts.
        readonly Dictionary<string, string[]> optionState = new Dictionary<string, string[]>();

        readonly ObservableCollection<DownloadItem> active = new ObservableCollection<DownloadItem>();
        readonly ObservableCollection<DownloadItem> done = new ObservableCollection<DownloadItem>();
        readonly List<Job> pending = new List<Job>();
        Job running;
        Process updater;

        // External tools. Missing ones are downloaded into toolsDir on first run.
        const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        const string FfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-shared.zip";
        readonly string[] toolDirs;
        readonly string toolsDir;
        string ytDlpExe, ytDlpVersion;   // null when not found
        string ffmpegDir, ffmpegVersion; // folder holding ffmpeg.exe + ffprobe.exe
        bool ffmpegOnPath;               // yt-dlp finds it itself; otherwise we pass --ffmpeg-location
        bool settingUp;
        Border setupCard;
        TextBlock setupIcon, setupTitle, setupDetail, setupButtonText, ytIcon, ytStatus, ffIcon, ffStatus;
        ProgressBar setupProgress;
        Button setupButton;

        // Quality presets and the "Check formats" preview.
        ComboBox audioQualityCombo, videoQualityCombo, previewVideoCombo, previewAudioCombo;
        FrameworkElement audioQualityPanel, videoQualityPanel, previewVideoPanel, previewAudioPanel, previewFormats;
        TextBlock qualityNote, previewTitle, previewMeta, previewStatus, previewSize, previewDuration;
        Border previewCard, previewDurationPill;
        System.Windows.Shapes.Rectangle previewThumb;
        ProgressBar previewProgress;
        Button checkButton;
        string previewUrl;     // the link the preview card describes
        bool previewReady;     // its formats have loaded

        public Controller(Window window)
        {
            w = window;
            settingsPath = Path.Combine(appDir, AppInfo.FileName + ".settings");
            helpCachePath = Path.Combine(appDir, "yt-dlp-help.txt");

            urlBox = Find<TextBox>("UrlBox");
            folderBox = Find<TextBox>("FolderBox");
            logBox = Find<TextBox>("LogBox");
            searchBox = Find<TextBox>("SearchBox");
            extraBox = Find<TextBox>("ExtraBox");
            embedCheck = Find<CheckBox>("EmbedCheck");
            updateButton = Find<Button>("UpdateButton");
            versionText = Find<TextBlock>("VersionText");
            countText = Find<TextBlock>("CountText");
            descTitle = Find<TextBlock>("DescTitle");
            descMeta = Find<TextBlock>("DescMeta");
            descBody = Find<TextBlock>("DescBody");
            optBadgeText = Find<TextBlock>("OptBadgeText");
            optBadge = Find<Border>("OptBadge");
            dlBadgeText = Find<TextBlock>("DlBadgeText");
            dlBadge = Find<Border>("DlBadge");
            activeCount = Find<TextBlock>("ActiveCount");
            doneCount = Find<TextBlock>("DoneCount");
            activeEmpty = Find<Border>("ActiveEmpty");
            doneEmpty = Find<FrameworkElement>("DoneEmpty");
            formatHint = Find<TextBlock>("FormatHint");
            logPanel = Find<Border>("LogPanel");
            logToggleText = Find<TextBlock>("LogToggleText");
            navDownloads = Find<RadioButton>("NavDownloads");
            grid = Find<DataGrid>("OptionGrid");
            sectionList = Find<ListBox>("SectionList");
            setupCard = Find<Border>("SetupCard");
            setupIcon = Find<TextBlock>("SetupIcon");
            setupTitle = Find<TextBlock>("SetupTitle");
            setupDetail = Find<TextBlock>("SetupDetail");
            setupButtonText = Find<TextBlock>("SetupButtonText");
            setupProgress = Find<ProgressBar>("SetupProgress");
            setupButton = Find<Button>("SetupButton");
            ytIcon = Find<TextBlock>("YtIcon");
            ytStatus = Find<TextBlock>("YtStatus");
            ffIcon = Find<TextBlock>("FfIcon");
            ffStatus = Find<TextBlock>("FfStatus");

            string localTools = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.FileName);
            toolDirs = new[] { appDir, localTools };
            toolsDir = IsWritable(appDir) ? appDir : localTools;
            setupButton.Click += async delegate { await SetupMissingTools(); };

            var pages = new[] { Find<FrameworkElement>("HomePage"), Find<FrameworkElement>("DownloadsPage"), Find<FrameworkElement>("OptionsPage") };
            var navs = new[] { Find<RadioButton>("NavHome"), navDownloads, Find<RadioButton>("NavOptions") };
            for (int i = 0; i < navs.Length; i++)
            {
                FrameworkElement page = pages[i];
                navs[i].Checked += delegate { foreach (var p in pages) p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed; };
            }

            foreach (string[] f in Formats)
            {
                var rb = Find<RadioButton>(f[0]);
                formatButtons[f[1]] = rb;
                rb.Checked += delegate { UpdateFormatHint(); };
            }
            embedCheck.Checked += delegate { UpdateFormatHint(); };
            embedCheck.Unchecked += delegate { UpdateFormatHint(); };

            audioQualityCombo = Find<ComboBox>("AudioQualityCombo");
            videoQualityCombo = Find<ComboBox>("VideoQualityCombo");
            previewVideoCombo = Find<ComboBox>("PreviewVideoCombo");
            previewAudioCombo = Find<ComboBox>("PreviewAudioCombo");
            audioQualityPanel = Find<FrameworkElement>("AudioQualityPanel");
            videoQualityPanel = Find<FrameworkElement>("VideoQualityPanel");
            previewVideoPanel = Find<FrameworkElement>("PreviewVideoPanel");
            previewAudioPanel = Find<FrameworkElement>("PreviewAudioPanel");
            previewFormats = Find<FrameworkElement>("PreviewFormats");
            qualityNote = Find<TextBlock>("QualityNote");
            previewTitle = Find<TextBlock>("PreviewTitle");
            previewMeta = Find<TextBlock>("PreviewMeta");
            previewStatus = Find<TextBlock>("PreviewStatus");
            previewSize = Find<TextBlock>("PreviewSize");
            previewDuration = Find<TextBlock>("PreviewDuration");
            previewCard = Find<Border>("PreviewCard");
            previewDurationPill = Find<Border>("PreviewDurationPill");
            previewThumb = Find<System.Windows.Shapes.Rectangle>("PreviewThumb");
            previewProgress = Find<ProgressBar>("PreviewProgress");
            checkButton = Find<Button>("CheckButton");

            audioQualityCombo.ItemsSource = AudioQualities;
            audioQualityCombo.SelectedIndex = 0;
            videoQualityCombo.ItemsSource = VideoQualities;
            videoQualityCombo.SelectedIndex = 0;
            foreach (ComboBox c in new[] { audioQualityCombo, videoQualityCombo, previewVideoCombo, previewAudioCombo })
                c.SelectionChanged += delegate { UpdateFormatHint(); };
            checkButton.Click += async delegate { await CheckFormats(); };
            Find<Button>("PreviewClose").Click += delegate { HidePreview(); };
            urlBox.TextChanged += delegate { OnLinksChanged(); };

            Find<Button>("DownloadButton").Click += delegate { StartDownload(); };
            Find<Button>("PasteButton").Click += delegate { PasteLinks(); };
            updateButton.Click += delegate { RunUpdate(); };
            Find<Button>("BrowseButton").Click += delegate { BrowseFolder(); };
            Find<Button>("OpenFolderButton").Click += delegate { OpenFolder(); };
            Find<Button>("HeaderFolderButton").Click += delegate { OpenFolder(); };
            Find<Button>("ClearLogButton").Click += delegate { logBox.Clear(); };
            Find<Button>("ClearOptionsButton").Click += delegate { ClearOptions(); };
            Find<Button>("ClearDoneButton").Click += delegate { done.Clear(); UpdateCounts(); };
            Find<Button>("LogToggle").Click += delegate
            {
                bool show = logPanel.Visibility != Visibility.Visible;
                logPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                logToggleText.Text = show ? "Hide log" : "Show log";
            };
            urlBox.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; StartDownload(); }
            };

            Find<ItemsControl>("ActiveList").ItemsSource = active;
            Find<ItemsControl>("DoneList").ItemsSource = done;

            searchBox.TextChanged += delegate { ApplyFilter(); };
            sectionList.ItemsSource = sections;
            sectionList.SelectionChanged += delegate { ApplyFilter(); };
            grid.SelectionChanged += delegate { ShowDescription(); };
            // Clicking into a row's value box or check box should also select that row.
            grid.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((s, e) => SelectRowFrom(e.OriginalSource as DependencyObject)), true);
            grid.AddHandler(UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((s, e) => SelectRowFrom(e.OriginalSource as DependencyObject)), true);

            folderBox.Text = Path.Combine(appDir, "downloads");
            try
            {
                using (Stream s = typeof(Controller).Assembly.GetManifestResourceStream("logo.png"))
                {
                    var logo = new BitmapImage();
                    logo.BeginInit();
                    logo.StreamSource = s;
                    logo.CacheOption = BitmapCacheOption.OnLoad;
                    logo.EndInit();
                    Find<Image>("LogoImage").Source = logo;
                }
            }
            catch { } // header just shows no logo
            LoadSettings();
            UpdateFormatHint();
            UpdateCounts();

            w.Loaded += async delegate
            {
                if (File.Exists(helpCachePath)) PopulateOptions(ParseHelp(File.ReadAllText(helpCachePath, Encoding.UTF8)));
                urlBox.Focus();
                await DetectTools();
                if (ytDlpExe == null || ffmpegDir == null) await SetupMissingTools();
                else RunUpdate();
            };
            w.Closing += delegate { SaveSettings(); CancelAll(); };
        }

        T Find<T>(string name) where T : class
        {
            var e = w.FindName(name) as T;
            if (e == null) throw new InvalidOperationException("Missing element in MainWindow.xaml: " + name);
            return e;
        }

        // ---------------------------------------------------------------- home page

        string[] SelectedFormat()
        {
            foreach (string[] f in Formats) if (formatButtons[f[1]].IsChecked == true) return f;
            return Formats[0];
        }

        string EmbedArgs(string formatKey)
        {
            if (embedCheck.IsChecked != true) return "";
            // WAV can't hold cover art, so only tag it.
            return formatKey == "wav" ? "--embed-metadata" : "--embed-thumbnail --embed-metadata";
        }

        static bool IsAudioFormat(string key) { return key == "mp3" || key == "m4a" || key == "flac" || key == "wav"; }

        static string ChoiceValue(ComboBox c)
        {
            var ch = c.SelectedItem as Choice;
            return ch == null ? null : ch.Value;
        }

        // The yt-dlp arguments for a format preset, including the chosen quality.
        string FormatArgs(string key)
        {
            string aq = ChoiceValue(audioQualityCombo) ?? "0";
            switch (key)
            {
                case "mp3": return "-x --audio-format mp3 --audio-quality " + aq;
                case "m4a": return "-x --audio-format m4a --audio-quality " + aq;
                case "flac": return "-x --audio-format flac";
                case "wav": return "-x --audio-format wav";
                case "video":
                    string vq = ChoiceValue(videoQualityCombo) ?? "";
                    string sort = vq == "" ? " -S vcodec:h264,lang,quality,res,fps,hdr:12,acodec:aac"
                        : vq == "best" ? ""
                        : " -S res:" + vq + ",vcodec:h264,acodec:aac"; // highest up to the cap, H.264 when tied
                    return "--merge-output-format mp4 --remux-video mp4" + sort;
                default: return "";
            }
        }

        void UpdateFormatHint()
        {
            string key = SelectedFormat()[1];
            bool lossy = key == "mp3" || key == "m4a";
            audioQualityPanel.Visibility = lossy ? Visibility.Visible : Visibility.Collapsed;
            videoQualityPanel.Visibility = key == "video" ? Visibility.Visible : Visibility.Collapsed;
            qualityNote.Text = lossy ? "Choosing more than the source's own bitrate (often about 130-160 kbps on YouTube) makes the file bigger, not better."
                : key == "flac" || key == "wav" ? "Lossless: FLAC and WAV keep the full quality of the source, so there's no quality to choose."
                : key == "video" ? "Best compatible plays on almost anything. Best available can pick VP9 or AV1, which some older players can't open."
                : "";
            qualityNote.Visibility = qualityNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdatePreviewPanels();

            string args = (FormatArgs(key) + " " + EmbedArgs(key)).Trim();
            string exact = PreviewFormatArg(key);
            if (exact != null) args = (args + " -f " + exact).Trim();
            formatHint.Text = key == "custom" && args.Length == 0
                ? "No preset: only the settings on the Options page are used."
                : args;
        }

        // ---------------------------------------------------------------- "Check formats" preview

        string[] Links()
        {
            return urlBox.Text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        void OnLinksChanged()
        {
            string[] links = Links();
            checkButton.IsEnabled = links.Length == 1;
            // The preview only applies to the one link it was made for.
            if (previewUrl != null && (links.Length != 1 || links[0] != previewUrl)) HidePreview();
        }

        void HidePreview()
        {
            previewUrl = null;
            previewReady = false;
            previewCard.Visibility = Visibility.Collapsed;
            previewVideoCombo.ItemsSource = null;
            previewAudioCombo.ItemsSource = null;
            UpdateFormatHint();
        }

        async Task CheckFormats()
        {
            string[] links = Links();
            if (links.Length != 1) return;
            if (ytDlpExe == null)
            {
                MessageBox.Show(w, "yt-dlp isn't installed yet.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string url = links[0];
            previewUrl = url;
            previewReady = false;
            previewCard.Visibility = Visibility.Visible;
            previewTitle.Text = url;
            previewMeta.Text = "";
            previewStatus.Text = "Checking formats...";
            previewStatus.Foreground = (Brush)w.Resources["SubText"];
            previewProgress.Visibility = Visibility.Visible;
            previewFormats.Visibility = Visibility.Collapsed;
            previewDurationPill.Visibility = Visibility.Collapsed;
            previewThumb.Fill = null;
            checkButton.IsEnabled = false;

            string json = null, error = null;
            string exe = ytDlpExe;
            await Task.Run(() =>
            {
                try { json = CaptureWithErrors(exe, "-J --no-playlist --no-warnings " + Quote(url), out error); }
                catch (Exception ex) { error = ex.Message; }
            });
            checkButton.IsEnabled = Links().Length == 1;
            if (previewUrl != url) return; // the link changed while we were waiting
            previewProgress.Visibility = Visibility.Collapsed;

            Dictionary<string, object> info = null;
            try
            {
                if (!string.IsNullOrEmpty(json))
                    info = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 1000 }
                        .DeserializeObject(json) as Dictionary<string, object>;
            }
            catch (Exception ex) { error = "Couldn't read yt-dlp's answer: " + ex.Message; }
            if (info == null)
            {
                string msg = (error ?? "").Trim();
                int i = msg.LastIndexOf("ERROR:", StringComparison.Ordinal);
                previewStatus.Text = i >= 0 ? msg.Substring(i + 6).Trim() : msg.Length > 0 ? msg : "Couldn't get this link's formats.";
                previewStatus.Foreground = (Brush)w.Resources["Danger"];
                return;
            }
            ShowPreview(info);
        }

        void ShowPreview(Dictionary<string, object> info)
        {
            previewTitle.Text = Str(info, "title") ?? previewUrl;
            var meta = new List<string>();
            if (Str(info, "uploader") != null) meta.Add(Str(info, "uploader"));
            double views = Num(info, "view_count");
            if (views > 0) meta.Add(views.ToString("#,0", CultureInfo.InvariantCulture) + " views");
            previewMeta.Text = string.Join("  ·  ", meta);
            string duration = Str(info, "duration_string");
            previewDuration.Text = duration != null && Regex.IsMatch(duration, @"^\d+$") ? "0:" + duration.PadLeft(2, '0') : duration;
            previewDurationPill.Visibility = duration != null ? Visibility.Visible : Visibility.Collapsed;
            ImageSource thumb = LoadThumb(Str(info, "thumbnail"), Str(info, "id"), Str(info, "extractor_key"));
            previewThumb.Fill = thumb == null ? null : new ImageBrush(thumb) { Stretch = Stretch.UniformToFill };

            if (Str(info, "_type") == "playlist")
            {
                object[] entries = Value(info, "entries") as object[];
                previewStatus.Text = "This is a playlist" + (entries != null ? " of " + entries.Length + " videos" : "") +
                    ". Exact formats can only be picked for a single video, so the quality settings below are used.";
                return;
            }

            var videos = new List<Choice>();
            var audios = new List<Choice>();
            object[] formats = Value(info, "formats") as object[] ?? new object[0];
            foreach (object o in formats)
            {
                var f = o as Dictionary<string, object>;
                if (f == null || Str(f, "format_id") == null) continue;
                string ext = Str(f, "ext") ?? "?";
                string vcodec = Str(f, "vcodec"), acodec = Str(f, "acodec");
                if (ext == "mhtml") continue; // storyboard images
                bool hasVideo = vcodec != "none", hasAudio = acodec != "none";
                double size = Num(f, "filesize") > 0 ? Num(f, "filesize") : Num(f, "filesize_approx");
                // HLS streams often duplicate a regular format; label them so the two can be told apart.
                string sizeText = ((Str(f, "protocol") ?? "").Contains("m3u8") ? "  ·  HLS" : "") + (size > 0 ? "  ·  " + Mb((long)size) : "");
                if (hasVideo)
                {
                    double h = Num(f, "height"), fps = Num(f, "fps");
                    string res = h > 0 ? h + "p" + (fps > 30 ? Math.Round(fps).ToString(CultureInfo.InvariantCulture) : "") : (Str(f, "format_note") ?? Str(f, "format_id"));
                    string label = res + "  ·  " + ext + (vcodec != null ? " (" + vcodec.Split('.')[0] + ")" : "") +
                        (hasAudio && acodec != null ? "  ·  with audio" : "") + sizeText;
                    videos.Add(new Choice(label, Str(f, "format_id")) { Size = size, Rank = h, Rank2 = fps, HasVideo = true, HasAudio = hasAudio && acodec != null });
                }
                else if (hasAudio)
                {
                    double abr = Num(f, "abr") > 0 ? Num(f, "abr") : Num(f, "tbr");
                    string note = Str(f, "format_note");
                    string label = (abr > 0 ? Math.Round(abr) + " kbps" : note ?? Str(f, "format_id")) + "  ·  " + ext +
                        (acodec != null ? " (" + acodec.Split('.')[0] + ")" : "") + (abr > 0 && note != null ? "  ·  " + note : "") + sizeText;
                    audios.Add(new Choice(label, Str(f, "format_id")) { Size = size, Rank = abr, HasAudio = true });
                }
            }
            // Best first: by resolution, then frame rate (or by bitrate for audio), then size.
            Comparison<Choice> best = (a, b) =>
                a.Rank != b.Rank ? b.Rank.CompareTo(a.Rank) : a.Rank2 != b.Rank2 ? b.Rank2.CompareTo(a.Rank2) : b.Size.CompareTo(a.Size);
            videos.Sort(best);
            audios.Sort(best);
            videos.Insert(0, new Choice("Automatic (uses Video quality below)", null));
            audios.Insert(0, new Choice("Automatic (best audio)", null));
            previewVideoCombo.ItemsSource = videos;
            previewAudioCombo.ItemsSource = audios;
            previewVideoCombo.SelectedIndex = 0;
            previewAudioCombo.SelectedIndex = 0;

            previewStatus.Text = (videos.Count - 1) + " video and " + (audios.Count - 1) + " audio formats. " +
                "Leave them on Automatic, or pick exact ones for this video.";
            previewFormats.Visibility = Visibility.Visible;
            previewReady = true;
            UpdateFormatHint();
        }

        void UpdatePreviewPanels()
        {
            if (previewVideoPanel == null) return;
            string key = SelectedFormat()[1];
            // Audio formats only need an audio stream to convert from.
            previewVideoPanel.Visibility = IsAudioFormat(key) ? Visibility.Collapsed : Visibility.Visible;
            previewAudioPanel.Margin = IsAudioFormat(key) ? new Thickness(0) : new Thickness(6, 0, 0, 0);

            var v = previewVideoCombo.SelectedItem as Choice;
            var a = previewAudioCombo.SelectedItem as Choice;
            double total = (IsAudioFormat(key) ? 0 : (v != null ? v.Size : 0)) + (a != null ? a.Size : 0);
            bool picked = a != null && a.Value != null && (IsAudioFormat(key) || (v != null && v.Value != null));
            bool known = picked && a.Size > 0 && (IsAudioFormat(key) || v.Size > 0);
            previewSize.Text = !previewReady ? ""
                : known ? "Estimated download size: " + Mb((long)total) + (IsAudioFormat(key) && key != "wav" ? " (before converting)" : "")
                : picked ? "The site doesn't list a size for the selected format."
                : "The size is shown once you pick exact formats.";
        }

        // The -f value for the formats picked in the preview, or null to let the presets decide.
        string PreviewFormatArg(string key)
        {
            if (!previewReady) return null;
            var v = previewVideoCombo.SelectedItem as Choice;
            var a = previewAudioCombo.SelectedItem as Choice;
            string vid = v != null ? v.Value : null, aud = a != null ? a.Value : null;
            if (IsAudioFormat(key)) return aud;
            if (vid == null && aud == null) return null;
            if (vid != null && aud == null && v.HasAudio) return vid; // already has sound
            return (vid ?? "bv*") + "+" + (aud ?? "ba");
        }

        static object Value(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object v = Value(d, key);
            string s = v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(s) ? null : s;
        }

        static double Num(Dictionary<string, object> d, string key)
        {
            object v = Value(d, key);
            try { return v == null ? 0 : Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        // Runs yt-dlp and returns its output, also collecting what it wrote to stderr (for error messages).
        string CaptureWithErrors(string exe, string args, out string errors)
        {
            var err = new StringBuilder();
            using (Process p = NewProcess(args))
            {
                p.StartInfo.FileName = exe;
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
                p.Start();
                p.BeginErrorReadLine();
                string text = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                lock (err) errors = err.ToString();
                return text;
            }
        }

        void PasteLinks()
        {
            string text = "";
            try { text = Clipboard.GetText(); } catch { }
            if (text.Trim().Length == 0) return;
            string existing = urlBox.Text.TrimEnd();
            urlBox.Text = (existing.Length > 0 ? existing + Environment.NewLine : "") + text.Trim();
            urlBox.CaretIndex = urlBox.Text.Length;
            urlBox.Focus();
        }

        void StartDownload()
        {
            string[] urls = urlBox.Text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (urls.Length == 0)
            {
                MessageBox.Show(w, "Paste at least one link first.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string folder = folderBox.Text.Trim();
            if (folder.Length == 0)
            {
                MessageBox.Show(w, "Choose an output folder.", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string[] f = SelectedFormat();
            // While setup is running, links are queued and start once the tools have arrived.
            if (!settingUp && ytDlpExe == null)
            {
                MessageBox.Show(w, "yt-dlp isn't installed yet. Use the Download button at the top of the Home page to get it.",
                    AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!settingUp && ffmpegDir == null && (f[1] != "custom" || embedCheck.IsChecked == true))
            {
                MessageBox.Show(w, "Converting to " + f[2] + " needs ffmpeg, which isn't installed yet. " +
                    "Use the Download button at the top of the Home page to get it, or choose Custom.",
                    AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Defaults first, so anything set on the Options page overrides them (yt-dlp uses the last value given).
            var args = new StringBuilder(MachineArgs.Trim());
            string preset = FormatArgs(f[1]);
            if (preset.Length > 0) args.Append(' ').Append(preset);
            string embed = EmbedArgs(f[1]);
            if (embed.Length > 0) args.Append(' ').Append(embed);
            // Exact formats picked under "Check formats" apply to that one link.
            string exact = urls.Length == 1 && urls[0] == previewUrl ? PreviewFormatArg(f[1]) : null;
            if (exact != null) args.Append(" -f ").Append(Quote(exact));
            args.Append(" -P ").Append(Quote(folder)).Append(" -o \"%(title)s.%(ext)s\"");
            if (!BuildOptionArgs(args)) return;
            string extra = extraBox.Text.Trim();
            if (extra.Length > 0) args.Append(' ').Append(extra);

            SaveSettings();
            foreach (string u in urls) Enqueue(u, args.ToString(), f[1], f[2]);
            urlBox.Clear();
            navDownloads.IsChecked = true;
        }

        // ---------------------------------------------------------------- download queue

        void Enqueue(string url, string args, string formatKey, string formatLabel)
        {
            var job = new Job { Url = url, Args = args, FormatKey = formatKey, FormatLabel = formatLabel };
            DownloadItem item = NewItem(job);
            item.Title = url;
            item.StatusText = "Queued";
            job.Current = item;
            pending.Add(job);
            Pump();
        }

        DownloadItem NewItem(Job job)
        {
            var it = new DownloadItem { Job = job, FormatLabel = job.FormatLabel };
            it.CancelCommand = new Cmd(() => Cancel(it));
            it.RemoveCommand = new Cmd(() => { done.Remove(it); UpdateCounts(); });
            it.OpenCommand = new Cmd(() => Reveal(it));
            it.RetryCommand = new Cmd(() => { done.Remove(it); Enqueue(job.Url, job.Args, job.FormatKey, job.FormatLabel); });
            job.Items.Add(it);
            active.Add(it);
            UpdateCounts();
            return it;
        }

        void Pump()
        {
            UpdateCounts();
            if (running != null || updater != null || settingUp || pending.Count == 0) return;
            if (ytDlpExe == null)
            {
                foreach (Job j in pending.ToArray())
                    foreach (DownloadItem it in j.Items.ToArray()) Finish(it, "failed", "yt-dlp isn't installed");
                pending.Clear();
                return;
            }
            Job job = pending[0];
            pending.RemoveAt(0);
            StartJob(job);
        }

        void StartJob(Job job)
        {
            // Put first so an --ffmpeg-location set on the Options page still wins.
            string ffmpegArg = ffmpegDir != null && !ffmpegOnPath ? "--ffmpeg-location " + Quote(ffmpegDir) + " " : "";
            string arguments = ffmpegArg + job.Args + " " + Quote(job.Url);
            var p = NewProcess(arguments);
            p.OutputDataReceived += (s, e) => { if (e.Data != null) { string d = e.Data; UI(() => OnJobOutput(job, d)); } };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) { string d = e.Data; UI(() => OnJobError(job, d)); } };
            p.Exited += delegate { UI(() => OnJobExit(job, p)); };

            Log("");
            Log("> yt-dlp " + arguments);
            try { p.Start(); }
            catch (Exception ex)
            {
                job.LastError = "Could not start yt-dlp: " + ex.Message;
                foreach (DownloadItem it in job.Items.ToArray()) Finish(it, "failed", job.LastError);
                Pump();
                return;
            }
            job.Proc = p;
            running = job;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            job.Current.State = "fetching";
            job.Current.StatusText = "Fetching info...";
        }

        void OnJobOutput(Job job, string line)
        {
            DownloadItem cur = job.Current;
            if (line.StartsWith("__META__"))
            {
                string[] f = line.Substring(8).Split('\t');
                if (cur == null || cur.HasMeta || cur.IsFinished) cur = job.Current = NewItem(job);
                cur.HasMeta = true;
                cur.Title = Field(f, 0) ?? job.Url;
                cur.Uploader = Field(f, 1) ?? "";
                string duration = Field(f, 3) ?? "";
                // yt-dlp gives "19" for clips under a minute; show it as 0:19.
                cur.Duration = Regex.IsMatch(duration, @"^\d+$") ? "0:" + duration.PadLeft(2, '0') : duration;
                cur.Thumb = LoadThumb(Field(f, 2), Field(f, 4), Field(f, 5));
                cur.Percent = 0;
                cur.State = "downloading";
                cur.StatusText = "Starting...";
            }
            else if (line.StartsWith("__PROG__"))
            {
                if (cur == null || cur.IsFinished) return;
                string[] f = line.Substring(8).Split('\t');
                double pct;
                if (double.TryParse((Field(f, 0) ?? "").TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out pct))
                    cur.Percent = Math.Max(0, Math.Min(100, pct));
                cur.Speed = Field(f, 1) ?? "-";
                string eta = Field(f, 2);
                cur.EtaText = eta != null ? "ETA " + eta : "";
                string total = Field(f, 4) ?? (Field(f, 5) != null ? "~" + Field(f, 5) : null);
                cur.SizeText = (Field(f, 3) ?? "?") + (total != null ? " / " + total : "");
                cur.State = "downloading";
            }
            else if (line.StartsWith("__STAGE__"))
            {
                if (cur == null || cur.IsFinished) return;
                cur.State = "converting";
                cur.StatusText = job.FormatKey == "video" ? "Merging video and audio..."
                    : job.FormatKey == "custom" ? "Post-processing..." : "Converting to " + job.FormatLabel + "...";
            }
            else if (line.StartsWith("__FILE__"))
            {
                if (cur == null) return;
                cur.File = line.Substring(8).Trim();
                Finish(cur, "done", "Saved as " + Path.GetFileName(cur.File));
            }
            else Log(line);
        }

        void OnJobError(Job job, string line)
        {
            Log(line);
            if (line.StartsWith("ERROR:")) job.LastError = line.Substring(6).Trim();
        }

        void OnJobExit(Job job, Process p)
        {
            p.WaitForExit(); // flush remaining async output
            int code = p.ExitCode;
            p.Dispose();
            if (running == job) running = null;

            foreach (DownloadItem it in job.Items.ToArray())
            {
                if (it.IsFinished) continue;
                if (job.Cancelled) Finish(it, "cancelled", "Cancelled");
                else if (code == 0) Finish(it, "done", it.File != null ? "Saved as " + Path.GetFileName(it.File) : "Done");
                else Finish(it, "failed", job.LastError ?? "yt-dlp exited with code " + code);
            }
            Log(job.Cancelled ? "Cancelled." : code == 0 ? "Done." : "yt-dlp exited with code " + code + ".");
            Pump();
        }

        static string Field(string[] f, int i)
        {
            if (i >= f.Length) return null;
            string v = f[i].Trim();
            return v.Length == 0 || v == "NA" || v == "N/A" || v == "Unknown" ? null : v;
        }

        void Finish(DownloadItem it, string state, string status)
        {
            it.State = state;
            it.StatusText = status;
            it.NotifyFile();
            if (active.Remove(it)) done.Insert(0, it);
            UpdateCounts();
        }

        void Cancel(DownloadItem it)
        {
            Job job = it.Job;
            job.Cancelled = true;
            if (pending.Remove(job))
            {
                foreach (DownloadItem x in job.Items.ToArray()) Finish(x, "cancelled", "Cancelled");
                return;
            }
            if (job == running) Kill(job.Proc);
        }

        void CancelAll()
        {
            foreach (Job j in pending) j.Cancelled = true;
            pending.Clear();
            if (running != null) { running.Cancelled = true; Kill(running.Proc); }
        }

        static void Kill(Process p)
        {
            // Kill yt-dlp and any ffmpeg child it spawned.
            try { Process.Start(new ProcessStartInfo("taskkill", "/T /F /PID " + p.Id) { CreateNoWindow = true, UseShellExecute = false }); }
            catch { }
        }

        void Reveal(DownloadItem it)
        {
            if (it.File != null && File.Exists(it.File)) Process.Start("explorer.exe", "/select," + Quote(it.File));
            else OpenFolder();
        }

        static ImageSource LoadThumb(string url, string id, string extractor)
        {
            try
            {
                // YouTube's preferred thumbnails are WebP, which WPF can't decode; its JPEG variant always exists.
                if (extractor == "Youtube" && id != null) url = "https://i.ytimg.com/vi/" + id + "/mqdefault.jpg";
                if (url == null || Regex.IsMatch(url, @"\.webp(\?|$)", RegexOptions.IgnoreCase)) return null;
                var b = new BitmapImage();
                b.BeginInit();
                b.UriSource = new Uri(url);
                b.DecodePixelWidth = 256;
                b.EndInit();
                return b;
            }
            catch { return null; }
        }

        void UpdateCounts()
        {
            activeCount.Text = active.Count.ToString();
            doneCount.Text = done.Count.ToString();
            activeEmpty.Visibility = active.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            doneEmpty.Visibility = done.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            dlBadgeText.Text = active.Count.ToString();
            dlBadge.Visibility = active.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------------------------------------------------------------- updating yt-dlp

        void RunUpdate()
        {
            if (updater != null || settingUp) return;
            if (ytDlpExe == null) { ShowMissingTools(); return; }
            if (running != null)
            {
                MessageBox.Show(w, "yt-dlp can't be updated while a download is running.", AppInfo.Name,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Process p = NewProcess("-U");
            p.OutputDataReceived += (s, e) => { if (e.Data != null) { string d = e.Data; UI(() => Log(d)); } };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) { string d = e.Data; UI(() => Log(d)); } };
            p.Exited += delegate { UI(() => OnUpdateExit(p)); };
            Log("Checking for yt-dlp updates...");
            try { p.Start(); }
            catch (Exception ex)
            {
                Log("Could not start yt-dlp: " + ex.Message);
                versionText.Text = "yt-dlp.exe not found";
                MessageBox.Show(w, "Could not find or start yt-dlp.exe.\nPut it next to this program or on your PATH.",
                    AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            updater = p;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            updateButton.IsEnabled = false;
            versionText.Text = "Checking for updates...";
        }

        void OnUpdateExit(Process p)
        {
            p.WaitForExit();
            int code = p.ExitCode;
            p.Dispose();
            updater = null;
            updateButton.IsEnabled = true;
            Log(code == 0 ? "yt-dlp is up to date." : "Update check failed (exit " + code + ").");
            // The option list can change between yt-dlp versions, so re-read it after updating.
            RefreshHelpAndVersion();
            Pump();
        }

        // ---------------------------------------------------------------- tools (yt-dlp, ffmpeg)

        static bool IsWritable(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        static string FindOnPath(string exe)
        {
            foreach (string d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    string dir = d.Trim().Trim('"');
                    if (dir.Length > 0 && File.Exists(Path.Combine(dir, exe))) return Path.Combine(dir, exe);
                }
                catch (ArgumentException) { } // malformed PATH entry
            }
            return null;
        }

        string FindYtDlp()
        {
            foreach (string d in toolDirs)
                if (File.Exists(Path.Combine(d, "yt-dlp.exe"))) return Path.Combine(d, "yt-dlp.exe");
            return FindOnPath("yt-dlp.exe");
        }

        string FindFfmpegDir(out bool onPath)
        {
            onPath = false;
            foreach (string d in toolDirs)
                foreach (string dir in new[] { Path.Combine(d, "ffmpeg"), d })
                    if (File.Exists(Path.Combine(dir, "ffmpeg.exe")) && File.Exists(Path.Combine(dir, "ffprobe.exe"))) return dir;
            string exe = FindOnPath("ffmpeg.exe");
            if (exe == null || !File.Exists(Path.Combine(Path.GetDirectoryName(exe), "ffprobe.exe"))) return null;
            onPath = true;
            return Path.GetDirectoryName(exe);
        }

        string TryCapture(string exe, string args)
        {
            try { return Capture(exe, args); } catch { return ""; }
        }

        async Task DetectTools()
        {
            string yt = null, ytVer = null, ff = null, ffVer = null;
            bool onPath = false;
            await Task.Run(() =>
            {
                yt = FindYtDlp();
                if (yt != null) ytVer = TryCapture(yt, "--version").Trim();
                ff = FindFfmpegDir(out onPath);
                if (ff != null)
                {
                    // Release builds give "7.1-full_build-...", git builds "2026-09-17-git-..." or "N-127565-g5fb8...";
                    // keep just the version, date or build number.
                    Match m = Regex.Match(TryCapture(Path.Combine(ff, "ffmpeg.exe"), "-version"),
                        @"^ffmpeg version (\d{4}-\d{2}-\d{2}|N-\d+|n?\d+(?:\.\d+)*)");
                    ffVer = m.Success ? m.Groups[1].Value : "";
                }
            });
            ytDlpExe = yt;
            ytDlpVersion = string.IsNullOrEmpty(ytVer) ? null : ytVer;
            ffmpegDir = ff;
            ffmpegOnPath = onPath;
            ffmpegVersion = string.IsNullOrEmpty(ffVer) ? null : ffVer;
            UpdateToolStatus();
        }

        void UpdateToolStatus()
        {
            SetToolStatus(ytIcon, ytStatus, "yt-dlp", ytDlpExe != null, ytDlpVersion);
            SetToolStatus(ffIcon, ffStatus, "ffmpeg", ffmpegDir != null, ffmpegVersion);
            if (ytDlpExe != null && ytDlpVersion != null) versionText.Text = "Powered by yt-dlp " + ytDlpVersion;
            else if (ytDlpExe == null) versionText.Text = "yt-dlp not installed";
        }

        void SetToolStatus(TextBlock icon, TextBlock text, string name, bool found, string version)
        {
            icon.Text = found ? "" : ""; // check mark / warning
            icon.Foreground = (Brush)w.Resources[found ? "Success" : "Danger"];
            text.Text = name + (found ? (version != null ? " " + version : " installed") : " missing");
        }

        void ShowMissingTools()
        {
            var missing = new List<string>();
            if (ytDlpExe == null) missing.Add("yt-dlp");
            if (ffmpegDir == null) missing.Add("ffmpeg");
            if (missing.Count == 0) { setupCard.Visibility = Visibility.Collapsed; return; }
            ShowSetup("", string.Join(" and ", missing) + (missing.Count == 1 ? " is" : " are") + " missing",
                (ytDlpExe == null ? "yt-dlp does the downloading. " : "") +
                (ffmpegDir == null ? "ffmpeg is needed to convert to MP3 and other formats. " : "") +
                "They'll be downloaded from their official GitHub releases into " + toolsDir + ".", "Download");
        }

        void ShowSetup(string glyph, string title, string detail, string button)
        {
            setupCard.Visibility = Visibility.Visible;
            setupIcon.Text = glyph;
            setupTitle.Text = title;
            setupDetail.Text = detail;
            setupProgress.Visibility = button == null ? Visibility.Visible : Visibility.Collapsed;
            setupButton.Visibility = button == null ? Visibility.Collapsed : Visibility.Visible;
            if (button != null) setupButtonText.Text = button;
        }

        // Downloads whichever of yt-dlp / ffmpeg is missing, then starts anything that was queued meanwhile.
        async Task SetupMissingTools()
        {
            if (settingUp) return;
            settingUp = true;
            bool gotYtDlp = false;
            string error = null;
            int steps = (ytDlpExe == null ? 1 : 0) + (ffmpegDir == null ? 1 : 0), step = 0;
            try
            {
                Directory.CreateDirectory(toolsDir);
                if (ytDlpExe == null)
                {
                    step++;
                    string dest = Path.Combine(toolsDir, "yt-dlp.exe");
                    await Fetch(YtDlpUrl, dest, "yt-dlp", step, steps);
                    gotYtDlp = true;
                }
                if (ffmpegDir == null)
                {
                    step++;
                    string zip = Path.Combine(toolsDir, "ffmpeg-download.zip");
                    await Fetch(FfmpegUrl, zip, "ffmpeg", step, steps);
                    ShowSetup("", "Setting up ffmpeg", "Unpacking...", null);
                    setupProgress.IsIndeterminate = true;
                    string dir = Path.Combine(toolsDir, "ffmpeg");
                    await Task.Run(() => ExtractFfmpeg(zip, dir));
                    File.Delete(zip);
                }
            }
            catch (Exception ex) { error = ex.Message; }
            finally
            {
                settingUp = false;
                setupProgress.IsIndeterminate = false;
            }

            await DetectTools();
            if (error != null)
            {
                Log("Setup failed: " + error);
                ShowSetup("", "Couldn't download the tools", error + " Check your internet connection and try again.", "Try again");
            }
            else
            {
                setupCard.Visibility = Visibility.Collapsed;
                Log("Tools ready in " + toolsDir);
            }

            if (gotYtDlp) RefreshHelpAndVersion();   // fresh download, so no update check needed
            else if (ytDlpExe != null) RunUpdate();
            Pump();
        }

        async Task Fetch(string url, string dest, string name, int step, int steps)
        {
            string title = "Downloading " + name + (steps > 1 ? " (" + step + " of " + steps + ")" : "");
            ShowSetup("", title, "Connecting to GitHub...", null);
            setupProgress.IsIndeterminate = true;
            Log(title + " from " + url);
            string part = dest + ".part";
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = AppInfo.FileName;
                wc.DownloadProgressChanged += (s, e) =>
                {
                    bool known = e.TotalBytesToReceive > 0;
                    setupProgress.IsIndeterminate = !known;
                    if (known) setupProgress.Value = e.ProgressPercentage;
                    setupDetail.Text = Mb(e.BytesReceived) + (known ? " of " + Mb(e.TotalBytesToReceive) : "") + " · from the official GitHub release";
                };
                try { await wc.DownloadFileTaskAsync(new Uri(url), part); }
                catch { try { File.Delete(part); } catch { } throw; }
            }
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(part, dest);
        }

        static string Mb(long bytes)
        {
            return bytes >= 1073741824
                ? (bytes / 1073741824.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB"
                : (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        // Keeps only the programs and DLLs from the archive's bin folder (ffplay isn't needed).
        static void ExtractFfmpeg(string zip, string dir)
        {
            Directory.CreateDirectory(dir);
            using (ZipArchive archive = ZipFile.OpenRead(zip))
            {
                foreach (ZipArchiveEntry e in archive.Entries)
                {
                    if (e.Name.Length == 0 || !e.FullName.Contains("/bin/") || e.Name.Equals("ffplay.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    // Entry.Name has no directory part, so nothing can be written outside dir.
                    e.ExtractToFile(Path.Combine(dir, e.Name), true);
                }
            }
            if (!File.Exists(Path.Combine(dir, "ffmpeg.exe")) || !File.Exists(Path.Combine(dir, "ffprobe.exe")))
                throw new IOException("The ffmpeg download didn't contain ffmpeg.exe and ffprobe.exe.");
        }

        void RefreshHelpAndVersion()
        {
            string exe = YtDlpPath();
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string version = Capture(exe, "--version").Trim();
                    UI(() => versionText.Text = version.Length > 0 ? "Powered by yt-dlp " + version : "Powered by yt-dlp");
                    string text = Capture(exe, "--help");
                    List<OptionRow> opts = ParseHelp(text);
                    if (opts.Count == 0) return;
                    File.WriteAllText(helpCachePath, text, Encoding.UTF8);
                    UI(() => PopulateOptions(opts));
                }
                catch (Exception ex)
                {
                    UI(() => Log("Could not read yt-dlp options: " + ex.Message));
                }
            });
        }

        string YtDlpPath()
        {
            return ytDlpExe ?? Path.Combine(toolsDir, "yt-dlp.exe");
        }

        Process NewProcess(string arguments)
        {
            var psi = new ProcessStartInfo(YtDlpPath(), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = appDir
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            return new Process { StartInfo = psi, EnableRaisingEvents = true };
        }

        string Capture(string exe, string args)
        {
            using (Process p = NewProcess(args))
            {
                p.StartInfo.FileName = exe;
                p.StartInfo.RedirectStandardError = false;
                p.StartInfo.StandardErrorEncoding = null; // Process.Start rejects an encoding for a stream it isn't redirecting
                p.Start();
                string text = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return text;
            }
        }

        // Quotes one argument using the Windows command-line rules (handles embedded quotes and trailing backslashes).
        static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in s)
            {
                if (c == '\\') { backslashes++; continue; }
                sb.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
                sb.Append(c);
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2).Append('"');
            return sb.ToString();
        }

        // ---------------------------------------------------------------- options page

        static List<OptionRow> ParseHelp(string text)
        {
            var list = new List<OptionRow>();
            string section = null;
            OptionRow cur = null;
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                Match sm = SectionRegex.Match(raw);
                if (sm.Success) { section = sm.Groups[1].Value; cur = null; continue; }
                if (section == null) continue;

                if (raw.StartsWith("    -"))
                {
                    string body = raw.Substring(4);
                    Match gap = Regex.Match(body, @"\s{2,}");
                    string spec = gap.Success ? body.Substring(0, gap.Index) : body.Trim();
                    string desc = gap.Success ? body.Substring(gap.Index).Trim() : "";
                    Match m = SpecRegex.Match(spec);
                    if (!m.Success) { cur = null; continue; }
                    cur = new OptionRow
                    {
                        Name = m.Groups[2].Value,
                        Aliases = m.Groups[1].Value.Trim().TrimEnd(','),
                        Metavar = m.Groups[3].Success ? m.Groups[3].Value : null,
                        Section = section,
                        Description = desc
                    };
                    // Preset aliases ("-t mp3", "-t aac", ...) are fixed choices, so each becomes a plain flag.
                    if (section == "Preset Aliases" && cur.Metavar != null)
                    {
                        cur.Name = cur.Name + " " + cur.Metavar;
                        cur.Metavar = null;
                    }
                    if (!Hidden.Contains(cur.Name)) list.Add(cur);
                }
                else if (cur != null && raw.StartsWith("     ") && raw.Trim().Length > 0)
                {
                    string more = raw.Trim();
                    // Rejoin words the help text hyphenated across lines ("--no-js-" + "runtimes").
                    bool hyphenBreak = cur.Description.EndsWith("-") && cur.Description.Length > 1
                        && char.IsLetter(cur.Description[cur.Description.Length - 2]);
                    cur.Description += (cur.Description.Length == 0 || hyphenBreak ? "" : " ") + more;
                }
            }
            return list;
        }

        void PopulateOptions(List<OptionRow> parsed)
        {
            if (parsed.Count == 0) return;
            CaptureState();

            foreach (OptionRow r in parsed)
            {
                string[] state;
                if (optionState.TryGetValue(r.Name, out state))
                {
                    if (r.TakesValue) r.Value = state[1];
                    r.Enabled = state[0] == "1";
                }
                r.EnabledChanged += delegate { UpdateOptionCount(); };
            }
            rows = new ObservableCollection<OptionRow>(parsed);

            string selectedKey = sectionList.SelectedItem is SectionItem ? ((SectionItem)sectionList.SelectedItem).Key : AllKey;
            sections.Clear();
            sections.Add(new SectionItem { Key = AllKey, Name = "All options", Count = rows.Count });
            enabledSection = new SectionItem { Key = EnabledKey, Name = "Enabled" };
            sections.Add(enabledSection);
            var bySection = new Dictionary<string, SectionItem>();
            foreach (OptionRow r in rows)
            {
                SectionItem s;
                if (!bySection.TryGetValue(r.Section, out s))
                {
                    string display = r.Section.EndsWith(" Options") ? r.Section.Substring(0, r.Section.Length - 8) : r.Section;
                    s = new SectionItem { Key = r.Section, Name = display };
                    bySection[r.Section] = s;
                    sections.Add(s);
                }
                s.Count++;
            }

            grid.ItemsSource = rows;
            CollectionViewSource.GetDefaultView(rows).Filter = FilterRow;
            SectionItem keep = null;
            foreach (SectionItem s in sections) if (s.Key == selectedKey) keep = s;
            sectionList.SelectedItem = keep ?? sections[0];

            UpdateOptionCount();
            ApplyFilter();
            descTitle.Text = rows.Count + " options loaded";
            descMeta.Visibility = Visibility.Collapsed;
            descBody.Text = "Tick an option to enable it. Options that take a value are switched on as soon as you type one.";
        }

        bool FilterRow(object o)
        {
            var r = (OptionRow)o;
            var s = sectionList.SelectedItem as SectionItem;
            string key = s == null ? AllKey : s.Key;
            if (key == EnabledKey && !r.Enabled) return false;
            if (key != AllKey && key != EnabledKey && r.Section != key) return false;
            string q = searchBox.Text.Trim();
            return q.Length == 0
                || r.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || r.Aliases.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || r.Description.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void ApplyFilter()
        {
            if (grid.ItemsSource == null) return;
            CollectionViewSource.GetDefaultView(rows).Refresh();
        }

        void SelectRowFrom(DependencyObject d)
        {
            while (d != null && !(d is DataGridRow))
                d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            var row = d as DataGridRow;
            if (row != null && grid.SelectedItem != row.Item) grid.SelectedItem = row.Item;
        }

        void ShowDescription()
        {
            var r = grid.SelectedItem as OptionRow;
            if (r == null) return;
            descTitle.Text = r.Name + r.MetavarText;
            descMeta.Text = r.Section + (r.Aliases.Length > 0 ? "  ·  alias " + r.Aliases : "");
            descMeta.Visibility = Visibility.Visible;
            descBody.Text = r.Description + (r.MultiArg
                ? "  (This option takes several arguments: type them separated by spaces, quoting any that contain spaces.)" : "");
        }

        void UpdateOptionCount()
        {
            int n = 0;
            foreach (OptionRow r in rows) if (r.Enabled) n++;
            if (enabledSection != null) enabledSection.Count = n;
            countText.Text = n == 0 ? "" : n + " enabled";
            optBadgeText.Text = n.ToString();
            optBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void ClearOptions()
        {
            if (MessageBox.Show(w, "Turn off all options and clear their values?", AppInfo.Name,
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            foreach (OptionRow r in rows) { r.Value = ""; r.Enabled = false; }
            optionState.Clear();
            ApplyFilter();
        }

        void CaptureState()
        {
            foreach (OptionRow r in rows)
            {
                if (r.Enabled || r.Value.Length > 0) optionState[r.Name] = new[] { r.Enabled ? "1" : "0", r.Value };
                else optionState.Remove(r.Name);
            }
        }

        // Appends the enabled options as command-line text; returns false if one is missing its value.
        bool BuildOptionArgs(StringBuilder args)
        {
            foreach (OptionRow r in rows)
            {
                if (!r.Enabled) continue;
                string v = r.Value.Trim();
                if (r.TakesValue && v.Length == 0)
                {
                    MessageBox.Show(w, r.Name + " is enabled but needs a value (" + r.Metavar + ").", AppInfo.Name,
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                args.Append(' ').Append(r.Name);
                if (r.TakesValue) args.Append(' ').Append(r.MultiArg ? v : Quote(v));
            }
            return true;
        }

        // ---------------------------------------------------------------- misc

        void UI(Action action)
        {
            w.Dispatcher.BeginInvoke(action);
        }

        void Log(string line)
        {
            logBox.AppendText(line + Environment.NewLine);
            logBox.ScrollToEnd();
        }

        void BrowseFolder()
        {
            string picked = FolderPicker.Pick(new WindowInteropHelper(w).Handle, folderBox.Text.Trim());
            if (picked != null) folderBox.Text = picked;
        }

        void OpenFolder()
        {
            string dir = folderBox.Text.Trim();
            try
            {
                Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", Quote(dir));
            }
            catch (Exception ex) { MessageBox.Show(w, ex.Message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        static void SelectChoice(ComboBox c, string value)
        {
            foreach (object o in c.Items)
                if (o is Choice && ((Choice)o).Value == value) { c.SelectedItem = o; return; }
        }

        static string Clean(string s)
        {
            return (s ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
        }

        void LoadSettings()
        {
            // Settings from before the rename to YT DLP Studio; the next save writes the new file.
            string legacy = Path.Combine(appDir, "MP3Downloader.settings");
            string path = File.Exists(settingsPath) ? settingsPath : legacy;
            if (!File.Exists(path)) return;
            try
            {
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string[] f = line.Split('\t');
                    if (f.Length < 2) continue;
                    RadioButton rb;
                    switch (f[0])
                    {
                        case "folder": folderBox.Text = f[1]; break;
                        case "format": if (formatButtons.TryGetValue(f[1], out rb)) rb.IsChecked = true; break;
                        case "aq": SelectChoice(audioQualityCombo, f[1]); break;
                        case "vq": SelectChoice(videoQualityCombo, f[1]); break;
                        case "preset": formatButtons[f[1] == "1" ? "mp3" : "custom"].IsChecked = true; break; // older settings files
                        case "embed": embedCheck.IsChecked = f[1] == "1"; break;
                        case "extra": extraBox.Text = f[1]; break;
                        case "opt": if (f.Length >= 4) optionState[f[1]] = new[] { f[2], f[3] }; break;
                    }
                }
            }
            catch (Exception ex) { Log("Could not load settings: " + ex.Message); }
        }

        void SaveSettings()
        {
            try
            {
                CaptureState();
                var lines = new List<string>
                {
                    "folder\t" + Clean(folderBox.Text),
                    "format\t" + SelectedFormat()[1],
                    "aq\t" + ChoiceValue(audioQualityCombo),
                    "vq\t" + ChoiceValue(videoQualityCombo),
                    "embed\t" + (embedCheck.IsChecked == true ? "1" : "0"),
                    "extra\t" + Clean(extraBox.Text)
                };
                foreach (var kv in optionState) lines.Add("opt\t" + kv.Key + "\t" + kv.Value[0] + "\t" + Clean(kv.Value[1]));
                File.WriteAllLines(settingsPath, lines, Encoding.UTF8);
            }
            catch { }
        }
    }

    // Light/dark palettes plus the Windows 11 Mica backdrop and dark title bar.
    static class Theme
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
        struct Margins { public int Left, Right, Top, Bottom; }

        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_SYSTEMBACKDROP_TYPE = 38, DWMSBT_MAINWINDOW = 2;

        public static bool SystemUsesDarkMode()
        {
            // YTDLP_STUDIO_THEME=light|dark overrides the Windows setting.
            string forced = Environment.GetEnvironmentVariable("YTDLP_STUDIO_THEME");
            if (forced == "light" || forced == "dark") return forced == "dark";
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        public static void Apply(Window w, bool dark)
        {
            var c = dark ? new Dictionary<string, string>
            {
                { "WindowBg", "#202020" }, { "CardBg", "#0DFFFFFF" }, { "CardBorder", "#1AFFFFFF" },
                { "ControlBg", "#0FFFFFFF" }, { "ControlHover", "#18FFFFFF" }, { "ControlBorder", "#1FFFFFFF" },
                { "InputFocusBg", "#E61C1C1C" }, { "PillActive", "#1AFFFFFF" },
                { "Text", "#FFFFFF" }, { "SubText", "#A8FFFFFF" },
                { "Accent", "#A996FF" }, { "AccentHover", "#BBACFF" }, { "AccentText", "#140A33" }, { "AccentSoft", "#33A996FF" },
                { "Danger", "#FF99A4" }, { "DangerSoft", "#26FF99A4" }, { "DangerHover", "#40FF99A4" }, { "Success", "#6CCB5F" },
                { "LogBg", "#59000000" }, { "LogText", "#D6FFFFFF" },
                { "RowHover", "#0AFFFFFF" }, { "Selection", "#14FFFFFF" }, { "RowLine", "#0DFFFFFF" },
                { "ScrollThumb", "#40FFFFFF" }, { "PopupBg", "#2C2C2C" }
            } : new Dictionary<string, string>
            {
                { "WindowBg", "#F3F3F3" }, { "CardBg", "#B3FFFFFF" }, { "CardBorder", "#14000000" },
                { "ControlBg", "#E6FFFFFF" }, { "ControlHover", "#F2F7F7F7" }, { "ControlBorder", "#1F000000" },
                { "InputFocusBg", "#FFFFFFFF" }, { "PillActive", "#FFFFFFFF" },
                { "Text", "#1B1B1B" }, { "SubText", "#99000000" },
                { "Accent", "#6D4AFF" }, { "AccentHover", "#7E60FF" }, { "AccentText", "#FFFFFF" }, { "AccentSoft", "#1F6D4AFF" },
                { "Danger", "#C42B1C" }, { "DangerSoft", "#1AC42B1C" }, { "DangerHover", "#2EC42B1C" }, { "Success", "#0F7B0F" },
                { "LogBg", "#0A000000" }, { "LogText", "#E6000000" },
                { "RowHover", "#08000000" }, { "Selection", "#0F000000" }, { "RowLine", "#0A000000" },
                { "ScrollThumb", "#40000000" }, { "PopupBg", "#FFFFFF" }
            };
            foreach (var kv in c)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(kv.Value));
                brush.Freeze();
                w.Resources[kv.Key] = brush;
            }

            w.SourceInitialized += delegate
            {
                IntPtr hwnd = new WindowInteropHelper(w).Handle;
                int on = dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4);
                int backdrop = DWMSBT_MAINWINDOW;
                // Only go transparent if Mica is actually available (Windows 11 22H2+); otherwise keep the solid background.
                if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, 4) == 0)
                {
                    w.Background = Brushes.Transparent;
                    HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
                    var m = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                    DwmExtendFrameIntoClientArea(hwnd, ref m);
                }
            };
        }
    }

    // The modern Windows folder picker (IFileOpenDialog in folder mode).
    static class FolderPicker
    {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")] class FileOpenDialogCom { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr specs);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr sink, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem item);

        const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, SIGDN_FILESYSPATH = 0x80058000;

        public static string Pick(IntPtr owner, string initial)
        {
            var dlg = (IFileOpenDialog)new FileOpenDialogCom();
            try
            {
                uint opts;
                dlg.GetOptions(out opts);
                dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);
                dlg.SetTitle("Choose where to save downloads");
                if (Directory.Exists(initial))
                {
                    try
                    {
                        Guid iid = typeof(IShellItem).GUID;
                        IShellItem start;
                        SHCreateItemFromParsingName(initial, IntPtr.Zero, ref iid, out start);
                        dlg.SetFolder(start);
                    }
                    catch { }
                }
                if (dlg.Show(owner) != 0) return null;
                IShellItem result;
                dlg.GetResult(out result);
                string path;
                result.GetDisplayName(SIGDN_FILESYSPATH, out path);
                return path;
            }
            finally { Marshal.ReleaseComObject(dlg); }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            var app = new Application();
            try
            {
                Window w;
                using (Stream s = typeof(Program).Assembly.GetManifestResourceStream("MainWindow.xaml"))
                    w = (Window)XamlReader.Load(s);
                Theme.Apply(w, Theme.SystemUsesDarkMode());
                new Controller(w);
                app.Run(w);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppInfo.FileName + "-error.log"), ex.ToString()); } catch { }
                MessageBox.Show(AppInfo.Name + " failed to start:\n\n" + ex.Message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
