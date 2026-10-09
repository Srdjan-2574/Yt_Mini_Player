// YtMiniPlayer - a small YouTube Music player: WebView2 (the Edge runtime already on Windows) + uBlock Origin.
// Written in C# 5 so it compiles with the .NET Framework csc.exe, no .NET SDK required.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

static class Program
{
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main()
    {
        bool first;
        using (new Mutex(true, "YtMiniPlayer_SingleInstance", out first))
        {
            if (!first) return;
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new PlayerForm());
        }
    }
}

sealed class PlayerForm : Form
{
    const string StartUrl = "https://music.youtube.com/";
    // Turns off Chromium background services the player doesn't need. --disable-gpu removes the GPU process (~130 MB)
    // and the V8 flags shrink the JS heap; measured: ~490 -> ~355 MB visible, ~145 -> ~128 MB in the tray.
    const string BrowserArgs = "--disable-background-networking --disable-component-update " +
                               "--disable-domain-reliability --disable-sync --no-pings --disable-gpu " +
                               "\"--js-flags=--optimize-for-size --max-semi-space-size=1\"";

    // Audio first: show the album art instead of the music video and stream video at 144p
    // (audio stays Opus itag 251, same quality).
    const string JsAudioOnly = @"(function () {
        if (location.hostname !== 'music.youtube.com' || window !== window.top) return;
        document.addEventListener('DOMContentLoaded', function () {
            var s = document.createElement('style');
            s.textContent = '#song-video{opacity:0!important}#song-image{display:block!important}';
            document.head.appendChild(s);
        });
        document.addEventListener('playing', function () {
            var p = document.getElementById('movie_player');
            if (p && p.setPlaybackQualityRange && p.getPlaybackQuality() !== 'tiny') p.setPlaybackQualityRange('tiny', 'tiny');
        }, true);
    })();";

    const string JsPlayPause = "(function(){var v=document.querySelector('video');if(v){if(v.paused)v.play();else v.pause();}})()";
    const string JsNext = "(function(){var b=document.querySelector('ytmusic-player-bar .next-button');if(b)b.click();})()";
    const string JsPrev = "(function(){var b=document.querySelector('ytmusic-player-bar .previous-button');if(b)b.click();})()";

    static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory;
    static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YtMiniPlayer");
    static readonly string WindowFile = Path.Combine(DataDir, "window.txt");

    readonly WebView2 web = new WebView2();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ToolStripMenuItem topItem = new ToolStripMenuItem("Always on top");

    public PlayerForm()
    {
        Text = "YtMiniPlayer";
        Icon = MakeIcon();
        BackColor = Color.FromArgb(3, 3, 3);
        StartPosition = FormStartPosition.Manual;
        RestoreWindow();

        web.Dock = DockStyle.Fill;
        web.DefaultBackgroundColor = BackColor;
        Controls.Add(web);

        topItem.Checked = TopMost;
        topItem.Click += delegate { TopMost = !TopMost; topItem.Checked = TopMost; };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Play / pause", null, delegate { RunJs(JsPlayPause); });
        menu.Items.Add("Next", null, delegate { RunJs(JsNext); });
        menu.Items.Add("Previous", null, delegate { RunJs(JsPrev); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Show / hide", null, delegate { ToggleWindow(); });
        menu.Items.Add(topItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, delegate { Close(); });

        tray.Icon = Icon;
        tray.Text = Text;
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleWindow(); };
        tray.Visible = true;
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            await InitWebView();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, "Microsoft Edge WebView2 Runtime was not found.\n" +
                "Download it from https://developer.microsoft.com/microsoft-edge/webview2/",
                "YtMiniPlayer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Startup failed:\n" + ex.Message,
                "YtMiniPlayer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    async Task InitWebView()
    {
        var options = new CoreWebView2EnvironmentOptions(BrowserArgs);
        options.AreBrowserExtensionsEnabled = true;
        var env = await CoreWebView2Environment.CreateAsync(null, DataDir, options);
        await web.EnsureCoreWebView2Async(env);

        var core = web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.DocumentTitleChanged += delegate
        {
            string title = core.DocumentTitle;
            Text = title;
            tray.Text = title.Length > 63 ? title.Substring(0, 63) : title; // NotifyIcon limit
        };
        core.NewWindowRequested += OnNewWindowRequested;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(JsAudioOnly);

        // The extension must be loaded before the first navigation so it blocks from the start.
        await LoadUBlock(core.Profile);
        core.Navigate(StartUrl);
    }

    // Installs ublock/ from the app folder into the profile; reinstalls only when the version or the app folder
    // changes, so uBO doesn't recompile its filter lists on every start.
    static async Task LoadUBlock(CoreWebView2Profile profile)
    {
        string dir = Path.Combine(AppDir, "ublock");
        string manifest = Path.Combine(dir, "manifest.json");
        if (!File.Exists(manifest)) return;

        string version = Regex.Match(File.ReadAllText(manifest), "\"version\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
        string marker = Path.Combine(DataDir, "ublock.txt");
        string[] saved = File.Exists(marker) ? File.ReadAllText(marker).Split('|') : new string[0];

        if (saved.Length >= 2)
        {
            foreach (var ext in await profile.GetBrowserExtensionsAsync())
            {
                if (ext.Id != saved[1]) continue;
                if (saved[0] == version && saved.Length == 3 && saved[2] == dir)
                {
                    if (!ext.IsEnabled) await ext.EnableAsync(true);
                    return;
                }
                await ext.RemoveAsync();
                break;
            }
        }

        var added = await profile.AddBrowserExtensionAsync(dir);
        File.WriteAllText(marker, version + "|" + added.Id + "|" + dir);
    }

    // Popups: YouTube/Google (e.g. sign-in) stay in the player, everything else opens in the default browser.
    void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        Uri uri;
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out uri)) return;
        if (uri.Host.EndsWith("youtube.com") || uri.Host.EndsWith("google.com"))
            web.CoreWebView2.Navigate(e.Uri);
        else if (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            Process.Start(e.Uri);
    }

    void RunJs(string js)
    {
        if (web.CoreWebView2 != null) web.CoreWebView2.ExecuteScriptAsync(js);
    }

    // Minimizing hides the window to the tray; a hidden WebView2 stops rendering while the music keeps playing.
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
        if (hidden)
        {
            Hide();
        }
        else
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }
        if (web.CoreWebView2 != null)
            web.CoreWebView2.MemoryUsageTargetLevel = hidden
                ? CoreWebView2MemoryUsageTargetLevel.Low
                : CoreWebView2MemoryUsageTargetLevel.Normal;
    }

    void RestoreWindow()
    {
        float scale;
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f; // DeviceDpi reports 96 until the form has a handle
        var size = new Size((int)(420 * scale), (int)(680 * scale));
        Rectangle area = Screen.PrimaryScreen.WorkingArea;
        Bounds = new Rectangle(area.Right - size.Width - 16, area.Bottom - size.Height - 16, size.Width, size.Height);
        try
        {
            string[] p = File.ReadAllText(WindowFile).Split(' ');
            var saved = new Rectangle(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
            foreach (Screen screen in Screen.AllScreens)
            {
                if (!screen.WorkingArea.IntersectsWith(saved)) continue;
                Bounds = saved;
                break;
            }
            TopMost = p[4] == "1";
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
            File.WriteAllText(WindowFile, string.Format("{0} {1} {2} {3} {4}", r.X, r.Y, r.Width, r.Height, TopMost ? 1 : 0));
        }
        catch (IOException)
        {
        }
        tray.Visible = false;
        base.OnFormClosing(e);
    }

    static Icon MakeIcon()
    {
        using (var bmp = new Bitmap(64, 64))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var red = new SolidBrush(Color.FromArgb(255, 0, 51)))
                g.FillEllipse(red, 2, 2, 60, 60);
            g.FillPolygon(Brushes.White, new[] { new Point(25, 18), new Point(25, 46), new Point(47, 32) });
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
}
