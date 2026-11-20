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
