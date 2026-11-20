using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace Screen;

internal static class Program
{
    /// <summary>Állandó telepítési hely: %LOCALAPPDATA%\Programs\mini_screenshot\Screen.exe</summary>
    public static string InstallPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "mini_screenshot", "Screen.exe");

    /// <summary>
    /// Egyfájlos (kiadott) exe fut-e. A fejlesztői build (bin\Release) nem telepíti magát,
    /// és nem kapcsol be automatikus indulást.
    /// </summary>
#pragma warning disable IL3000 // szándékos: egyfájlos exe-ben üres a Location, ebből ismerjük fel
    public static bool IsPublishedExe => string.IsNullOrEmpty(typeof(Program).Assembly.Location);
#pragma warning restore IL3000

    private const string MutexName = "Screen.PrtSc.SingleInstance";
    public const string ShowEventName = "mini_screenshot.Show";   // "már fut" jelzés a futó példánynak
    public const string QuitEventName = "mini_screenshot.Quit";   // kilépés kérése (frissítéskor)

    private static bool RunsFromInstallPath => string.Equals(
        Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(InstallPath), StringComparison.OrdinalIgnoreCase);

    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Letöltött exe (nem a telepítési helyről fut): telepítő varázsló
        if (IsPublishedExe && !RunsFromInstallPath)
        {
            RunSetup();
            return;
        }

        // Egyszerre csak egy példány fusson. Ha már fut, az mutassa meg magát (értesítés).
        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            Signal(ShowEventName);
            return;
        }

        Application.Run(new TrayContext(showWelcome: args.Contains("--welcome")));
    }

    /// <summary>
    /// Varázsló → futó példány leállítása → exe másolása az állandó helyre →
    /// beállítások mentése → indítás az új helyről.
    /// </summary>
    private static void RunSetup()
    {
        using var form = new SetupForm(alreadyInstalled: File.Exists(InstallPath));
        if (form.ShowDialog() != DialogResult.OK) return;

        try
        {
            StopRunningInstance();
            Directory.CreateDirectory(Path.GetDirectoryName(InstallPath)!);
            File.Copy(Environment.ProcessPath!, InstallPath, overwrite: true);

            Settings.SaveFolder = form.Folder;
            try { Directory.CreateDirectory(form.Folder); } catch { /* mentéskor újra próbálja */ }
            Settings.SetAutostart(form.Autostart, InstallPath);
            Settings.AutostartInitialized = true;

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(InstallPath, "--welcome") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("A telepítés nem sikerült:\n\n" + ex.Message, "mini_screenshot",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>A futó példányt udvariasan leállítja; ha nem reagál (régi verzió), kilövi.</summary>
    private static void StopRunningInstance()
    {
        Signal(QuitEventName);
        using var m = new Mutex(false, MutexName);
        bool released = false;
        try { released = m.WaitOne(3000); }
        catch (AbandonedMutexException) { released = true; }
        if (released) { m.ReleaseMutex(); return; }

        foreach (var p in System.Diagnostics.Process.GetProcessesByName("Screen"))
        {
            try
            {
                if (p.Id != Environment.ProcessId) { p.Kill(); p.WaitForExit(3000); }
            }
            catch { /* nincs jogosultság vagy már kilépett */ }
        }
    }

    private static void Signal(string name)
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(name);
            ev.Set();
        }
        catch { /* nincs futó példány, vagy régi verzió fut */ }
    }
}

/// <summary>Háttérben futó alkalmazás: tálcaikon + billentyűhook.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly KeyboardHook _hook;
    private readonly Control _ui = new();          // UI-szálra visszaküldéshez
    private readonly List<FreezeForm> _overlays = new();

    public static string SaveFolder => Settings.SaveFolder;

    private readonly EventWaitHandle _showEvent = new(false, EventResetMode.AutoReset, Program.ShowEventName);
    private readonly EventWaitHandle _quitEvent = new(false, EventResetMode.AutoReset, Program.QuitEventName);

    public TrayContext(bool showWelcome)
    {
        _ui.CreateControl();
        InitAutostart();
        TryCreateFolder(SaveFolder);

        var autostart = new ToolStripMenuItem("Indítás a Windows-zal") { Checked = Settings.IsAutostart(), CheckOnClick = true };
        autostart.CheckedChanged += (_, _) => Settings.SetAutostart(autostart.Checked, Application.ExecutablePath);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Kép készítése", null, (_, _) => Trigger());
        menu.Items.Add("Mappa megnyitása", null, (_, _) => OpenFolder());
        menu.Items.Add("Mentési mappa…", null, (_, _) => Settings.ChooseSaveFolder(null));
        menu.Items.Add(autostart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Kilépés", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "Screen – PrtSc",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Trigger(); };

        _hook = new KeyboardHook(() => _ui.BeginInvoke(Trigger), OnGlobalKey);

        // Telepítés után: üdvözlő értesítés
        if (showWelcome) ShowRunningTip("A mini_screenshot fut a háttérben");

        // Másik indítás jelzése ("már fut") és kilépés kérése (frissítéskor)
        ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => _ui.BeginInvoke(() => ShowRunningTip("A mini_screenshot már fut")), null, -1, false);
        ThreadPool.RegisterWaitForSingleObject(_quitEvent,
            (_, _) => _ui.BeginInvoke(ExitThread), null, -1, true);
    }

    private void ShowRunningTip(string title) =>
        _tray.ShowBalloonTip(6000, title,
            "Nyomd meg a PrtSc gombot a képernyőkép készítéséhez. A tálcaikonon jobb gombbal érheted el a beállításokat.",
            ToolTipIcon.Info);

    /// <summary>
    /// Automatikus indulás, ha nem a telepítő állította be (pl. kézzel másolt exe):
    /// első futáskor bekapcsolja. Ha be van kapcsolva, a bejegyzés az aktuális exe-re mutat.
    /// </summary>
    private static void InitAutostart()
    {
        if (!Program.IsPublishedExe) return;
        try
        {
            if (!Settings.AutostartInitialized)
            {
                Settings.SetAutostart(true, Application.ExecutablePath);
                Settings.AutostartInitialized = true;
            }
            else if (Settings.IsAutostart())
            {
                Settings.SetAutostart(true, Application.ExecutablePath);   // útvonal frissítése
            }
        }
        catch { /* registry hiba: az app enélkül is működik */ }
    }

    /// <summary>
    /// PrtSc lenyomás: minden kijelzőt lefotóz és befagyaszt.
    /// Mentés és vágólap csak a Másolás gombra történik.
    /// </summary>
    private void Trigger()
    {
        if (Settings.DialogOpen) return;                         // mappaválasztó nyitva: ne zavarjuk
        if (_overlays.Count > 0) { CloseOverlays(); return; }   // második PrtSc = mégse

        // Előbb az összes kijelzőt fotózzuk, csak utána jelenik meg bármi
        var shots = System.Windows.Forms.Screen.AllScreens.Select(s => (s, Capture(s.Bounds))).ToList();
        foreach (var (screen, shot) in shots)
        {
            var f = new FreezeForm(screen, shot);
            f.Confirmed += OnConfirmed;
            f.Cancelled += CloseOverlays;
            _overlays.Add(f);
            f.Show();
        }

        // A billentyűzet (Esc, Enter) annál a kijelzőnél legyen, ahol az egér van
        var under = _overlays.FirstOrDefault(o => o.Bounds.Contains(Cursor.Position)) ?? _overlays[0];
        ForceForeground(under);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);

    /// <summary>
    /// A Windows nem engedi, hogy egy háttéralkalmazás csak úgy elvegye a fókuszt.
    /// Ezért ideiglenesen az előtérben lévő ablak szálához csatlakozunk, így átadható a fókusz.
    /// </summary>
    private static void ForceForeground(Form f)
    {
        uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        uint me = GetCurrentThreadId();
        bool attached = fg != 0 && fg != me && AttachThreadInput(me, fg, true);
        try
        {
            BringWindowToTop(f.Handle);
            SetForegroundWindow(f.Handle);
            f.Activate();
            f.Focus();
        }
        finally
        {
            if (attached) AttachThreadInput(me, fg, false);
        }
    }

    /// <summary>
    /// Billentyűk a hookból, amíg a fagyasztott réteg nyitva van. A hook a UI-szálon fut.
    /// Esc mindig bezár, Enter ment – akkor is, ha a réteg nem kapta meg a fókuszt
    /// (pl. RDP-ablak vagy más alkalmazás tartja az előteret).
    /// </summary>
    private bool OnGlobalKey(int vk)
    {
        if (_overlays.Count == 0 || Settings.DialogOpen) return false;   // a mappaválasztóé az Esc/Enter
        // Gépelés közben az Esc és az Enter a szövegé (Esc: gépelés vége, Enter: új sor)
        if (_overlays.Any(o => o.IsEditingText)) return false;
        const int VK_RETURN = 0x0D, VK_ESCAPE = 0x1B;

        if (vk == VK_ESCAPE)
        {
            _ui.BeginInvoke(CloseOverlays);
            return true;
        }
        if (vk == VK_RETURN)
        {
            var target = _overlays.FirstOrDefault(o => o.Bounds.Contains(Cursor.Position)) ?? _overlays[0];
            _ui.BeginInvoke(() => { if (_overlays.Contains(target)) OnConfirmed(target); });
            return true;
        }
        return false;
    }

    private void OnConfirmed(FreezeForm source)
    {
        var crop = source.Crop();
        var screen = source.TargetScreen;
        CloseOverlays();

        TryCreateFolder(SaveFolder);   // ha közben törölték
        string file = Path.Combine(SaveFolder, $"kep_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.png");
        crop.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        CopyToClipboard(crop);
        ToastForm.Show(screen, crop, file);   // a toast veszi át a bitmapet
    }

    private void CloseOverlays()
    {
        foreach (var o in _overlays.ToList()) { o.Close(); o.Dispose(); }
        _overlays.Clear();
    }

    private static Bitmap Capture(Rectangle r)
    {
        var bmp = new Bitmap(r.Width, r.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Location, Point.Empty, r.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    private static void CopyToClipboard(Bitmap bmp)
    {
        // Sima bitmap + PNG adatfolyam, hogy minden alkalmazás jól kapja meg
        var data = new DataObject();
        data.SetImage(bmp);
        var png = new MemoryStream();
        bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        data.SetData("PNG", false, png);
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetDataObject(data, true); return; }
            catch (System.Runtime.InteropServices.ExternalException) { Thread.Sleep(50); }
        }
    }

    private static void TryCreateFolder(string path)
    {
        try { Directory.CreateDirectory(path); } catch { /* pl. leválasztott meghajtó: mentéskor derül ki */ }
    }

    private static void OpenFolder() =>
        System.Diagnostics.Process.Start("explorer.exe", $"\"{SaveFolder}\"");

    /// <summary>Minimalista tálcaikon: lekerekített keret, közepén pont.</summary>
    private static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.White, 3f);
            using var path = Ui.Rounded(new RectangleF(3, 6, 26, 20), 6);
            g.DrawPath(pen, path);
            g.FillEllipse(Brushes.White, 12, 12, 8, 8);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void ExitThreadCore()
    {
        _hook.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _showEvent.Dispose();
        _quitEvent.Dispose();
        base.ExitThreadCore();
    }
}
