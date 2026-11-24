using System.Drawing.Imaging;
using System.Text.Json;

namespace Screen;

/// <summary>Egy korábbi kép az előzményekben.</summary>
internal sealed record HistoryItem(string Dir, DateTime Time)
{
    public string ShotPath => Path.Combine(Dir, "kep.png");
    public string ThumbPath => Path.Combine(Dir, "elonezet.png");
    public string MetaPath => Path.Combine(Dir, "adatok.json");
}

/// <summary>
/// Előzmények: minden Másoláskor eltároljuk az eredeti (fagyasztott vagy hosszú) képet,
/// a vágást és a rajzolt objektumokat külön, így később újra megnyitható és tovább szerkeszthető.
/// Helye: %LOCALAPPDATA%\Programs\mini_screenshot\elozmenyek, a legutóbbi <see cref="Keep"/> darab.
/// </summary>
internal static class History
{
    public const int Keep = 15;     // ennyit tartunk meg a lemezen
    public const int Show = 10;     // ennyit mutatunk a legördülőben

    public static string Root => Path.Combine(Path.GetDirectoryName(Program.InstallPath)!, "elozmenyek");

    // ---- Mentett formátum (a Color és a Point közvetlenül nem szerializálható jól) ----
    private sealed class Meta
    {
        public DateTime Time { get; set; }
        public int SelX { get; set; }
        public int SelY { get; set; }
        public int SelW { get; set; }
        public int SelH { get; set; }
        public List<Ann> Anns { get; set; } = new();
    }

    private sealed class Ann
    {
        public Tool Kind { get; set; }
        public int Color { get; set; }
        public int Ax { get; set; }
        public int Ay { get; set; }
        public int Bx { get; set; }
        public int By { get; set; }
        public int[] Points { get; set; } = Array.Empty<int>();
        public bool ArrowStart { get; set; }
        public bool ArrowEnd { get; set; }
        public bool Inverse { get; set; }
        public string Text { get; set; } = "";
        public int FontSize { get; set; }
        public bool ManualSize { get; set; }
        public int? Background { get; set; }
        public int BackgroundOpacity { get; set; }
    }

    /// <summary>
    /// Mentés az előzményekbe. A bitmapeket a hívó adja át (másolatként), a lemezre írás háttérszálon fut.
    /// </summary>
    public static void Add(Bitmap shot, Rectangle sel, IEnumerable<Annotation> anns, Bitmap final)
    {
        var meta = new Meta
        {
            Time = DateTime.Now,
            SelX = sel.X, SelY = sel.Y, SelW = sel.Width, SelH = sel.Height,
            Anns = anns.Select(a => new Ann
            {
                Kind = a.Kind, Color = a.Color.ToArgb(),
                Ax = a.A.X, Ay = a.A.Y, Bx = a.B.X, By = a.B.Y,
                Points = a.Points.SelectMany(p => new[] { p.X, p.Y }).ToArray(),
                ArrowStart = a.ArrowStart, ArrowEnd = a.ArrowEnd, Inverse = a.Inverse,
                Text = a.Text, FontSize = a.FontSize, ManualSize = a.ManualSize,
                Background = a.Background.IsEmpty ? null : a.Background.ToArgb(),
                BackgroundOpacity = a.BackgroundOpacity,
            }).ToList(),
        };

        // Előnézet: a kész kép kicsinyítve (max. 320×200)
        double k = Math.Min(320.0 / final.Width, 200.0 / final.Height);
        k = Math.Min(1, k);
        var thumb = new Bitmap(Math.Max(1, (int)(final.Width * k)), Math.Max(1, (int)(final.Height * k)));
        using (var g = Graphics.FromImage(thumb))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(final, 0, 0, thumb.Width, thumb.Height);
        }
        final.Dispose();

        Task.Run(() =>
        {
            try
            {
                string dir = Path.Combine(Root, meta.Time.ToString("yyyyMMdd_HHmmss_fff"));
                Directory.CreateDirectory(dir);
                var item = new HistoryItem(dir, meta.Time);
                shot.Save(item.ShotPath, ImageFormat.Png);
                thumb.Save(item.ThumbPath, ImageFormat.Png);
                File.WriteAllText(item.MetaPath, JsonSerializer.Serialize(meta));
                Prune();
            }
            catch { /* az előzmény nem kritikus */ }
            finally
            {
                shot.Dispose();
                thumb.Dispose();
            }
        });
    }

    /// <summary>A legutóbbi képek, újabb elöl.</summary>
    public static List<HistoryItem> List(int max = Show)
    {
        try
        {
            if (!Directory.Exists(Root)) return new();
            return Directory.GetDirectories(Root)
                .Select(d => new HistoryItem(d, Directory.GetCreationTime(d)))
                .Where(i => File.Exists(i.MetaPath) && File.Exists(i.ShotPath) && File.Exists(i.ThumbPath))
                .OrderByDescending(i => Path.GetFileName(i.Dir))
                .Take(max)
                .Select(i => i with { Time = ReadTime(i) })
                .ToList();
        }
        catch { return new(); }
    }

    private static DateTime ReadTime(HistoryItem i)
    {
        try { return JsonSerializer.Deserialize<Meta>(File.ReadAllText(i.MetaPath))?.Time ?? i.Time; }
        catch { return i.Time; }
    }

    /// <summary>Előnézet betöltése (fájlzár nélkül).</summary>
    public static Bitmap? LoadThumb(HistoryItem i) => LoadBitmap(i.ThumbPath);

    /// <summary>Egy korábbi kép visszatöltése: eredeti kép, vágás, objektumok.</summary>
    public static (Bitmap shot, Rectangle sel, List<Annotation> anns)? Load(HistoryItem i)
    {
        try
        {
            var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(i.MetaPath));
            var shot = LoadBitmap(i.ShotPath);
            if (meta == null || shot == null) return null;
            var anns = meta.Anns.Select(a =>
            {
                var r = new Annotation
                {
                    Kind = a.Kind, Color = Color.FromArgb(a.Color),
                    A = new Point(a.Ax, a.Ay), B = new Point(a.Bx, a.By),
                    ArrowStart = a.ArrowStart, ArrowEnd = a.ArrowEnd, Inverse = a.Inverse,
                    Text = a.Text, FontSize = a.FontSize, ManualSize = a.ManualSize,
                    Background = a.Background is int bg ? Color.FromArgb(bg) : Color.Empty,
                    BackgroundOpacity = a.BackgroundOpacity,
                };
                for (int k = 0; k + 1 < a.Points.Length; k += 2) r.Points.Add(new Point(a.Points[k], a.Points[k + 1]));
                return r;
            }).ToList();
            return (shot, new Rectangle(meta.SelX, meta.SelY, meta.SelW, meta.SelH), anns);
        }
        catch { return null; }
    }

    /// <summary>Az összes korábbi kép törlése.</summary>
    public static void Clear()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
    }

    private static void Prune()
    {
        var dirs = Directory.GetDirectories(Root).OrderByDescending(Path.GetFileName).Skip(Keep);
        foreach (var d in dirs) { try { Directory.Delete(d, true); } catch { } }
    }

    /// <summary>Bitmap betöltése úgy, hogy a fájl ne maradjon zárolva.</summary>
    private static Bitmap? LoadBitmap(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var tmp = Image.FromStream(fs);
            return new Bitmap(tmp);
        }
        catch { return null; }
    }
}
