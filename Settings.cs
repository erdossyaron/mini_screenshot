using Microsoft.Win32;

namespace Screen;

/// <summary>
/// Felhasználói beállítások a registryben (HKCU\Software\mini_screenshot).
/// Jelenleg: a mentési mappa.
/// </summary>
internal static class Settings
{
    private const string Key = @"Software\mini_screenshot";

    public static readonly string DefaultFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Képernyőképek");

    /// <summary>Nyitva van-e a mappaválasztó (közben a PrtSc és a hook Esc/Enter kezelése szünetel).</summary>
    public static bool DialogOpen { get; private set; }

    /// <summary>Ide mentődnek a képek a Másolás gombra.</summary>
    public static string SaveFolder
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(Key);
                if (k?.GetValue("SaveFolder") is string s && s.Length > 0) return s;
            }
            catch { /* olvasási hiba: alapértelmezett */ }
            return DefaultFolder;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            k.SetValue("SaveFolder", value);
        }
    }

    /// <summary>Másoláskor PNG-be is mentsen-e (a Másolás melletti kapcsoló). Alapból igen.</summary>
    public static bool SaveEnabled
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(Key);
                return k?.GetValue("SaveEnabled") is not int v || v != 0;
            }
            catch { return true; }
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            k.SetValue("SaveEnabled", value ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    // ---------- Automatikus indulás (HKCU\...\Run) ----------

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "ScreenPrtSc";

    public static bool IsAutostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunName) != null;
    }

    public static void SetAutostart(bool on, string exePath)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(RunName, $"\"{exePath}\"");
        else k.DeleteValue(RunName, false);
    }

    /// <summary>A telepítő már beállította az automatikus indulást (a felhasználó döntése érvényes).</summary>
    public static bool AutostartInitialized
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue("AutostartInitialized") != null;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            if (value) k.SetValue("AutostartInitialized", 1, RegistryValueKind.DWord);
            else k.DeleteValue("AutostartInitialized", false);
        }
    }

    /// <summary>
    /// Mappaválasztó ablak. Ha a felhasználó választ, elmenti és true-t ad vissza.
    /// Az owner a fagyasztott réteg, hogy a választó fölötte jelenjen meg.
    /// </summary>
    public static bool ChooseSaveFolder(IWin32Window? owner, string? title = null)
    {
        string current = SaveFolder;
        try { Directory.CreateDirectory(current); } catch { /* nem létező meghajtó: a választó a szülőnél nyílik */ }

        using var d = new FolderBrowserDialog
        {
            Description = title ?? "Hova mentse a képeket?",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = current,
            SelectedPath = current,
        };

        DialogOpen = true;
        try
        {
            var result = owner == null ? d.ShowDialog() : d.ShowDialog(owner);
            if (result != DialogResult.OK || string.IsNullOrWhiteSpace(d.SelectedPath)) return false;
            SaveFolder = d.SelectedPath;
            return true;
        }
        finally
        {
            DialogOpen = false;
        }
    }
}
