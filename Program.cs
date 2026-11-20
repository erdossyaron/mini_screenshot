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

    [STAThread]
    private static void Main()
    {
        // Egyszerre csak egy példány fusson
        using var mutex = new Mutex(true, "Screen.PrtSc.SingleInstance", out bool first);
        if (!first) return;

        // Letöltött helyről (pl. Letöltések) indítva: átmásolja magát az állandó helyre, és onnan indul újra.
        // Így ha a letöltött fájlt később törlik, az automatikus indulás akkor is működik.
        if (IsPublishedExe && TrySelfInstall())
        {
            mutex.ReleaseMutex();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(InstallPath) { UseShellExecute = true });
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayContext());
    }

    /// <summary>true, ha átmásolta magát és az új helyről kell indulni.</summary>
    private static bool TrySelfInstall()
    {
        try
        {
            string current = Path.GetFullPath(Environment.ProcessPath!);
            if (string.Equals(current, Path.GetFullPath(InstallPath), StringComparison.OrdinalIgnoreCase)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(InstallPath)!);
            File.Copy(current, InstallPath, overwrite: true);   // újabb verzió letöltésekor frissít
            return true;
        }
        catch
        {
            return false;   // ha nem sikerül, a helyén fut tovább
        }
    }
}

/// <summary>Háttérben futó alkalmazás: tálcaikon + billentyűhook.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "ScreenPrtSc";

    private readonly NotifyIcon _tray;
    private readonly KeyboardHook _hook;
    private readonly Control _ui = new();          // UI-szálra visszaküldéshez
    private readonly List<FreezeForm> _overlays = new();

    public static string SaveFolder => Settings.SaveFolder;

    public TrayContext()
    {
        _ui.CreateControl();
        bool firstRun = InitAutostart();
        // Telepítéskor (első indítás): a felhasználó megadja, hova mentődjenek a képek
        if (firstRun)
            Settings.ChooseSaveFolder(null,
                "Hova mentse a képeket? Később a Másolás gomb melletti mappa ikonnal módosítható.");
        TryCreateFolder(SaveFolder);

        var autostart = new ToolStripMenuItem("Indítás a Windows-zal") { Checked = IsAutostart(), CheckOnClick = true };
        autostart.CheckedChanged += (_, _) => SetAutostart(autostart.Checked);

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

        // Első indításkor jelezzük, hogy az app a háttérben fut
        if (firstRun)
            _tray.ShowBalloonTip(6000, "Screen fut a háttérben",
                "Nyomd meg a PrtSc gombot a képernyőkép készítéséhez. A Windows indulásakor automatikusan elindul.",
                ToolTipIcon.Info);
    }

    private const string SettingsKey = @"Software\mini_screenshot";

    /// <summary>
    /// Automatikus indulás: első futáskor bekapcsolja (csak a kiadott exe-nél).
    /// Ha a felhasználó később kikapcsolja a menüben, nem kapcsoljuk vissza.
    /// Ha be van kapcsolva, a bejegyzés mindig az aktuális exe-re mutat.
    /// Visszaadja, hogy ez volt-e az első indítás.
    /// </summary>
    private static bool InitAutostart()
    {
        if (!Program.IsPublishedExe) return false;
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(SettingsKey);
            bool first = k.GetValue("AutostartInitialized") == null;
            if (first)
            {
                SetAutostart(true);
                k.SetValue("AutostartInitialized", 1, RegistryValueKind.DWord);
            }
            else if (IsAutostart())
            {
                SetAutostart(true);   // útvonal frissítése, ha az exe máshova került
            }
            return first;
        }
        catch
        {
            return false;
        }
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

    private static bool IsAutostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(AppName) != null;
    }

    private static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true)!;
        if (on) k.SetValue(AppName, $"\"{Application.ExecutablePath}\"");
        else k.DeleteValue(AppName, false);
    }

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
        base.ExitThreadCore();
    }
}
