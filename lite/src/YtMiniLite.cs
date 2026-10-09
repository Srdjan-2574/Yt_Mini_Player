// YtMiniLite - an audio-only YouTube Music player, without the website or a browser.
// Search and radio: the YT Music internal API. Audio URL: yt-dlp (+ Deno). Playback: Windows MediaPlayer (WinRT),
// which also provides media keys and the now-playing card in the Windows volume flyout.
// Written in C# 5 so it compiles with the .NET Framework csc.exe, no .NET SDK required.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

static class Program
{
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main()
    {
        bool first;
        using (new Mutex(true, "YtMiniLite_SingleInstance", out first))
        {
            if (!first) return;
            SetProcessDPIAware();
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; // + TLS 1.3
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new LiteForm());
        }
    }
}

// Translations: English is built in, other languages are lang\<code>.txt files with "English text=Translation" lines
// (plus "@name=Language name"). A new language is just a new file, no code changes.
static class Lang
{
    static readonly string Dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lang");
    static readonly Dictionary<string, string> Map = new Dictionary<string, string>();
    public static string Code = "en";

    public static void Load(string code)
    {
        Map.Clear();
        Code = "en";
        string file = Path.Combine(Dir, code + ".txt");
        if (code == "en" || !File.Exists(file)) return;
        foreach (KeyValuePair<string, string> kv in Read(file)) Map[kv.Key] = kv.Value;
        Code = code;
    }

    public static string T(string english)
    {
        string translated;
        return Map.TryGetValue(english, out translated) ? translated : english;
    }

    public static string T(string english, params object[] args)
    {
        return string.Format(T(english), args);
    }

    // (code, name) for every language: English + every file in lang\.
    public static List<KeyValuePair<string, string>> Available()
    {
        var all = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("en", "English") };
        if (!Directory.Exists(Dir)) return all;
        foreach (string file in Directory.GetFiles(Dir, "*.txt"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            foreach (KeyValuePair<string, string> kv in Read(file))
                if (kv.Key == "@name") name = kv.Value;
            all.Add(new KeyValuePair<string, string>(Path.GetFileNameWithoutExtension(file), name));
        }
        return all;
    }

    static IEnumerable<KeyValuePair<string, string>> Read(string file)
    {
        foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
        {
            int eq = line.IndexOf('=');
            if (eq > 0 && !line.StartsWith("#"))
                yield return new KeyValuePair<string, string>(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
        }
    }
}

sealed class Track
{
    public readonly string Id, Title, Subtitle, Length;
    public Task<string> Url;      // audio URL (yt-dlp), cached until it expires
    public DateTime UrlExpires;

    public Track(string id, string title, string subtitle, string length)
    {
        // Search puts the duration at the end of the subtitle ("Daft Punk • 35M views • 3:27") - split it off.
        Match m = Regex.Match(subtitle, @"^(.*) • (\d+:\d{2}(?::\d{2})?)$");
        if (length.Length == 0 && m.Success)
        {
            subtitle = m.Groups[1].Value;
            length = m.Groups[2].Value;
        }
        Id = id;
        Title = title;
        Subtitle = subtitle;
        Length = length;
    }

    public string Artist
    {
        get
        {
            foreach (string part in Subtitle.Split(new[] { " • " }, StringSplitOptions.RemoveEmptyEntries))
                if (part != "Song" && part != "Video") return part;
            return "";
        }
    }
}

// The YT Music internal API (the same one music.youtube.com uses) + yt-dlp for the audio URL.
static class YtMusic
{
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 1000 };
    static readonly string Tools = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools");

    // "Songs" filter (same as the Songs tab on the site, value taken from ytmusicapi): the original song comes first,
    // instead of the music videos, covers and remixes an unfiltered search returns.
    const string SongsOnly = "EgWKAQIIAWoMEA4QChADEAQQCRAF";

    public static List<Track> Search(string query)
    {
        List<Track> songs = Search(query, SongsOnly);
        return songs.Count > 0 ? songs : Search(query, null); // e.g. something that only exists as a video
    }

    static List<Track> Search(string query, string filter)
    {
        var tracks = new List<Track>();
        var seen = new HashSet<string>();
        string fields = "\"query\":" + Json.Serialize(query) + (filter == null ? "" : ",\"params\":" + Json.Serialize(filter));
        foreach (object item in Collect(Post("search", fields), "musicResponsiveListItemRenderer"))
        {
            string id = At(item, "playlistItemData", "videoId") as string;
            var cols = At(item, "flexColumns") as IList;
            if (id == null || cols == null || !seen.Add(id)) continue; // artists and albums have no videoId
            string title = Text(At(cols, 0, "musicResponsiveListItemFlexColumnRenderer", "text"));
            string sub = Text(At(cols, 1, "musicResponsiveListItemFlexColumnRenderer", "text"));
            tracks.Add(new Track(id, title, sub, ""));
        }
        return tracks;
    }

    // A song's "radio" - the same list YT Music builds when you play a song.
    public static List<Track> Radio(string videoId)
    {
        var tracks = new List<Track>();
        string body = "\"videoId\":" + Json.Serialize(videoId) + ",\"playlistId\":" + Json.Serialize("RDAMVM" + videoId);
        foreach (object item in Collect(Post("next", body), "playlistPanelVideoRenderer"))
        {
            string id = At(item, "videoId") as string;
            if (id == null) continue;
            tracks.Add(new Track(id, Text(At(item, "title")), Text(At(item, "longBylineText")), Text(At(item, "lengthText"))));
        }
        return tracks;
    }

    public static string ResolveAudioUrl(string videoId)
    {
        var psi = new ProcessStartInfo(Path.Combine(Tools, @"yt-dlp\yt-dlp.exe"),
            // Opus/WebM: YouTube's m4a is a DASH container that the Windows player misreads (it jumps to the end of the song).
            "--js-runtimes \"deno:" + Path.Combine(Tools, @"deno\deno.exe") + "\" -f \"bestaudio[acodec=opus]/bestaudio[ext=webm]\" " +
            "-g --no-warnings --no-playlist https://music.youtube.com/watch?v=" + videoId);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using (Process p = Process.Start(psi))
        {
            Task<string> err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            foreach (string line in output.Split('\n'))
                if (line.StartsWith("http")) return line.Trim();
            string[] errLines = err.Result.Trim().Split('\n');
            throw new Exception(errLines[errLines.Length - 1].Trim());
        }
    }

    static object Post(string endpoint, string fields)
    {
        using (var web = new WebClient())
        {
            web.Encoding = Encoding.UTF8;
            web.Headers[HttpRequestHeader.ContentType] = "application/json";
            web.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
            string body = "{\"context\":{\"client\":{\"clientName\":\"WEB_REMIX\",\"clientVersion\":\"1.20261001.01.00\",\"hl\":\"en\"}}," + fields + "}";
            return Json.DeserializeObject(web.UploadString("https://music.youtube.com/youtubei/v1/" + endpoint + "?prettyPrint=false", body));
        }
    }

    // Collects every object under the given key anywhere in the response - resilient to layout changes.
    static List<object> Collect(object node, string key)
    {
        var found = new List<object>();
        CollectInto(node, key, found);
        return found;
    }

    static void CollectInto(object node, string key, List<object> found)
    {
        var dict = node as IDictionary<string, object>;
        if (dict != null)
        {
            foreach (var kv in dict)
            {
                if (kv.Key == key) found.Add(kv.Value);
                else CollectInto(kv.Value, key, found);
            }
            return;
        }
        var list = node as IList;
        if (list != null)
            foreach (object child in list) CollectInto(child, key, found);
    }

    static object At(object node, params object[] path)
    {
        foreach (object key in path)
        {
            if (key is string)
            {
                var dict = node as IDictionary<string, object>;
                object value;
                node = dict != null && dict.TryGetValue((string)key, out value) ? value : null;
            }
            else
            {
                var list = node as IList;
                int i = (int)key;
                node = list != null && i < list.Count ? list[i] : null;
            }
            if (node == null) return null;
        }
        return node;
    }

    static string Text(object textNode)
    {
        var runs = At(textNode, "runs") as IList;
        if (runs == null) return At(textNode, "simpleText") as string ?? "";
        var sb = new StringBuilder();
        foreach (object run in runs) sb.Append(At(run, "text") as string);
        return sb.ToString();
    }
}

sealed class LiteForm : Form
{
    static readonly Color Back = Color.FromArgb(15, 15, 15);
    static readonly Color Bar = Color.FromArgb(28, 28, 28);
    static readonly Color Hover = Color.FromArgb(45, 45, 45);
    static readonly Color Dim = Color.FromArgb(170, 170, 170);
    static readonly Color Accent = Color.FromArgb(255, 0, 51);

    const string GlyphPrev = "", GlyphNext = "", GlyphPlay = "", GlyphPause = "";
    const string GlyphVolume = "", GlyphQueue = "", GlyphSearch = "";

    static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YtMiniLite");
    static readonly string SettingsFile = Path.Combine(DataDir, "settings.txt");

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    readonly MediaPlayer player = new MediaPlayer();
    readonly float scale;
    readonly Font titleFont, subFont, iconFont;
    readonly TextBox search = new TextBox();
    readonly Label header = new Label();
    readonly ListBox list = new ListBox();
    readonly Label now = new Label();
    readonly TrackBar seek = new TrackBar();
    readonly Label time = new Label();
    readonly TrackBar volume = new TrackBar();
    readonly Button playButton, viewButton;
    readonly NotifyIcon tray = new NotifyIcon();
    readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer();

    List<Track> results = new List<Track>();
    readonly List<Track> queue = new List<Track>();
    int current = -1;
    readonly System.Windows.Forms.Timer typing = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer hover = new System.Windows.Forms.Timer();
    readonly TrackBar trayVolume = new TrackBar();
    readonly ToolStripLabel trayVolumeLabel = new ToolStripLabel();
    string lastQuery = "";
    int hoverIndex = -1;
    int playToken, queueVersion, failStreak, searchToken;
    volatile int sourceToken;   // playToken of the song currently loaded in the player, 0 = none
    string retryId;             // song already retried once with a fresh URL
    string skipNotice;          // shown in the header once the next song starts
    DateTime startedAt;
    bool showingQueue, extending;

    public LiteForm()
    {
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
        titleFont = new Font("Segoe UI", 10f);
        subFont = new Font("Segoe UI", 8.5f);
        iconFont = new Font("Segoe Fluent Icons", 12f);
        if (iconFont.Name != "Segoe Fluent Icons") iconFont = new Font("Segoe MDL2 Assets", 12f); // Windows 10

        Text = "YtMiniLite";
        Icon = LoadAppIcon();
        BackColor = Back;
        ForeColor = Color.White;
        Font = titleFont;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(S(300), S(300));

        // --- search ---
        var top = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(S(8), S(8), S(8), S(4)) };
        search.Dock = DockStyle.Top;
        search.BackColor = Bar;
        search.ForeColor = Color.White;
        search.BorderStyle = BorderStyle.FixedSingle;
        search.Font = new Font("Segoe UI", 11f);
        // Search as you type: sent after a 350 ms pause, not on every keystroke.
        typing.Interval = 350;
        typing.Tick += delegate { typing.Stop(); DoSearch(); };
        search.TextChanged += delegate { typing.Stop(); typing.Start(); };
        search.KeyDown += (s, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            typing.Stop();
            DoSearch();
        };
        top.Controls.Add(search);

        header.Dock = DockStyle.Top;
        header.Height = subFont.Height + S(8);
        header.Padding = new Padding(S(8), 0, S(8), 0);
        header.TextAlign = ContentAlignment.MiddleLeft;
        header.ForeColor = Dim;
        header.Font = subFont;
        header.AutoEllipsis = true;
        header.Text = Lang.T("Type a song or artist.");

        // --- list (results or queue) ---
        list.Dock = DockStyle.Fill;
        list.BorderStyle = BorderStyle.None;
        list.BackColor = Back;
        list.ForeColor = Color.White;
        list.DrawMode = DrawMode.OwnerDrawFixed;
        list.ItemHeight = titleFont.Height + subFont.Height + S(12);
        list.IntegralHeight = false;
        list.DrawItem += DrawTrack;
        list.Resize += delegate { list.Invalidate(); };
        list.DoubleClick += delegate { ActivateItem(list.SelectedIndex); };
        // Audio prep starts as soon as the mouse rests on a song or it is selected with the arrow keys,
        // so most of the ~2 s of yt-dlp work is done by the time you double-click.
        hover.Interval = 300;
        hover.Tick += delegate
        {
            hover.Stop();
            List<Track> shown = showingQueue ? queue : results;
            if (hoverIndex >= 0 && hoverIndex < shown.Count) GetUrl(shown[hoverIndex]);
        };
        list.MouseMove += (s, e) => HoverPrefetch(list.IndexFromPoint(e.Location));
        list.SelectedIndexChanged += delegate { HoverPrefetch(list.SelectedIndex); };
        list.KeyDown += (s, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            ActivateItem(list.SelectedIndex);
        };

        // --- player ---
        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 1, BackColor = Bar,
            Padding = new Padding(S(8), S(6), S(8), S(6))
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        now.Dock = DockStyle.Fill;
        now.Height = titleFont.Height + S(4);
        now.AutoEllipsis = true;
        now.UseMnemonic = false;
        now.Text = Lang.T("Nothing playing");
        bottom.Controls.Add(now);

        var seekRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        seekRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        seekRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        SetupSlider(seek, 1000);
        seek.Dock = DockStyle.Fill;
        seek.MouseUp += delegate
        {
            var session = player.PlaybackSession;
            if (session.NaturalDuration.TotalSeconds > 0)
                session.Position = TimeSpan.FromSeconds(session.NaturalDuration.TotalSeconds * seek.Value / 1000);
        };
        time.AutoSize = true;
        time.Anchor = AnchorStyles.Right;
        time.ForeColor = Dim;
        time.Font = subFont;
        time.Text = "0:00 / 0:00";
        seekRow.Controls.Add(seek, 0, 0);
        seekRow.Controls.Add(time, 1, 0);
        bottom.Controls.Add(seekRow);

        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        controls.Controls.Add(IconButton(GlyphPrev, delegate { Previous(); }));
        playButton = IconButton(GlyphPlay, delegate { TogglePlay(); });
        controls.Controls.Add(playButton);
        controls.Controls.Add(IconButton(GlyphNext, delegate { Next(); }));
        viewButton = IconButton(GlyphQueue, delegate { ShowList(!showingQueue); });
        controls.Controls.Add(viewButton);
        var volumeIcon = new Label { Text = GlyphVolume, Font = iconFont, ForeColor = Dim, AutoSize = true, Margin = new Padding(S(10), S(8), 0, 0) };
        controls.Controls.Add(volumeIcon);
        // Tray volume: a plain slider in the existing menu, no extra windows or timers.
        SetupSlider(trayVolume, 100);
        trayVolume.BackColor = SystemColors.Window;
        trayVolume.Width = S(150);
        trayVolume.ValueChanged += delegate
        {
            volume.Value = trayVolume.Value;
            trayVolumeLabel.Text = Lang.T("Volume: {0}%", trayVolume.Value);
        };
        SetupSlider(volume, 100);
        volume.Width = S(110);
        volume.Margin = new Padding(0, S(4), 0, 0);
        volume.ValueChanged += delegate
        {
            player.Volume = volume.Value / 100.0;
            trayVolume.Value = volume.Value; // setting the same value doesn't fire ValueChanged, so there is no loop
        };
        volume.Value = 70;
        controls.Controls.Add(volume);
        bottom.Controls.Add(controls);

        Controls.Add(list);
        Controls.Add(header);
        Controls.Add(top);
        Controls.Add(bottom);

        // --- MediaPlayer (WinRT); events arrive on background threads ---
        player.AudioCategory = MediaPlayerAudioCategory.Media;
        // Each event remembers which song it belongs to (captured on the event thread), so an "ended" or "failed"
        // from a song the user already left can't skip the song they just picked.
        player.MediaEnded += (s, e) =>
        {
            int source = sourceToken;
            Ui(delegate
            {
                if (source != sourceToken) return;
                // A song that "ends" a few seconds after starting never really played.
                if ((DateTime.UtcNow - startedAt).TotalSeconds < 5)
                {
                    PlaybackFailed(Lang.T("the song stopped right after it started"));
                    return;
                }
                retryId = null;
                Next();
            });
        };
        player.MediaFailed += (s, e) =>
        {
            int source = sourceToken;
            string message = e.ErrorMessage;
            Ui(delegate { if (source == sourceToken) PlaybackFailed(message); });
        };
        player.PlaybackSession.PlaybackStateChanged += (s, e) => Ui(UpdatePlayButton);
        player.CommandManager.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        player.CommandManager.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        player.CommandManager.NextReceived += (s, e) => { e.Handled = true; Ui(Next); };
        player.CommandManager.PreviousReceived += (s, e) => { e.Handled = true; Ui(Previous); };

        clock.Interval = 500;
        clock.Tick += delegate { UpdateProgress(); };
        clock.Start();

        tray.Icon = new Icon(Icon, SystemInformation.SmallIconSize); // the sharp small size, not a scaled-down large one
        tray.Text = Text;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleWindow(); };
        tray.Visible = true;

        RestoreSettings();
        ApplyLanguage();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        SetCueBanner();
        search.Focus();
    }

    void SetCueBanner()
    {
        SendMessage(search.Handle, 0x1501 /* EM_SETCUEBANNER */, (IntPtr)1, Lang.T("Search songs, artists…"));
    }

    void HoverPrefetch(int index)
    {
        if (index == hoverIndex) return;
        hoverIndex = index;
        hover.Stop();
        if (index >= 0) hover.Start();
    }

    // The tray menu is rebuilt when the language changes - simpler than tracking every item.
    void BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(Lang.T("Play / pause"), null, delegate { TogglePlay(); });
        menu.Items.Add(Lang.T("Next"), null, delegate { Next(); });
        menu.Items.Add(Lang.T("Previous"), null, delegate { Previous(); });
        menu.Items.Add(new ToolStripSeparator());
        trayVolumeLabel.Text = Lang.T("Volume: {0}%", trayVolume.Value);
        menu.Items.Add(trayVolumeLabel);
        menu.Items.Add(new ToolStripControlHost(trayVolume));
        menu.Items.Add(new ToolStripSeparator());
        var language = new ToolStripMenuItem(Lang.T("Language"));
        foreach (KeyValuePair<string, string> lang in Lang.Available())
        {
            string code = lang.Key;
            var item = new ToolStripMenuItem(lang.Value, null, delegate { Lang.Load(code); ApplyLanguage(); });
            item.Checked = code == Lang.Code;
            language.DropDownItems.Add(item);
        }
        menu.Items.Add(language);
        menu.Items.Add(Lang.T("Show / hide"), null, delegate { ToggleWindow(); });
        menu.Items.Add(Lang.T("Exit"), null, delegate { Close(); });

        ContextMenuStrip old = tray.ContextMenuStrip;
        tray.ContextMenuStrip = menu;
        if (old != null)
        {
            old.Items.Clear(); // so the shared slider and label aren't disposed along with the old menu
            old.Dispose();
        }
    }

    void ApplyLanguage()
    {
        BuildTrayMenu();
        if (IsHandleCreated) SetCueBanner();
        if (player.Source == null) now.Text = Lang.T("Nothing playing");
        if (list.Items.Count > 0) ShowList(showingQueue);
        else header.Text = Lang.T("Type a song or artist.");
    }

    int S(int px)
    {
        return (int)(px * scale);
    }

    void Ui(Action action)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(action);
    }

    // ---------- search and queue ----------

    async void DoSearch()
    {
        string query = search.Text.Trim();
        if (query.Length < 2) return;
        if (query == lastQuery)
        {
            ShowList(false); // same query (e.g. Enter after an automatic search) - just show the results again
            return;
        }
        lastQuery = query;
        int token = ++searchToken;
        header.Text = Lang.T("Searching “{0}”…", query);
        try
        {
            List<Track> found = await Task.Run(() => YtMusic.Search(query));
            if (token != searchToken) return; // this is the response to an older query
            results = found;
            ShowList(false);
        }
        catch (Exception ex)
        {
            if (token != searchToken) return;
            lastQuery = "";
            header.Text = Lang.T("Search error: {0}", ex.Message);
        }
    }

    void ShowList(bool queueView)
    {
        showingQueue = queueView;
        list.BeginUpdate();
        list.Items.Clear();
        list.Items.AddRange((queueView ? queue : results).ToArray());
        if (queueView && current >= 0 && current < list.Items.Count) list.TopIndex = Math.Max(0, current - 2);
        list.EndUpdate();
        hoverIndex = -1;
        header.Text = queueView
            ? Lang.T("Queue (radio) · {0} songs · double-click to play", queue.Count)
            : Lang.T("Results · {0} · double-click plays the song and starts a radio", results.Count);
        viewButton.Text = queueView ? GlyphSearch : GlyphQueue;
    }

    void ActivateItem(int index)
    {
        if (index < 0) return;
        failStreak = 0;
        retryId = null;
        if (showingQueue) PlayAt(index);
        else StartRadio(results[index]);
    }

    // Plays the song and fills the queue with its radio, like "Start radio" on YT Music.
    void StartRadio(Track track)
    {
        queueVersion++;
        queue.Clear();
        queue.Add(track);
        current = -1;
        ShowList(true);
        PlayAt(0);
        ExtendQueue(track);
    }

    async void ExtendQueue(Track from)
    {
        int version = queueVersion;
        extending = true;
        try
        {
            List<Track> more = await Task.Run(() => YtMusic.Radio(from.Id));
            if (version != queueVersion) return; // another radio was started in the meantime
            var ids = new HashSet<string>();
            foreach (Track t in queue) ids.Add(t.Id);
            var added = new List<Track>();
            foreach (Track t in more)
            {
                if (!ids.Add(t.Id)) continue;
                queue.Add(t);
                added.Add(t);
            }
            if (showingQueue)
            {
                // Append only: rebuilding the list would scroll it and could put a different song under the
                // mouse between the two clicks of a double-click.
                list.Items.AddRange(added.ToArray());
                header.Text = Lang.T("Queue (radio) · {0} songs · double-click to play", queue.Count);
            }
            Prefetch(current + 1);
        }
        catch (Exception ex)
        {
            header.Text = Lang.T("Radio unavailable: {0}", ex.Message);
        }
        finally
        {
            extending = false;
        }
    }

    // ---------- playback ----------

    Task<string> GetUrl(Track track)
    {
        if (track.Url == null || track.Url.IsFaulted || DateTime.UtcNow > track.UrlExpires)
        {
            track.UrlExpires = DateTime.UtcNow.AddHours(5); // YouTube URLs are valid for ~6h
            string id = track.Id;
            track.Url = Task.Run(() => YtMusic.ResolveAudioUrl(id));
        }
        return track.Url;
    }

    void Prefetch(int index)
    {
        if (index >= 0 && index < queue.Count) GetUrl(queue[index]);
    }

    async void PlayAt(int index)
    {
        if (index < 0 || index >= queue.Count) return;
        int token = ++playToken;
        // Stop the old song right away, so it can't end (and trigger "next") while this one is loading.
        sourceToken = 0;
        player.Source = null;
        current = index;
        Track track = queue[index];
        now.Text = track.Title + "  ·  " + track.Artist;
        tray.Text = Truncate(track.Title, 63);
        list.Invalidate();
        string url;
        try
        {
            if (track.Url == null || track.Url.IsFaulted || !track.Url.IsCompleted) header.Text = Lang.T("Loading “{0}”…", track.Title);
            url = await GetUrl(track);
        }
        catch (Exception ex)
        {
            if (token == playToken) header.Text = Lang.T("Can't play: {0}", ex.Message);
            return;
        }
        if (token != playToken) return; // the user picked another song in the meantime

        if ((DateTime.UtcNow - startedAt).TotalSeconds >= 5) failStreak = 0; // the previous song really played
        try
        {
            var item = new MediaPlaybackItem(MediaSource.CreateFromUri(new Uri(url)));
            MediaItemDisplayProperties props = item.GetDisplayProperties();
            props.Type = MediaPlaybackType.Music;
            props.MusicProperties.Title = track.Title;
            props.MusicProperties.Artist = track.Artist;
            props.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri("https://i.ytimg.com/vi/" + track.Id + "/hqdefault.jpg"));
            item.ApplyDisplayProperties(props);
            startedAt = DateTime.UtcNow;
            sourceToken = token;
            player.Source = item;
            player.Play();
        }
        catch (Exception ex)
        {
            PlaybackFailed(ex.Message);
            return;
        }
        if (skipNotice != null)
        {
            header.Text = skipNotice;
            skipNotice = null;
        }
        else if (showingQueue)
        {
            header.Text = Lang.T("Queue (radio) · {0} songs · double-click to play", queue.Count);
        }

        Prefetch(index + 1); // the next song is ready right away, no waiting for yt-dlp
        if (queue.Count - index <= 3 && !extending) ExtendQueue(queue[queue.Count - 1]);
    }

    // A song that won't play gets one more try with a fresh URL, then is skipped (and the header says so).
    // Stops after 3 skips in a row, so it doesn't burn through the whole queue in seconds.
    void PlaybackFailed(string message)
    {
        if (current < 0 || current >= queue.Count) return;
        Track track = queue[current];
        track.Url = null; // the URL may have expired - the next attempt fetches a new one
        if (retryId != track.Id)
        {
            retryId = track.Id;
            PlayAt(current);
            return;
        }
        if (++failStreak >= 3)
        {
            header.Text = Lang.T("Playback isn't working: {0}", message);
            return;
        }
        skipNotice = Lang.T("Skipped “{0}”: {1}", track.Title, message);
        Next();
    }

    void Next()
    {
        PlayAt(current + 1);
    }

    void Previous()
    {
        if (player.PlaybackSession.Position.TotalSeconds > 3) player.PlaybackSession.Position = TimeSpan.Zero;
        else PlayAt(current - 1);
    }

    void TogglePlay()
    {
        if (player.Source == null)
        {
            // Nothing has played yet: start the selected search result. (While a song loads, Source is also
            // null - then do nothing instead of starting something else.)
            if (current < 0 && !showingQueue && list.SelectedIndex >= 0) ActivateItem(list.SelectedIndex);
            return;
        }
        if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) player.Pause();
        else player.Play();
    }

    void UpdatePlayButton()
    {
        playButton.Text = player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing ? GlyphPause : GlyphPlay;
    }

    void UpdateProgress()
    {
        MediaPlaybackSession session = player.PlaybackSession;
        double total = session.NaturalDuration.TotalSeconds;
        if (total > 0 && Control.MouseButtons == MouseButtons.None) // don't move the slider while the user is dragging it
            seek.Value = (int)Math.Min(1000, session.Position.TotalSeconds * 1000 / total);
        time.Text = Clock(session.Position) + " / " + Clock(session.NaturalDuration);
    }

    static string Clock(TimeSpan t)
    {
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    static string Truncate(string s, int max)
    {
        return s.Length > max ? s.Substring(0, max) : s;
    }

    // ---------- drawing ----------

    void DrawTrack(object sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var track = (Track)list.Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        bool playing = showingQueue && e.Index == current;
        Rectangle r = e.Bounds;
        using (var bg = new SolidBrush(selected ? Hover : Back)) e.Graphics.FillRectangle(bg, r);

        int pad = S(10);
        int right = r.Right - pad;
        const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        if (track.Length.Length > 0)
        {
            int w = TextRenderer.MeasureText(track.Length, subFont).Width;
            TextRenderer.DrawText(e.Graphics, track.Length, subFont, new Rectangle(right - w, r.Top, w, r.Height), Dim,
                flags | TextFormatFlags.VerticalCenter);
            right -= w + pad;
        }
        var line = new Rectangle(r.Left + pad, r.Top + S(6), Math.Max(0, right - r.Left - pad), titleFont.Height);
        TextRenderer.DrawText(e.Graphics, track.Title, titleFont, line, playing ? Accent : Color.White, flags);
        line.Y += titleFont.Height;
        line.Height = subFont.Height;
        TextRenderer.DrawText(e.Graphics, track.Subtitle, subFont, line, Dim, flags);
    }

    Button IconButton(string glyph, EventHandler click)
    {
        var b = new Button
        {
            Text = glyph, Font = iconFont, ForeColor = Color.White, BackColor = Bar, FlatStyle = FlatStyle.Flat,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, TabStop = false, Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, S(4), 0), Padding = new Padding(S(4))
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Hover;
        b.Click += click;
        return b;
    }

    void SetupSlider(TrackBar slider, int max)
    {
        slider.AutoSize = false;
        slider.Height = S(26);
        slider.Maximum = max;
        slider.TickStyle = TickStyle.None;
        slider.BackColor = Bar;
        slider.TabStop = false;
        slider.LargeChange = 0; // clicking the track jumps to that position (MouseDown below) instead of stepping
        slider.MouseDown += (s, e) =>
        {
            int margin = S(12);
            double ratio = (e.X - margin) / (double)Math.Max(1, slider.Width - 2 * margin);
            slider.Value = (int)Math.Round(Math.Max(0, Math.Min(1, ratio)) * max);
        };
    }

    // ---------- window, tray, settings ----------

    // Minimizing hides the window to the tray and stops UI refresh; audio keeps playing.
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && Visible) SetHidden(true);
    }

    void ToggleWindow()
    {
        SetHidden(Visible && WindowState != FormWindowState.Minimized);
    }

    void SetHidden(bool hidden)
    {
        clock.Enabled = !hidden;
        if (hidden)
        {
            Hide();
            return;
        }
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        UpdateProgress();
    }

    void RestoreSettings()
    {
        var size = new Size(S(380), S(560));
        Rectangle area = Screen.PrimaryScreen.WorkingArea;
        Bounds = new Rectangle(area.Right - size.Width - 16, area.Bottom - size.Height - 16, size.Width, size.Height);
        try
        {
            string[] p = File.ReadAllText(SettingsFile).Trim().Split(' ');
            var saved = new Rectangle(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
            foreach (Screen screen in Screen.AllScreens)
            {
                if (!screen.WorkingArea.IntersectsWith(saved)) continue;
                Bounds = saved;
                break;
            }
            volume.Value = Math.Max(0, Math.Min(100, int.Parse(p[4])));
            if (p.Length > 5) Lang.Load(p[5]);
        }
        catch
        {
            // first run or a corrupt file - keep the defaults
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Rectangle r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(SettingsFile, string.Format("{0} {1} {2} {3} {4} {5}", r.X, r.Y, r.Width, r.Height, volume.Value, Lang.Code));
        }
        catch (IOException)
        {
        }
        tray.Visible = false;
        player.Dispose();
        base.OnFormClosing(e);
    }

    // The icon embedded at build time (/resource) - the same one Explorer shows for the .exe (/win32icon).
    static Icon LoadAppIcon()
    {
        using (Stream s = typeof(LiteForm).Assembly.GetManifestResourceStream("app.ico"))
            return new Icon(s);
    }
}
