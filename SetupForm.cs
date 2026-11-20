namespace Screen;

/// <summary>
/// Telepítő varázsló. A letöltött exe indításakor jelenik meg:
/// mentési mappa, automatikus indulás, Telepítés / Frissítés gomb.
/// A méreteket a kijelző DPI-jével kézzel skálázzuk (a betűk pontban vannak, azok maguktól skálázódnak).
/// </summary>
internal sealed class SetupForm : Form
{
    private readonly TextBox _folder;
    private readonly CheckBox _autostart;

    public string Folder => _folder.Text.Trim();
    public bool Autostart => _autostart.Checked;

    public SetupForm(bool alreadyInstalled)
    {
        AutoScaleMode = AutoScaleMode.None;
        float s = DeviceDpi / 96f;
        int P(int v) => (int)Math.Round(v * s);
        Point At(int x, int y) => new(P(x), P(y));
        Size Sz(int w, int h) => new(P(w), P(h));

        Text = alreadyInstalled ? "mini_screenshot frissítése" : "mini_screenshot telepítése";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        Font = new Font("Segoe UI", 9.75f);
        BackColor = Color.White;
        ClientSize = Sz(520, 316);

        var title = new Label
        {
            Text = "Képernyőkép a PrtSc gombra",
            Font = new Font("Segoe UI Semibold", 15f),
            AutoSize = true,
            Location = At(24, 20),
        };

        var intro = new Label
        {
            Text = "A háttérben fut. PrtSc-re befagyasztja a képernyőt, vágás és jelölés után " +
                   "egy kattintással vágólapra másol és PNG-be ment.",
            ForeColor = Color.FromArgb(90, 90, 100),
            Location = At(24, 62),
            Size = Sz(472, 44),
        };

        var folderLabel = new Label
        {
            Text = "Hova mentse a képeket?",
            Font = new Font("Segoe UI Semibold", 9.75f),
            AutoSize = true,
            Location = At(24, 118),
        };

        _folder = new TextBox
        {
            Text = Settings.SaveFolder,
            Location = At(24, 144),
            Size = Sz(362, 28),
        };

        var browse = new Button
        {
            Text = "Tallózás…",
            Location = At(394, 142),
            Size = Sz(102, 30),
        };
        browse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog
            {
                Description = "Hova mentse a képeket?",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                InitialDirectory = Directory.Exists(Folder) ? Folder : Settings.DefaultFolder,
                SelectedPath = Folder,
            };
            if (d.ShowDialog(this) == DialogResult.OK) _folder.Text = d.SelectedPath;
        };

        _autostart = new CheckBox
        {
            Text = "Indítás a Windows-zal",
            Checked = true,
            AutoSize = true,
            Location = At(24, 188),
        };

        var where = new Label
        {
            Text = "Telepítés helye: %LOCALAPPDATA%\\Programs\\mini_screenshot",
            ForeColor = Color.FromArgb(140, 140, 150),
            Font = new Font("Segoe UI", 8.5f),
            AutoSize = true,
            Location = At(24, 220),
        };

        var install = new Button
        {
            Text = alreadyInstalled ? "Frissítés" : "Telepítés",
            DialogResult = DialogResult.OK,
            Location = At(290, 262),
            Size = Sz(116, 34),
            BackColor = Ui.Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 9.75f),
        };
        install.FlatAppearance.BorderSize = 0;

        var cancel = new Button
        {
            Text = "Mégse",
            DialogResult = DialogResult.Cancel,
            Location = At(414, 262),
            Size = Sz(82, 34),
        };

        AcceptButton = install;
        CancelButton = cancel;
        Controls.AddRange(new Control[] { title, intro, folderLabel, _folder, browse, _autostart, where, install, cancel });

        // Üres mappa nem fogadható el
        install.Click += (_, _) =>
        {
            if (Folder.Length == 0) { DialogResult = DialogResult.None; _folder.Focus(); }
        };

        Shown += (_, _) => { Activate(); install.Focus(); };
    }
}
