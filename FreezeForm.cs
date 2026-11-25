using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Screen;

/// <summary>
/// Kijelzőnként egy teljes képernyős réteg, ami a lefotózott (befagyasztott) képet mutatja.
///
/// Vágás eszköz (alapértelmezett): bármelyik sarok vagy él húzásával azonnal vág,
/// a belseje mozgatható, kívül húzva új kijelölés rajzolható, sima kattintásra
/// a legközelebbi sarok odaugrik (két kattintás = két sarokpont).
///
/// Rajzeszközök: elmosás, ovál, téglalap, vonal (nyíllal), ceruza, kéz (kijelölés, mozgatás, törlés).
/// Semmi nem mentődik, amíg a "Másolás" gombra (vagy Enterre) nem kattintasz.
/// </summary>
internal sealed class FreezeForm : Form
{
    public event Action<FreezeForm>? Confirmed;
    public event Action? Cancelled;

    public System.Windows.Forms.Screen TargetScreen { get; }

    private enum Hit { None, Move, N, S, E, W, NE, NW, SE, SW, New }

    /// <summary>Eszköztár-gomb. A Plain gomb háttere nem változik kijelöléskor.</summary>
    private sealed record UiButton(string Id, Rectangle R, Action Click, Func<bool> Active,
        Action<Graphics, Rectangle, Color> Icon, bool Plain = false);

    private static readonly Color Red = Color.FromArgb(239, 68, 68);
    private static readonly Color White = Color.White;
    private static readonly Color Black = Color.FromArgb(17, 17, 17);
    private static readonly Tool[] Tools = { Tool.Crop, Tool.Blur, Tool.Oval, Tool.Rect, Tool.Line, Tool.Pen, Tool.Text, Tool.Hand };
    private static readonly Color[] Palette = { Red, White, Black };

    /// <summary>A legnépszerűbb képarányok a méretkijelző menüjében.</summary>
    private static readonly (string Name, double Value)[] Ratios =
    {
        ("16:9", 16 / 9.0), ("16:10", 16 / 10.0), ("4:3", 4 / 3.0), ("3:2", 3 / 2.0),
        ("1:1", 1.0), ("21:9", 21 / 9.0), ("9:16", 9 / 16.0), ("4:5", 4 / 5.0),
    };

    private readonly float S;
    private readonly Bitmap _shot;   // eredeti fagyasztott kép
    private readonly Bitmap? _dim;   // sötétített változat a vágáson kívüli részhez (hosszú képnél nincs)
    private Bitmap? _blurSmall;      // erősen kicsinyített kép az elmosáshoz, első használatkor készül
    private int _blurF;              // kicsinyítés mértéke
    private readonly Rectangle _full;

    // Hosszú (görgetéssel összefűzött) kép: a dokumentum magasabb a képernyőnél, görgethető nézet
    private readonly bool _longDoc;
    private readonly int _centerX;   // keskeny kép: vízszintes középre igazítás
    private int _viewX;              // széles kép (pásztázott felvétel): vízszintes görgetés
    private Point? _panFrom;         // középső gombos húzás: a nézet mozgatása
    private int _viewY;              // nézet görgetése (dokumentum-sor a képernyő tetején)
    private readonly System.Windows.Forms.Timer _edgeTimer = new() { Interval = 30 };

    /// <summary>"Hosszú kép" gomb: az app görgetve rögzíti a kijelölés alatti tartalmat.</summary>
    public event Action<FreezeForm>? LongCaptureRequested;
    public bool IsLongDoc => _longDoc;
    public Rectangle Selection => _sel;
    public Bitmap CopyShot() => new(_shot);
    private Rectangle _sel;

    // Vágás húzása
    private Hit _drag = Hit.None;
    private Point _start;
    private Rectangle _startSel;

    // Eszközök és jelölések
    private Tool _tool = Tool.Crop;
    private Color _color = Red;
    private bool _arrowStart, _arrowEnd = true;
    private readonly List<Annotation> _anns = new();
    private Annotation? _drawing, _selected;
    private bool _movingAnn, _resizingAnn;
    private bool _pendingClick;   // rajzeszköznél lenyomva, de még nem dőlt el: kattintás vagy húzás
    private bool _pendingDeselect;
    private bool _keepOnClick;       // a kéz eszköz épp most jelölt ki: a felengedés ne vegye le a fókuszt   // kijelölés mellett, az objektumon kívül lenyomva: felengedéskor fókuszvesztés

    // Szöveg eszköz
    private static readonly int[] FontSizes = { 12, 14, 16, 18, 20, 24, 28, 32, 40, 48, 64, 80 };
    private int _fontSize = 20;
    private Annotation? _editing;     // éppen gépelt szöveg
    private bool _caretOn = true;
    private bool _suppressUp;         // dupla kattintás utáni felengedést nem dolgozzuk fel
    private readonly System.Windows.Forms.Timer _caretTimer = new() { Interval = 530 };
    private Rectangle _fontLabelRect, _opacityLabelRect;
    private readonly List<int> _textSepX = new();
    private int _textSepTop, _textSepBottom;

    // Szövegdoboz háttere: szín (Empty = nincs) és fedés százalékban
    private static readonly Color[] TextBackgrounds = { Color.Empty, Color.White, Color.FromArgb(17, 17, 17), Color.FromArgb(255, 214, 10) };
    private Color _textBg = Color.Empty;
    private int _textBgOpacity = 80;

    private bool _blurInverse;   // elmosás eszköz: inverz (fókusz) mód

    // Tiltott szöveg színe (a TextRenderer nem kezel átlátszóságot, ezért tömör szürke)
    private static readonly Color DisabledText = Color.FromArgb(78, 78, 86);
    private static readonly Graphics Measurer = Graphics.FromImage(new Bitmap(1, 1));
    private float TextPad => 4 * S;

    public bool IsEditingText => _editing != null;

    // Képarány-zár a vágáshoz (null = szabad méretezés)
    private double? _ratio;
    private string? _ratioName;
    private bool _ratioMenuOpen;
    private Point _last;

    // Eszköztár
    private readonly List<UiButton> _buttons = new();
    private readonly List<Rectangle> _panels = new();
    private Rectangle _sizeRect;
    private string _sizeText = "";
    private int _sepX, _sepTop, _sepBottom;
    private string? _hoverId, _pressId;

    private float _flash = 1f, _ui;  // animációs állapot

    private float StrokeW => Math.Max(2f, 2.2f * S);

    /// <param name="longDoc">true: görgetéssel összefűzött, a képernyőnél magasabb kép (görgethető szerkesztő).</param>
    public FreezeForm(System.Windows.Forms.Screen screen, Bitmap shot, bool longDoc = false)
    {
        TargetScreen = screen;
        S = Ui.ScaleFor(screen.Bounds);
        _shot = shot;
        _full = new Rectangle(Point.Empty, shot.Size);
        _sel = _full;
        _longDoc = longDoc;

        if (longDoc)
        {
            _centerX = Math.Max(0, (screen.Bounds.Width - shot.Width) / 2);
        }
        else
        {
            _dim = new Bitmap(shot.Width, shot.Height, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(_dim);
            using var b = new SolidBrush(Color.FromArgb(150, 8, 8, 12));
            g.DrawImageUnscaled(shot, 0, 0);
            g.FillRectangle(b, _full);
        }

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;
        DoubleBuffered = true;
        KeyPreview = true;
        Bounds = screen.Bounds;
        Cursor = Cursors.Cross;

        // Villogó kurzor gépelés közben
        _caretTimer.Tick += (_, _) => { if (_editing != null) { _caretOn = !_caretOn; Invalidate(); } };
        _caretTimer.Start();

        // Hosszú képnél: görgetés a képernyő szélén húzás közben
        if (longDoc)
        {
            _edgeTimer.Tick += (_, _) => EdgeScrollTick();
            _edgeTimer.Start();
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;   // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e) => e.Cancel = true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Bounds = TargetScreen.Bounds;
        // Fagyasztás-animáció: fehér villanás lecseng, a keret és az eszköztár beúszik
        if (_longDoc) _flash = 0;   // a hosszú képnél nincs fagyasztás-villanás
        Ui.Animate(260, k => { if (!_longDoc) _flash = 1 - (float)k; _ui = (float)k; Invalidate(); });
    }

    // ================= Eszközök =================

    /// <summary>Eszközváltás. Az aktív (utoljára letett) objektum aktív marad.</summary>
    private void SetTool(Tool t)
    {
        if (t != Tool.Text) StopEditing();
        _tool = t;
        Invalidate();
    }

    // ================= Szöveg =================

    private Font TextFont(Annotation a) => new("Segoe UI", a.FontSize * S, GraphicsUnit.Pixel);

    /// <summary>
    /// Automatikus méret: a doboz a szöveghez igazodik (nincs sortörés, csak Enterrel).
    /// Ha a dobozt a fogópontokkal méretezték, ez már nem fut le.
    /// </summary>
    private void FitTextBox(Annotation a)
    {
        if (a.Kind != Tool.Text || a.ManualSize) return;
        using var f = TextFont(a);
        string probe = a.Text.Length == 0 ? "W" : a.Text.EndsWith('\n') ? a.Text + " " : a.Text;
        var size = Measurer.MeasureString(probe, f);
        var tl = new Point(Math.Min(a.A.X, a.B.X), Math.Min(a.A.Y, a.B.Y));
        a.A = tl;
        a.B = new Point(tl.X + (int)Math.Ceiling(size.Width + TextPad * 2) + 2,
                        tl.Y + (int)Math.Ceiling(size.Height + TextPad * 2));
    }

    /// <summary>Új szövegdoboz a kattintás helyén, azonnal gépelhető.</summary>
    private void CreateText(Point p)
    {
        var a = new Annotation
        {
            Kind = Tool.Text, Color = _color, FontSize = _fontSize,
            Background = _textBg, BackgroundOpacity = _textBgOpacity,
        };
        int lineH = (int)(_fontSize * S * 1.33f);
        a.A = a.B = new Point(p.X - (int)TextPad, p.Y - lineH / 2 - (int)TextPad);
        FitTextBox(a);
        _anns.Add(a);
        StartEditing(a);
    }

    private void StartEditing(Annotation a)
    {
        _editing = a;
        _selected = a;
        _tool = Tool.Text;
        _fontSize = a.FontSize;
        _color = a.Color;
        _textBg = a.Background;
        _textBgOpacity = a.BackgroundOpacity;
        _caretOn = true;
        _editSnap = TakeSnapshot();   // a gépelés egy visszavonható mozzanat
        Invalidate();
    }

    /// <summary>Gépelés vége. Az üresen hagyott szövegdoboz törlődik.</summary>
    private void StopEditing()
    {
        if (_editing == null) return;
        var e = _editing;
        _editing = null;
        if (string.IsNullOrWhiteSpace(e.Text))
        {
            _anns.Remove(e);
            if (_selected == e) _selected = null;
        }
        // Ha a gépelés változtatott a szövegen, egy lépésben visszavonható
        if (_editSnap != null && !SameState(_editSnap, TakeSnapshot())) _undo.Push(_editSnap);
        _editSnap = null;
        Invalidate();
    }

    private void TypeText(string s)
    {
        if (_editing == null) return;
        _editing.Text += s;
        FitTextBox(_editing);
        _caretOn = true;
        Invalidate();
    }

    /// <summary>Szövegdoboz háttérszíne. A kijelölt szövegre is azonnal hat.</summary>
    private void SetTextBackground(Color c)
    {
        _textBg = c;
        if (_selected?.Kind == Tool.Text) _selected.Background = c;
        Invalidate();
    }

    /// <summary>Háttér fedése 10%-os lépésben (10–100%). A kijelölt szövegre is hat.</summary>
    private void StepOpacity(int dir)
    {
        _textBgOpacity = Math.Clamp(_textBgOpacity + dir * 10, 10, 100);
        if (_selected?.Kind == Tool.Text) _selected.BackgroundOpacity = _textBgOpacity;
        Invalidate();
    }

    /// <summary>Betűméret léptetése. A kijelölt szövegre is azonnal hat.</summary>
    private void StepFont(int dir)
    {
        int i = Array.IndexOf(FontSizes, _fontSize);
        if (i < 0) i = Array.IndexOf(FontSizes, 20);
        _fontSize = FontSizes[Math.Clamp(i + dir, 0, FontSizes.Length - 1)];
        if (_selected?.Kind == Tool.Text)
        {
            _selected.FontSize = _fontSize;
            FitTextBox(_selected);
        }
        Invalidate();
    }

    /// <summary>
    /// Az aktív objektum átméretezésének kezdete: a kattintáshoz legközelebbi
    /// sarkot (vonalnál végpontot) fogjuk meg, a szemközti marad a helyén.
    /// Utána a rajzoláséval azonos UpdateShape mozgatja (Shift is működik).
    /// </summary>
    private void BeginResize(Annotation a, Point p)
    {
        static long D2(Point x, Point y) => (long)(x.X - y.X) * (x.X - y.X) + (long)(x.Y - y.Y) * (x.Y - y.Y);

        if (a.Kind == Tool.Text) a.ManualSize = true;   // kézi méret: innentől nincs automatikus méretezés

        if (a.Kind == Tool.Line)
        {
            if (D2(p, a.A) < D2(p, a.B))
            {
                // A mozgó pont mindig B: csere, a nyilak a helyükön maradnak
                (a.A, a.B) = (a.B, a.A);
                (a.ArrowStart, a.ArrowEnd) = (a.ArrowEnd, a.ArrowStart);
                _arrowStart = a.ArrowStart;
                _arrowEnd = a.ArrowEnd;
            }
            return;
        }

        var r = a.Box;
        var corners = new[]
        {
            (pt: new Point(r.Left, r.Top), opp: new Point(r.Right, r.Bottom)),
            (pt: new Point(r.Right, r.Top), opp: new Point(r.Left, r.Bottom)),
            (pt: new Point(r.Left, r.Bottom), opp: new Point(r.Right, r.Top)),
            (pt: new Point(r.Right, r.Bottom), opp: new Point(r.Left, r.Top)),
        };
        var near = corners.MinBy(c => D2(c.pt, p));
        a.A = near.opp;
        a.B = near.pt;
    }

    private enum ZOrder { ToBack, Backward, Forward, ToFront }

    /// <summary>
    /// A kijelölt objektum rétegsorrendje. A lista sorrendje a rajzolási sorrend:
    /// az első van leghátul, az utolsó legelöl.
    /// </summary>
    /// <summary>
    /// Használható-e a gomb: kell kijelölt objektum, és ami már legelöl van,
    /// azt nem lehet előrébb hozni, ami leghátul, azt nem lehet hátrébb küldeni.
    /// </summary>
    private bool CanReorder(ZOrder z)
    {
        if (_selected == null) return false;
        int i = _anns.IndexOf(_selected);
        if (i < 0) return false;
        return z is ZOrder.ToBack or ZOrder.Backward ? i > 0 : i < _anns.Count - 1;
    }

    private void Reorder(ZOrder z)
    {
        if (!CanReorder(z) || _selected is not { } sel) return;
        int i = _anns.IndexOf(sel);
        if (i < 0) return;
        switch (z)
        {
            case ZOrder.ToBack:
                _anns.RemoveAt(i); _anns.Insert(0, sel); break;
            case ZOrder.Backward when i > 0:
                (_anns[i - 1], _anns[i]) = (_anns[i], _anns[i - 1]); break;
            case ZOrder.Forward when i < _anns.Count - 1:
                (_anns[i + 1], _anns[i]) = (_anns[i], _anns[i + 1]); break;
            case ZOrder.ToFront:
                _anns.RemoveAt(i); _anns.Add(sel); break;
        }
        Invalidate();
    }

    /// <summary>
    /// Rétegsorrend-ikonok: egymásra lógó lapok, a kitöltött lap a kijelölt objektum.
    /// Előrébb / hátrébb: két lap, a kitöltött elöl / hátul.
    /// Legelőre / leghátra: három lap, a kitöltött a köteg tetején / alján.
    /// </summary>
    private void DrawOrderIcon(Graphics g, ZOrder z, Rectangle r, Color c)
    {
        bool enabled = CanReorder(z);
        if (!enabled) c = Color.FromArgb(c.A * 70 / 255, c);   // nem használható → halvány
        var ib = RectangleF.Inflate(r, -9 * S, -9 * S);

        bool three = z is ZOrder.ToBack or ZOrder.ToFront;
        int count = three ? 3 : 2;
        int filled = z switch { ZOrder.ToFront => 2, ZOrder.Forward => 1, _ => 0 };
        float sq = ib.Width * (three ? 0.56f : 0.66f);
        float step = (ib.Width - sq) / (count - 1);

        // A gomb aktuális háttere, hogy a felső lap takarja az alatta lévőt
        var bg = _hoverId == "z" + z && enabled ? Color.FromArgb(45, 45, 52) : Ui.Bg;
        using var pen = new Pen(c, 1.5f * S) { LineJoin = LineJoin.Round };
        using var fill = new SolidBrush(c);
        using var cover = new SolidBrush(bg);

        // Hátulról előre: bal alsó lap van leghátul, jobb felső legelöl
        for (int i = 0; i < count; i++)
        {
            var rect = new RectangleF(ib.Left + i * step, ib.Bottom - sq - i * step, sq, sq);
            using var path = Ui.Rounded(rect, 2.2f * S);
            g.FillPath(i == filled ? fill : cover, path);
            g.DrawPath(pen, path);
        }
    }

    /// <summary>Súgófelirat a gombokhoz (lebegéskor jelenik meg).</summary>
    private string? TipFor(string id) => id switch
    {
        "toolCrop" => "Vágás  (C)",
        "toolBlur" => "Elmosás  (B)",
        "toolOval" => "Ovál  (O)  ·  Shift: kör",
        "toolRect" => "Téglalap  (R)  ·  Shift: négyzet",
        "toolLine" => "Vonal  (L)  ·  Shift: 45°",
        "toolPen" => "Ceruza  (P)",
        "toolText" => "Szöveg  (T)  ·  dupla katt: szerkesztés",
        "toolHand" => "Kijelölés  (H)  ·  kattints egy objektumra",
        "fontDown" => "Kisebb betű",
        "fontUp" => "Nagyobb betű",
        "blurNormal" => "Elmosás a téglalapon belül",
        "blurInverse" => "Inverz: minden elmosva, csak a téglalap éles",
        "bg0" => "Nincs háttér",
        "bg1" => "Fehér háttér",
        "bg2" => "Fekete háttér",
        "bg3" => "Sárga háttér",
        "opDown" => "Átlátszóbb háttér",
        "opUp" => "Fedőbb háttér",
        "zToBack" => "Leghátra",
        "zBackward" => "Hátrébb",
        "zForward" => "Előrébb",
        "zToFront" => "Legelőre",
        "delete" => "Törlés  (Delete)",
        "history" => "Korábbi képek: újra megnyitható és szerkeszthető",
        "arrowStart" => "Nyíl az elején",
        "arrowSwap" => "Nyíl irányának cseréje",
        "arrowEnd" => "Nyíl a végén",
        "size" => "Képarány",
        "copy" => Settings.SaveEnabled ? "Vágólapra másol és PNG-be ment  (Enter)" : "Vágólapra másol  (Enter)",
        "save" => Settings.SaveEnabled ? "Mentés PNG-be: bekapcsolva" : "Mentés PNG-be: kikapcsolva (csak vágólap)",
        "folder" => "Mentési mappa:  " + Settings.SaveFolder,
        "long" => _anns.Count == 0
            ? "Hosszú kép felvétele: görgess vagy húzd (pásztázd) a kijelölt részt, Stop / Enter zárja"
            : "Hosszú kép: rajzolás előtt használható",
        _ when id.StartsWith("color") => null,
        _ => null,
    };

    private void DrawTooltip(Graphics g, int a)
    {
        if (_hoverId == null || _pressId != null) return;
        var btn = _buttons.FirstOrDefault(b => b.Id == _hoverId);
        var tip = btn == null ? null : TipFor(btn.Id);
        if (btn == null || tip == null) return;

        using var font = new Font("Segoe UI", 12 * S, GraphicsUnit.Pixel);
        var size = TextRenderer.MeasureText(tip, font);
        int w = size.Width + (int)(16 * S), h = (int)(26 * S), gap = (int)(8 * S);
        int x = Math.Clamp(btn.R.X + btn.R.Width / 2 - w / 2, 0, Math.Max(0, ScreenRect.Width - w));
        int y = btn.R.Top - gap - h - (int)(5 * S);
        if (y < 0) y = btn.R.Bottom + gap + (int)(5 * S);
        // Ne takarja a fölötte nyíló panelt: ha ott panel van, alá kerül
        var rect = new Rectangle(x, y, w, h);
        if (_panels.Any(p => p.IntersectsWith(rect))) rect.Y = btn.R.Bottom + gap + (int)(5 * S);

        using (var path = Ui.Rounded(rect, h / 2f))
        using (var bg = new SolidBrush(Color.FromArgb(a * 240 / 255, 10, 10, 12)))
            g.FillPath(bg, path);
        TextRenderer.DrawText(g, tip, font, rect, Color.FromArgb(a, Ui.Text),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// <summary>Objektum kijelölése: aktív lesz, és a típusának eszköze kapcsol be.</summary>
    private void SelectAnnotation(Annotation a)
    {
        if (_editing != a) StopEditing();
        _selected = a;
        _tool = a.Kind;
        if (a.Kind != Tool.Blur) _color = a.Color;
        if (a.Kind == Tool.Line) { _arrowStart = a.ArrowStart; _arrowEnd = a.ArrowEnd; }
        if (a.Kind == Tool.Text)
        {
            _fontSize = a.FontSize;
            _textBg = a.Background;
            _textBgOpacity = a.BackgroundOpacity;
        }
        if (a.Kind == Tool.Blur) _blurInverse = a.Inverse;
        Invalidate();
    }

    /// <summary>Elmosás módja: normál (a téglalap mosódik) vagy inverz (a téglalap éles, a többi mosódik).</summary>
    private void SetBlurInverse(bool inverse)
    {
        _blurInverse = inverse;
        if (_selected?.Kind == Tool.Blur) _selected.Inverse = inverse;
        Invalidate();
    }

    /// <summary>
    /// A kijelölt (vagy éppen rajzolt) objektum bárhol megfogható mozgatásra,
    /// a szélein is (vonalnál a vonal mentén, bő tűréssel).
    /// </summary>
    private bool CanMoveSelected(Point p) => _selected != null && _selected.HitTest(p, 10 * S);

    private void Deselect()
    {
        StopEditing();
        _selected = null;
        Invalidate();
    }

    private bool IsClick(Point p) => Math.Abs(p.X - _start.X) <= 4 * S && Math.Abs(p.Y - _start.Y) <= 4 * S;

    private void SetColor(Color c)
    {
        _color = c;
        if (_selected != null && _selected.Kind != Tool.Blur) _selected.Color = c;
        Invalidate();
    }

    private void ToggleArrow(bool start)
    {
        if (start) _arrowStart = !_arrowStart; else _arrowEnd = !_arrowEnd;
        if (_selected?.Kind == Tool.Line) { _selected.ArrowStart = _arrowStart; _selected.ArrowEnd = _arrowEnd; }
        Invalidate();
    }

    /// <summary>Felcseréli a két nyílvéget: ami az elején volt, a végére kerül, és fordítva.</summary>
    private void SwapArrows()
    {
        (_arrowStart, _arrowEnd) = (_arrowEnd, _arrowStart);
        if (_selected?.Kind == Tool.Line) { _selected.ArrowStart = _arrowStart; _selected.ArrowEnd = _arrowEnd; }
        Invalidate();
    }

    private Annotation? AnnotationAt(Point p)
    {
        for (int i = _anns.Count - 1; i >= 0; i--)
            if (_anns[i].HitTest(p, 8 * S)) return _anns[i];
        return null;
    }

    /// <summary>Rajzolás közben a végpont frissítése. Shift: kör, négyzet, 45°-os vonal.</summary>
    private void UpdateShape(Annotation a, Point p)
    {
        if (a.Kind == Tool.Pen)
        {
            var last = a.Points[^1];
            if (Math.Abs(p.X - last.X) + Math.Abs(p.Y - last.Y) >= 2) a.Points.Add(p);
            return;
        }

        int dx = p.X - a.A.X, dy = p.Y - a.A.Y;
        if ((ModifierKeys & Keys.Shift) != 0)
        {
            if (a.Kind is Tool.Oval or Tool.Rect)
            {
                int m = Math.Max(Math.Abs(dx), Math.Abs(dy));
                dx = (dx < 0 ? -1 : 1) * m;
                dy = (dy < 0 ? -1 : 1) * m;
            }
            else if (a.Kind == Tool.Line)
            {
                double len = Math.Sqrt(dx * dx + dy * dy);
                double ang = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
                dx = (int)Math.Round(Math.Cos(ang) * len);
                dy = (int)Math.Round(Math.Sin(ang) * len);
            }
        }
        a.B = new Point(a.A.X + dx, a.A.Y + dy);
    }

    // ================= Vágás =================

    private Hit HitTest(Point p)
    {
        float t = 14 * S, c = 22 * S;
        var r = _sel;
        bool nearL = Math.Abs(p.X - r.Left) <= t, nearR = Math.Abs(p.X - r.Right) <= t;
        bool nearT = Math.Abs(p.Y - r.Top) <= t, nearB = Math.Abs(p.Y - r.Bottom) <= t;
        bool cornL = Math.Abs(p.X - r.Left) <= c, cornR = Math.Abs(p.X - r.Right) <= c;
        bool cornT = Math.Abs(p.Y - r.Top) <= c, cornB = Math.Abs(p.Y - r.Bottom) <= c;

        if (cornL && cornT) return Hit.NW;
        if (cornR && cornT) return Hit.NE;
        if (cornL && cornB) return Hit.SW;
        if (cornR && cornB) return Hit.SE;
        bool inX = p.X > r.Left && p.X < r.Right, inY = p.Y > r.Top && p.Y < r.Bottom;
        if (nearT && inX) return Hit.N;
        if (nearB && inX) return Hit.S;
        if (nearL && inY) return Hit.W;
        if (nearR && inY) return Hit.E;
        if (r.Contains(p)) return _sel == _full ? Hit.New : Hit.Move;
        return Hit.New;
    }

    private static Cursor CursorFor(Hit h) => h switch
    {
        Hit.NW or Hit.SE => Cursors.SizeNWSE,
        Hit.NE or Hit.SW => Cursors.SizeNESW,
        Hit.N or Hit.S => Cursors.SizeNS,
        Hit.E or Hit.W => Cursors.SizeWE,
        Hit.Move => Cursors.SizeAll,
        _ => Cursors.Cross,
    };

    /// <summary>
    /// Képarány be- és kikapcsolása. Bekapcsoláskor a vágás azonnal felveszi az arányt
    /// (a jelenlegi vágásba írt legnagyobb, középre igazított téglalap).
    /// </summary>
    private void ToggleRatio(string name, double value)
    {
        if (_ratioName == name) { _ratio = null; _ratioName = null; Invalidate(); return; }
        _ratio = value;
        _ratioName = name;
        _tool = Tool.Crop;   // arányválasztás után rögtön a vágás méretezhető

        double w = _sel.Width, h = _sel.Height;
        if (w / h > value) w = h * value; else h = w / value;
        int cx = _sel.Left + _sel.Width / 2, cy = _sel.Top + _sel.Height / 2;
        _sel = new Rectangle(cx - (int)(w / 2), cy - (int)(h / 2), (int)Math.Round(w), (int)Math.Round(h));
        Invalidate();
    }

    /// <summary>Arányos téglalap a rögzített sarokból a mutató felé, a képernyőn belül.</summary>
    private Rectangle RatioFromCorner(Point anchor, Point p)
    {
        double r = _ratio!.Value;
        int sx = p.X < anchor.X ? -1 : 1, sy = p.Y < anchor.Y ? -1 : 1;
        double w = Math.Abs(p.X - anchor.X), h = Math.Abs(p.Y - anchor.Y);
        if (h == 0 || w / h > r) h = w / r; else w = h * r;   // a nagyobb elmozdulás vezet

        double maxW = sx > 0 ? _full.Width - anchor.X : anchor.X;
        double maxH = sy > 0 ? _full.Height - anchor.Y : anchor.Y;
        if (w > maxW) { w = maxW; h = w / r; }
        if (h > maxH) { h = maxH; w = h * r; }

        int x2 = anchor.X + sx * (int)Math.Round(w), y2 = anchor.Y + sy * (int)Math.Round(h);
        return Rectangle.FromLTRB(Math.Min(anchor.X, x2), Math.Min(anchor.Y, y2), Math.Max(anchor.X, x2), Math.Max(anchor.Y, y2));
    }

    /// <summary>Arányos élhúzás: a húzott méret változik, a másik arányosan, középre igazítva.</summary>
    private Rectangle RatioFromEdge(Hit edge, Point p)
    {
        double r = _ratio!.Value;
        var s = _startSel;
        if (edge is Hit.E or Hit.W)
        {
            int ax = edge == Hit.E ? s.Left : s.Right;
            double w = Math.Abs(p.X - ax), h = w / r;
            if (h > _full.Height) { h = _full.Height; w = h * r; }
            int x2 = ax + (p.X < ax ? -1 : 1) * (int)Math.Round(w);
            int top = Math.Clamp(s.Top + s.Height / 2 - (int)(h / 2), 0, _full.Height - (int)h);
            return Rectangle.FromLTRB(Math.Min(ax, x2), top, Math.Max(ax, x2), top + (int)Math.Round(h));
        }
        else
        {
            int ay = edge == Hit.S ? s.Top : s.Bottom;
            double h = Math.Abs(p.Y - ay), w = h * r;
            if (w > _full.Width) { w = _full.Width; h = w / r; }
            int y2 = ay + (p.Y < ay ? -1 : 1) * (int)Math.Round(h);
            int left = Math.Clamp(s.Left + s.Width / 2 - (int)(w / 2), 0, _full.Width - (int)w);
            return Rectangle.FromLTRB(left, Math.Min(ay, y2), left + (int)Math.Round(w), Math.Max(ay, y2));
        }
    }

    private void DragCrop(Point p)
    {
        var s = _startSel;
        if (_ratio != null && _drag != Hit.Move)
        {
            _sel = _drag switch
            {
                Hit.New => RatioFromCorner(_start, p),
                Hit.NW => RatioFromCorner(new Point(s.Right, s.Bottom), p),
                Hit.NE => RatioFromCorner(new Point(s.Left, s.Bottom), p),
                Hit.SW => RatioFromCorner(new Point(s.Right, s.Top), p),
                Hit.SE => RatioFromCorner(new Point(s.Left, s.Top), p),
                _ => RatioFromEdge(_drag, p),
            };
            return;
        }

        int dx = p.X - _start.X, dy = p.Y - _start.Y;
        int l = s.Left, t = s.Top, r = s.Right, b = s.Bottom;

        switch (_drag)
        {
            case Hit.New:
                l = _start.X; t = _start.Y; r = p.X; b = p.Y; break;
            case Hit.Move:
                dx = Math.Clamp(dx, -s.Left, _full.Width - s.Right);
                dy = Math.Clamp(dy, -s.Top, _full.Height - s.Bottom);
                l += dx; r += dx; t += dy; b += dy; break;
            default:
                if (_drag is Hit.W or Hit.NW or Hit.SW) l = p.X;
                if (_drag is Hit.E or Hit.NE or Hit.SE) r = p.X;
                if (_drag is Hit.N or Hit.NW or Hit.NE) t = p.Y;
                if (_drag is Hit.S or Hit.SW or Hit.SE) b = p.Y;
                break;
        }
        _sel = Rectangle.FromLTRB(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b));
    }

    /// <summary>
    /// Sima kattintás vágás módban: a legközelebbi sarok a kattintás helyére ugrik,
    /// a szemközti sarok marad.
    /// </summary>
    private void SnapNearestCorner(Point p)
    {
        var s = _startSel;
        var corners = new[]
        {
            (pt: new Point(s.Left, s.Top), opp: new Point(s.Right, s.Bottom)),
            (pt: new Point(s.Right, s.Top), opp: new Point(s.Left, s.Bottom)),
            (pt: new Point(s.Left, s.Bottom), opp: new Point(s.Right, s.Top)),
            (pt: new Point(s.Right, s.Bottom), opp: new Point(s.Left, s.Top)),
        };
        var near = corners.MinBy(c => (long)(c.pt.X - p.X) * (c.pt.X - p.X) + (long)(c.pt.Y - p.Y) * (c.pt.Y - p.Y));
        var o = near.opp;
        var r = _ratio != null
            ? RatioFromCorner(o, p)
            : Rectangle.FromLTRB(Math.Min(p.X, o.X), Math.Min(p.Y, o.Y), Math.Max(p.X, o.X), Math.Max(p.Y, o.Y));
        _sel = r.Width >= 4 && r.Height >= 4 ? r : s;
    }

    // ================= Egér és billentyűzet =================

    private UiButton? ButtonAt(Point p) => _buttons.FirstOrDefault(b => b.R.Contains(p));
    private bool OnPanel(Point p) => _panels.Any(r => r.Contains(p));
    /// <summary>Képernyős pont → dokumentum-pont (hosszú képnél eltolás és görgetés), a képen belülre szorítva.</summary>
    private Point ClampToScreen(Point p) =>
        new(Math.Clamp(p.X - OffX, 0, _full.Width), Math.Clamp(p.Y + _viewY, 0, _full.Height));

    private Rectangle ScreenRect => new(0, 0, TargetScreen.Bounds.Width, TargetScreen.Bounds.Height);
    private int MaxViewY => Math.Max(0, _full.Height - ScreenRect.Height + (int)(90 * S));   // alul hely az eszköztárnak
    private int MaxViewX => Math.Max(0, _full.Width - ScreenRect.Width);
    /// <summary>A dokumentum bal szélének helye a képernyőn: keskeny képnél középen, szélesnél görgetve.</summary>
    private int OffX => _full.Width <= ScreenRect.Width ? _centerX : -_viewX;

    /// <summary>A kijelölés látható része képernyő-koordinátában (az eszköztár elhelyezéséhez).</summary>
    private Rectangle SelOnScreen()
    {
        var r = Rectangle.Intersect(new Rectangle(_sel.X + OffX, _sel.Y - _viewY, _sel.Width, _sel.Height), ScreenRect);
        return r.IsEmpty ? new Rectangle(0, ScreenRect.Bottom, ScreenRect.Width, 0) : r;
    }

    /// <summary>Görgő: hosszú képnél a nézetet görgeti (húzás közben is); Shift + görgő: vízszintesen.</summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!_longDoc) return;
        int d = -(int)(e.Delta * S);
        if ((ModifierKeys & Keys.Shift) != 0) ScrollView(d, 0);
        else ScrollView(0, d);
    }

    /// <summary>Vízszintes görgő (billenő görgő, érintőpad): széles képnél oldalra görget.</summary>
    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEHWHEEL = 0x020E;
        if (m.Msg == WM_MOUSEHWHEEL && _longDoc)
        {
            int delta = (short)((long)m.WParam >> 16);
            ScrollView((int)(delta * S), 0);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    private void ScrollView(int dx, int dy)
    {
        int oldX = _viewX, oldY = _viewY;
        _viewX = Math.Clamp(_viewX + dx, 0, MaxViewX);
        _viewY = Math.Clamp(_viewY + dy, 0, MaxViewY);
        if (_viewX == oldX && _viewY == oldY) return;
        // Húzás közben a megfogott pont kövesse a nézetet (a középső gombos mozgatás kivételével)
        if (Capture && _panFrom == null)
        {
            var c = PointToClient(Cursor.Position);
            OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, c.X, c.Y, 0));
        }
        Invalidate();
    }

    /// <summary>
    /// Hosszú képnél húzás közben (vágás, rajzolás, mozgatás) a képernyő tetején vagy alján
    /// a nézet magától görget; minél közelebb a szélhez, annál gyorsabban.
    /// </summary>
    private void EdgeScrollTick()
    {
        if (!_longDoc || !Capture) return;
        var c = PointToClient(Cursor.Position);
        float zone = 48 * S;
        int h = ScreenRect.Height, w = ScreenRect.Width;
        int Speed(float into) => (int)(6 * S + 30 * S * into / zone);
        int dy = c.Y < zone ? -Speed(zone - c.Y) : c.Y > h - zone ? Speed(c.Y - (h - zone)) : 0;
        int dx = c.X < zone ? -Speed(zone - c.X) : c.X > w - zone ? Speed(c.X - (w - zone)) : 0;
        if (dx != 0 || dy != 0) ScrollView(dx, dy);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        LayoutToolbar();
        // Középső gomb: a hosszú / széles kép nézetének mozgatása bármelyik eszközzel
        if (e.Button == MouseButtons.Middle && _longDoc) { _panFrom = e.Location; Cursor = Cursors.SizeAll; return; }
        if (e.Button == MouseButtons.Right) { Cancelled?.Invoke(); return; }
        if (e.Button != MouseButtons.Left) return;

        var btn = ButtonAt(e.Location);
        if (btn != null) { _pressId = btn.Id; Invalidate(); return; }
        if (OnPanel(e.Location)) return;

        var p = ClampToScreen(e.Location);
        _beforeGesture = TakeSnapshot();   // a felengedéskor eldől, változott-e valami (rajzolás, mozgatás, vágás…)
        switch (_tool)
        {
            case Tool.Hand:
                // Kéz: objektumra kattintva kijelöli, és rögtön átvált az objektum eszközére.
                // Lenyomva tartva azonnal mozgatható is. Üres helyre kattintva csak leveszi a fókuszt.
                _start = p;
                if (HandleAt(p) != null)
                {
                    BeginResize(_selected!, p);
                    _resizingAnn = true;
                    break;
                }
                var picked = AnnotationAt(p);
                if (picked != null)
                {
                    SelectAnnotation(picked);
                    _movingAnn = true;
                    _last = p;
                    _keepOnClick = true;   // ez a kattintás kijelölt, ne vegye le a fókuszt felengedéskor
                }
                else Deselect();
                break;

            case Tool.Crop:
                _drag = HitTest(p);
                _start = p;
                _startSel = _sel;
                break;

            default:
                _start = p;
                // 1. Az aktív objektum fogópontja: átméretezés
                if (HandleAt(p) != null)
                {
                    BeginResize(_selected!, p);
                    _resizingAnn = true;
                    break;
                }
                // 2. Kattintással kijelölt objektum belseje vagy vonala: mozgatás
                if (CanMoveSelected(p))
                {
                    _movingAnn = true;
                    _last = p;
                    break;
                }
                // 3. Van kijelölt objektum, de máshol nyomtad le: ez csak fókuszvesztés lesz,
                //    rajzolás nem indul (új objektum csak fókusz nélkül jöhet)
                if (_selected != null)
                {
                    _pendingDeselect = true;
                    break;
                }
                // 4. Nincs fókusz: húzásra új objektum, kattintásra kijelölés
                _pendingClick = true;
                break;
        }
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        LayoutToolbar();
        if (_panFrom is Point pf)
        {
            _panFrom = e.Location;
            ScrollView(pf.X - e.X, pf.Y - e.Y);
            return;
        }
        var p = ClampToScreen(e.Location);

        if (_pendingClick && !IsClick(p))
        {
            // Húzás: új objektum a lenyomás helyétől (akkor is, ha egy objektumon kezdődik)
            _pendingClick = false;
            StopEditing();
            _selected = null;
            _drawing = new Annotation
            {
                Kind = _tool, Color = _color, A = _start, B = _start,
                ArrowStart = _arrowStart, ArrowEnd = _arrowEnd,
                FontSize = _fontSize, ManualSize = _tool == Tool.Text,   // húzott szövegdoboz: fix méret
                Background = _textBg, BackgroundOpacity = _textBgOpacity,
                Inverse = _tool == Tool.Blur && _blurInverse,
            };
            if (_tool == Tool.Pen) _drawing.Points.Add(_start);
        }
        if (_pendingClick) return;

        if (_drawing != null) { UpdateShape(_drawing, p); Invalidate(); return; }
        if (_resizingAnn && _selected != null) { UpdateShape(_selected, p); Invalidate(); return; }
        if (_movingAnn && _selected != null)
        {
            _selected.Offset(p.X - _last.X, p.Y - _last.Y);
            _last = p;
            Invalidate();
            return;
        }
        if (_drag != Hit.None) { DragCrop(p); Invalidate(); return; }

        // Nincs húzás: lebegés és kurzor
        var btn = ButtonAt(e.Location);
        if (btn?.Id != _hoverId) { _hoverId = btn?.Id; Invalidate(); }

        if (btn != null) Cursor = Cursors.Hand;
        else if (OnPanel(e.Location)) Cursor = Cursors.Default;
        else Cursor = _tool switch
        {
            Tool.Crop => CursorFor(HitTest(p)),
            Tool.Hand => HandleCursor(p) ?? Cursors.Hand,
            // Nem kijelölt objektum fölött is célkereszt: a húzás ott is új objektumot rajzol
            // Fogópont: méretezés · kijelölt objektum: mozgatás ·
            // kijelölés mellett máshol: nyíl (kattintás = fókuszvesztés) · fókusz nélkül: rajzolás
            _ => HandleCursor(p) ?? (CanMoveSelected(p) ? Cursors.SizeAll
                 : _selected != null ? Cursors.Default
                 : _tool == Tool.Text ? Cursors.IBeam : Cursors.Cross),
        };
    }

    /// <summary>Dupla kattintás egy szövegen: szerkesztés (gépelés) indul.</summary>
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || ButtonAt(e.Location) != null || OnPanel(e.Location)) return;
        var hit = AnnotationAt(ClampToScreen(e.Location));
        if (hit?.Kind != Tool.Text) return;
        StartEditing(hit);
        _suppressUp = true;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        LayoutToolbar();
        if (e.Button == MouseButtons.Middle && _panFrom != null) { _panFrom = null; Cursor = Cursors.Default; return; }
        if (_pressId != null)
        {
            var id = _pressId;
            _pressId = null;
            var btn = ButtonAt(e.Location);
            if (btn != null && btn.Id == id) Undoable(btn.Click);   // szín, nyíl, betű, sorrend, törlés, arány…
            Invalidate();
            return;
        }

        Capture = false;
        var p = ClampToScreen(e.Location);

        if (_suppressUp)
        {
            // Dupla kattintás második felengedése: a szerkesztés már elindult
            _suppressUp = false;
            _pendingClick = _pendingDeselect = _movingAnn = _resizingAnn = false;
            _beforeGesture = null;
            Invalidate();
            return;
        }

        if (_pendingDeselect)
        {
            // Kijelölés mellett bárhova kattintva (vagy húzva): fókuszvesztés
            _pendingDeselect = false;
            Deselect();
        }
        else if (_pendingClick)
        {
            // Fókusz nélkül sima kattintás: objektumon kijelöli, szöveg eszköznél üres helyre új szöveg
            _pendingClick = false;
            var hit = AnnotationAt(p);
            if (hit != null) SelectAnnotation(hit);
            else if (_tool == Tool.Text) CreateText(p);
        }
        else if (_drawing != null)
        {
            // A letett objektum aktív marad, amíg újat nem rajzolsz vagy mellé nem kattintasz
            if (!_drawing.IsTiny)
            {
                _anns.Add(_drawing);
                _selected = _drawing;
                if (_drawing.Kind == Tool.Text) StartEditing(_drawing);   // húzott szövegdoboz: gépelhető
            }
            else if (_drawing.Kind == Tool.Text && _selected == null) CreateText(_start);
            _drawing = null;
        }
        else if (_resizingAnn)
        {
            _resizingAnn = false;
            if (IsClick(p)) Deselect();   // húzás nélküli kattintás a fogópontra: fókuszvesztés
        }
        else if (_movingAnn)
        {
            _movingAnn = false;
            // Húzás nélküli kattintás a kijelöltre: fókuszvesztés (kivéve, ha épp a kéz jelölte ki)
            if (IsClick(p) && !_keepOnClick) Deselect();
            _keepOnClick = false;
        }
        else if (_drag != Hit.None)
        {
            _drag = Hit.None;
            bool click = Math.Abs(p.X - _start.X) <= 4 * S && Math.Abs(p.Y - _start.Y) <= 4 * S;
            if (click) SnapNearestCorner(p);
            else if (_sel.Width < 4 || _sel.Height < 4) _sel = _startSel;   // túl kicsi → vissza
        }

        // Ha a mozdulat változtatott valamin (rajzolás, mozgatás, méretezés, vágás), visszavonható
        if (_beforeGesture != null && !SameState(_beforeGesture, TakeSnapshot())) _undo.Push(_beforeGesture);
        _beforeGesture = null;
        Invalidate();
    }

    // ================= Visszavonás (Ctrl+Z) =================

    /// <summary>A visszaállítható állapot: jelölések, vágás, képarány.</summary>
    private sealed record Snapshot(List<Annotation> Anns, Rectangle Sel, double? Ratio, string? RatioName);

    private readonly Stack<Snapshot> _undo = new();
    private Snapshot? _beforeGesture;   // egérlenyomáskori állapot
    private Snapshot? _editSnap;        // gépelés kezdetekori állapot

    /// <summary>Állapotkép. Az üres szövegdobozok nem számítanak (úgyis törlődnek).</summary>
    private Snapshot TakeSnapshot() => new(
        _anns.Where(a => !(a.Kind == Tool.Text && string.IsNullOrWhiteSpace(a.Text))).Select(a => a.Clone()).ToList(),
        _sel, _ratio, _ratioName);

    private static bool SameState(Snapshot a, Snapshot b) =>
        a.Sel == b.Sel && a.Ratio == b.Ratio && a.RatioName == b.RatioName &&
        a.Anns.Count == b.Anns.Count && a.Anns.Zip(b.Anns).All(p => p.First.SameAs(p.Second));

    /// <summary>Művelet, amely visszavonható, ha valóban változtatott valamin.</summary>
    private void Undoable(Action act)
    {
        var before = TakeSnapshot();
        act();
        if (!SameState(before, TakeSnapshot())) _undo.Push(before);
    }

    /// <summary>Az utolsó mozzanat visszavonása: rajzolás, mozgatás, méretezés, vágás, szín, törlés, gépelés…</summary>
    private void Undo()
    {
        var current = TakeSnapshot();
        while (_undo.Count > 0)
        {
            var s = _undo.Pop();
            if (SameState(s, current)) continue;   // a látható állapoton nem változtatna → a következő
            _editing = null;
            _editSnap = null;
            _anns.Clear();
            _anns.AddRange(s.Anns.Select(a => a.Clone()));
            _sel = s.Sel;
            _ratio = s.Ratio;
            _ratioName = s.RatioName;
            _selected = null;
            _drawing = null;
            break;
        }
        Invalidate();
    }

    /// <summary>Gépelt karakterek a szerkesztett szövegbe.</summary>
    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (_editing == null || char.IsControl(e.KeyChar)) return;
        TypeText(e.KeyChar.ToString());
        e.Handled = true;
    }

    /// <summary>Szerkesztés közbeni billentyűk. A kurzor mindig a szöveg végén áll.</summary>
    private void EditKey(KeyEventArgs e)
    {
        var a = _editing!;
        switch (e.KeyCode)
        {
            case Keys.Escape:
                StopEditing();
                break;
            case Keys.Enter:
                TypeText("\n");
                break;
            case Keys.Back:
                if (a.Text.Length > 0)
                {
                    // Ctrl+Backspace: egész szó törlése
                    int cut = e.Control ? a.Text.TrimEnd().LastIndexOfAny(new[] { ' ', '\n' }) + 1 : a.Text.Length - 1;
                    a.Text = a.Text[..Math.Max(0, cut)];
                    FitTextBox(a);
                    _caretOn = true;
                    Invalidate();
                }
                break;
            case Keys.V when e.Control:
                if (Clipboard.ContainsText()) TypeText(Clipboard.GetText().Replace("\r\n", "\n").Replace('\r', '\n'));
                break;
            case Keys.Z when e.Control:
                // Gépelés közben: a gépelés vége, és visszavonja az utolsó mozzanatot (a gépelést)
                StopEditing();
                Undo();
                break;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_editing != null) { EditKey(e); return; }   // gépelés közben nincs gyorsbillentyű

        switch (e.KeyCode)
        {
            case Keys.Escape: Cancelled?.Invoke(); return;
            case Keys.Enter: Confirm(); return;
            case Keys.C when e.Control: Confirm(); return;
            case Keys.Z when e.Control:
                Undo();
                return;
            case Keys.Delete or Keys.Back:
                if (_selected != null) Undoable(DeleteSelected);
                return;
        }
        if (e.Control || e.Alt) return;

        // Gyorsbillentyűk az eszközökhöz
        Tool? t = e.KeyCode switch
        {
            Keys.C => Tool.Crop, Keys.B => Tool.Blur, Keys.O => Tool.Oval, Keys.R => Tool.Rect,
            Keys.L => Tool.Line, Keys.P => Tool.Pen, Keys.T => Tool.Text, Keys.H => Tool.Hand, _ => null,
        };
        if (t != null) SetTool(t.Value);
    }

    private void Confirm()
    {
        StopEditing();
        Confirmed?.Invoke(this);
    }

    /// <summary>A kijelölt rész új bitmapként, a jelölésekkel együtt.</summary>
    public Bitmap Crop()
    {
        var bmp = new Bitmap(_sel.Width, _sel.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.TranslateTransform(-_sel.X, -_sel.Y);
        g.DrawImage(_shot, _sel, _sel, GraphicsUnit.Pixel);
        Ui.Prepare(g);
        foreach (var a in _anns) DrawAnnotation(g, a);
        return bmp;
    }

    // ================= Rajzolás =================

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        LayoutToolbar();
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.CompositingQuality = CompositingQuality.HighSpeed;

        if (_longDoc)
        {
            // Hosszú kép: sötét háttér, középre igazított, görgetett dokumentum
            g.Clear(Color.FromArgb(18, 18, 20));
            g.TranslateTransform(OffX, -_viewY);
            var vis = Rectangle.Intersect(new Rectangle(-OffX, _viewY, ScreenRect.Width, ScreenRect.Height), _full);
            if (!vis.IsEmpty)
            {
                g.DrawImage(_shot, vis, vis, GraphicsUnit.Pixel);
                using var outside = new Region(vis);
                outside.Exclude(_sel);
                using var dimBrush = new SolidBrush(Color.FromArgb(150, 8, 8, 12));
                g.FillRegion(dimBrush, outside);
            }
        }
        else
        {
            // Kívül sötét, a vágás eredeti fényerejű
            g.DrawImage(_dim!, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);
            var bright = Rectangle.Intersect(_sel, e.ClipRectangle);
            if (!bright.IsEmpty) g.DrawImage(_shot, bright, bright, GraphicsUnit.Pixel);
        }

        Ui.Prepare(g);
        foreach (var a in _anns) DrawAnnotation(g, a);
        if (_drawing != null) DrawAnnotation(g, _drawing);

        if (_selected != null) DrawSelection(g, _selected);
        if (_editing != null && _caretOn) DrawCaret(g, _editing);

        int alpha = (int)(255 * _ui);
        if (alpha > 0) DrawFrame(g, alpha);
        g.ResetTransform();   // az eszköztár képernyő-koordinátában van
        if (alpha > 0) DrawToolbar(g, alpha);

        if (_flash > 0)
        {
            using var w = new SolidBrush(Color.FromArgb((int)(150 * _flash), Color.White));
            g.FillRectangle(w, ClientRectangle);
        }
    }

    private void DrawAnnotation(Graphics g, Annotation a)
    {
        if (a.Kind == Tool.Blur)
        {
            if (a.Inverse)
            {
                // Inverz (fókusz): a vágáson belül minden el van mosva, kivéve a téglalapot
                var state = g.Save();
                g.SetClip(_sel, CombineMode.Intersect);
                g.ExcludeClip(a.Box);
                DrawBlurred(g, _sel);
                g.Restore(state);
                return;
            }
            var r = Rectangle.Intersect(a.Box, _full);
            if (r.Width <= 0 || r.Height <= 0) return;
            DrawBlurred(g, r);
            return;
        }

        using var pen = new Pen(a.Color, StrokeW)
        {
            StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round,
        };
        switch (a.Kind)
        {
            case Tool.Oval:
                g.DrawEllipse(pen, a.Box);
                break;
            case Tool.Rect:
                g.DrawRectangle(pen, a.Box);
                break;
            case Tool.Line:
                using (var cap = new AdjustableArrowCap(4.5f, 5.5f, true))
                {
                    if (a.ArrowStart) pen.CustomStartCap = cap;
                    if (a.ArrowEnd) pen.CustomEndCap = cap;
                    g.DrawLine(pen, a.A, a.B);
                }
                break;
            case Tool.Pen:
                if (a.Points.Count > 1) g.DrawLines(pen, a.Points.ToArray());
                break;
            case Tool.Text:
                if (!a.Background.IsEmpty)
                {
                    // Háttér: lekerekített doboz a beállított fedéssel
                    using var bgPath = Ui.Rounded(a.Box, 4 * S);
                    using var bgBrush = new SolidBrush(Color.FromArgb(a.BackgroundOpacity * 255 / 100, a.Background));
                    g.FillPath(bgBrush, bgPath);
                }
                if (a.Text.Length == 0) break;
                using (var font = TextFont(a))
                using (var br = new SolidBrush(a.Color))
                    g.DrawString(a.Text, font, br, TextLayout(a));
                break;
        }
    }

    /// <summary>A szöveg helye a dobozon belül. Automatikus méretnél nincs sortörés.</summary>
    private RectangleF TextLayout(Annotation a)
    {
        var lr = RectangleF.Inflate(a.Box, -TextPad, -TextPad);
        if (!a.ManualSize) lr.Width = 100000;   // automatikus méret: csak Enter tör sort
        return lr;
    }

    /// <summary>Villogó szövegkurzor a szöveg végén (a mentett képre nem kerül rá).</summary>
    private void DrawCaret(Graphics g, Annotation a)
    {
        using var font = TextFont(a);
        string probe = a.Text + "x";   // a kurzor az utolsó karakter utáni hely
        using var sf = new StringFormat();
        sf.SetMeasurableCharacterRanges(new[] { new CharacterRange(probe.Length - 1, 1) });
        var regions = g.MeasureCharacterRanges(probe, font, TextLayout(a), sf);
        var rb = regions.Length > 0 ? regions[0].GetBounds(g) : RectangleF.Empty;
        foreach (var r in regions) r.Dispose();

        float x = rb.IsEmpty ? a.Box.Left + TextPad : rb.Left;
        float y = rb.IsEmpty ? a.Box.Top + TextPad : rb.Top;
        float h = font.GetHeight(g);
        using var pen = new Pen(Ui.Accent, Math.Max(1.5f, 1.5f * S));
        g.DrawLine(pen, x + 1 * S, y + h * 0.12f, x + 1 * S, y + h * 0.9f);
    }

    /// <summary>Fogópontok: sarkok, vonalnál a két vég. Ceruzának nincs.</summary>
    private static Point[] HandlePoints(Annotation a)
    {
        if (a.Kind == Tool.Pen) return Array.Empty<Point>();
        if (a.Kind == Tool.Line) return new[] { a.A, a.B };
        var r = a.Box;
        return new[] { new Point(r.Left, r.Top), new Point(r.Right, r.Top), new Point(r.Left, r.Bottom), new Point(r.Right, r.Bottom) };
    }

    /// <summary>A kijelölt objektum melyik fogópontja van a kattintás alatt (ha van).</summary>
    private Point? HandleAt(Point p)
    {
        if (_selected == null) return null;
        float tol = 12 * S;
        foreach (var h in HandlePoints(_selected))
            if (Math.Abs(h.X - p.X) <= tol && Math.Abs(h.Y - p.Y) <= tol) return h;
        return null;
    }

    private Cursor? HandleCursor(Point p)
    {
        var h = HandleAt(p);
        if (h == null) return null;
        if (_selected!.Kind == Tool.Line) return Cursors.SizeAll;
        var c = _selected.Box.Location + new Size(_selected.Box.Width / 2, _selected.Box.Height / 2);
        bool left = h.Value.X < c.X, top = h.Value.Y < c.Y;
        return left == top ? Cursors.SizeNWSE : Cursors.SizeNESW;
    }

    /// <summary>Aktív objektum jelzése: fogópontok a sarkokon (vonalnál a végeken).</summary>
    private void DrawSelection(Graphics g, Annotation a)
    {
        if (a.Kind is Tool.Pen or Tool.Text)   // ceruza: nincs fogópont; szöveg: látszódjon a doboz
        {
            var box = a.Box;
            box.Inflate((int)(6 * S), (int)(6 * S));
            using var dash = new Pen(Color.FromArgb(200, Ui.Accent), 1.3f * S) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(dash, box);
        }
        var pts = HandlePoints(a);
        float d = 8 * S;
        using var fill = new SolidBrush(Color.White);
        using var edge = new Pen(Ui.Accent, 1.6f * S);
        foreach (var p in pts)
        {
            var c = new RectangleF(p.X - d / 2, p.Y - d / 2, d, d);
            g.FillEllipse(fill, c);
            g.DrawEllipse(edge, c);
        }
    }

    /// <summary>
    /// Erősen elmosott másolat: kicsinyítés, majd simított nagyítás.
    /// A szöveg teljesen olvashatatlan lesz alatta.
    /// </summary>
    /// <remarks>
    /// Csak a kicsinyített képet tartjuk meg, és rajzoláskor nagyítjuk fel a kért részt.
    /// Így egy hosszú, sok ezer pixeles képnél sem kell egy teljes méretű elmosott másolat.
    /// </remarks>
    private void DrawBlurred(Graphics g, Rectangle r)
    {
        if (_blurSmall == null)
        {
            _blurF = Math.Max(10, (int)(12 * S));
            int sw = Math.Max(1, _shot.Width / _blurF), sh = Math.Max(1, _shot.Height / _blurF);
            _blurSmall = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb);
            using var sg = Graphics.FromImage(_blurSmall);
            sg.InterpolationMode = InterpolationMode.HighQualityBilinear;
            sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            sg.DrawImage(_shot, new Rectangle(0, 0, sw, sh));
        }

        float fx = _shot.Width / (float)_blurSmall.Width, fy = _shot.Height / (float)_blurSmall.Height;
        using var ia = new ImageAttributes();
        ia.SetWrapMode(WrapMode.TileFlipXY);   // a széleken se legyen sötét perem
        var st = g.Save();
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_blurSmall, r, r.X / fx, r.Y / fy, r.Width / fx, r.Height / fy, GraphicsUnit.Pixel, ia);
        g.Restore(st);
    }

    private void DrawFrame(Graphics g, int a)
    {
        var r = _sel;
        float inset = 1.5f * S;   // teljes képernyőnél is látszódjon a keret
        var fr = RectangleF.FromLTRB(r.Left + inset, r.Top + inset, r.Right - inset, r.Bottom - inset);

        int frameA = _tool == Tool.Crop ? a * 200 / 255 : a * 90 / 255;
        using (var pen = new Pen(Color.FromArgb(frameA, Color.White), 1.2f * S))
            g.DrawRectangle(pen, fr.X, fr.Y, fr.Width, fr.Height);

        if (_tool != Tool.Crop) return;   // más eszköznél a vágás nem méretezhető

        // Sarokfogók: vastag L alakú jelek
        float len = Math.Min(20 * S, Math.Min(fr.Width, fr.Height) / 2), w = 4 * S;
        using var hp = new Pen(Color.FromArgb(a, Color.White), w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float o = w / 2;
        float l = fr.Left + o, t = fr.Top + o, rr = fr.Right - o, b = fr.Bottom - o;
        g.DrawLines(hp, new[] { new PointF(l, t + len), new PointF(l, t), new PointF(l + len, t) });
        g.DrawLines(hp, new[] { new PointF(rr - len, t), new PointF(rr, t), new PointF(rr, t + len) });
        g.DrawLines(hp, new[] { new PointF(l, b - len), new PointF(l, b), new PointF(l + len, b) });
        g.DrawLines(hp, new[] { new PointF(rr - len, b), new PointF(rr, b), new PointF(rr, b - len) });
    }

    // ================= Eszköztár =================

    /// <summary>
    /// Elrendezés: [eszközök | színek]  [méret · Másolás]
    /// Vonal eszköznél fölötte külön kis panel a nyilak beállításához.
    /// </summary>
    private void LayoutToolbar()
    {
        _buttons.Clear();
        _panels.Clear();

        int h = (int)(44 * S), pad = (int)(5 * S), b = h - pad * 2, gap = (int)(2 * S);
        int sep = (int)(13 * S), outer = (int)(14 * S), between = (int)(10 * S);

        int toolsW = pad + Tools.Length * b + (Tools.Length - 1) * gap + sep + Palette.Length * b + (Palette.Length - 1) * gap + pad;

        using var sizeFont = new Font("Segoe UI", 12 * S, GraphicsUnit.Pixel);
        _sizeText = _ratioName != null ? $"{_sel.Width} × {_sel.Height}  ·  {_ratioName}" : $"{_sel.Width} × {_sel.Height}";
        int sizeW = TextRenderer.MeasureText(_sizeText, sizeFont).Width + (int)(22 * S);
        int copyBtnW = (int)(132 * S);                  // "[ikon] Másolás"
        int saveW = (int)(64 * S);                      // mentés be/ki kapcsoló
        int longW = _longDoc ? 0 : b + gap;                 // "Hosszú kép" gomb csak a fagyasztott képen
        int histW = b + gap;                            // korábbi képek gomb
        int copyW = histW + sizeW + longW + copyBtnW + gap + saveW + gap + b + pad;   // [előzmény] [méret] [hosszú] [Másolás] [mentés ◐] [mappa]
        int total = toolsW + between + copyW;   // a rétegsorrend nem itt van, hanem a kijelölt objektum mellett

        // Képernyő-koordinátában: hosszú képnél a kijelölés látható része számít
        var selS = SelOnScreen();
        var scr = ScreenRect;
        int x = selS.Left + (selS.Width - total) / 2;
        int y = selS.Bottom + outer;
        if (y + h > scr.Height - outer) y = selS.Bottom - h - outer;   // nincs hely alatta → belül
        if (y + h > scr.Height - outer) y = scr.Height - h - outer;
        if (y < outer) y = outer;
        x = Math.Clamp(x, outer, Math.Max(outer, scr.Width - total - outer));

        // Eszközök panel
        _panels.Add(new Rectangle(x, y, toolsW, h));
        int cx = x + pad;
        Rectangle lineBtn = default, textBtn = default, blurBtn = default;
        foreach (var tool in Tools)
        {
            var t = tool;
            var r = new Rectangle(cx, y + pad, b, b);
            if (t == Tool.Line) lineBtn = r;
            if (t == Tool.Text) textBtn = r;
            if (t == Tool.Blur) blurBtn = r;
            _buttons.Add(new UiButton("tool" + t, r, () => SetTool(t), () => _tool == t, (g, rr, c) => DrawToolIcon(g, t, rr, c)));
            cx += b + gap;
        }
        cx += sep - gap;
        _sepX = cx - sep / 2;
        _sepTop = y + (int)(12 * S);
        _sepBottom = y + h - (int)(12 * S);
        foreach (var color in Palette)
        {
            var col = color;
            var r = new Rectangle(cx, y + pad, b, b);
            _buttons.Add(new UiButton("color" + col.ToArgb(), r, () => SetColor(col), () => _color == col,
                (g, rr, _) => DrawSwatch(g, rr, col, (int)(255 * _ui)), Plain: true));
            cx += b + gap;
        }

        // Méret + Másolás panel
        var copyPanel = new Rectangle(x + toolsW + between, y, copyW, h);
        _panels.Add(copyPanel);

        LayoutObjectPanel(h, pad, b, gap);

        // Korábbi képek: legördülő bélyegképekkel
        var histBtn = new Rectangle(copyPanel.X + pad, y + pad, b, b);
        _buttons.Add(new UiButton("history", histBtn, ToggleHistory, () => _historyOpen, DrawHistoryIcon));

        _sizeRect = new Rectangle(copyPanel.X + pad + histW, y + pad, sizeW - pad, h - pad * 2);
        _buttons.Add(new UiButton("size", _sizeRect, () =>
            {
                _ratioMenuOpen = !_ratioMenuOpen;
                if (_ratioMenuOpen && _historyOpen) { _historyOpen = false; DisposeHistory(); }
                Invalidate();
            },
            () => _ratioMenuOpen, DrawSizeLabel));
        // Hosszú kép: felvétel indul, a kijelölt részen a felhasználó görget, az app összefűzi
        if (!_longDoc)
        {
            bool canLong = _anns.Count == 0;   // a már rajzolt jelölések elvesznének
            _buttons.Add(new UiButton("long", new Rectangle(copyPanel.X + histW + sizeW, y + pad, b, b),
                () => { if (_anns.Count == 0) { StopEditing(); LongCaptureRequested?.Invoke(this); } },
                () => false, (g, r, c) => DrawLongIcon(g, r, canLong ? c : Color.FromArgb(c.A * 70 / 255, c)), Plain: !canLong));
        }

        // Másolás (csak vágólap), mellette a mentés kapcsoló (be: PNG-be is ment), majd a mappa
        int copyX = copyPanel.X + histW + sizeW + longW;
        _buttons.Add(new UiButton("copy", new Rectangle(copyX, y + pad, copyBtnW, h - pad * 2),
            Confirm, () => false, DrawCopyButton, Plain: true));
        int saveX = copyX + copyBtnW + gap;
        _buttons.Add(new UiButton("save", new Rectangle(saveX, y + pad, saveW, h - pad * 2),
            () => { Settings.SaveEnabled = !Settings.SaveEnabled; Invalidate(); }, () => false, DrawSaveToggle));
        _buttons.Add(new UiButton("folder", new Rectangle(saveX + saveW + gap, y + pad, b, b),
            ChooseFolder, () => false,
            (g, r, c) => DrawFolderIcon(g, r, Settings.SaveEnabled ? c : Color.FromArgb(c.A * 110 / 255, c))));

        if (_historyOpen) LayoutHistory(histBtn, copyPanel, h, pad, gap);

        // Képarány-menü a méretkijelző fölött (vagy alatt, ha fölötte nincs hely)
        if (_ratioMenuOpen)
        {
            using var rf = new Font("Segoe UI Semibold", 12 * S, GraphicsUnit.Pixel);
            var widths = Ratios.Select(r => Math.Max(b, TextRenderer.MeasureText(r.Name, rf).Width + (int)(18 * S))).ToArray();
            int pw = pad * 2 + widths.Sum() + gap * (Ratios.Length - 1);
            int py = y - h - (int)(8 * S);
            if (py < outer) py = y + h + (int)(8 * S);
            int px = Math.Clamp(copyPanel.Right - pw, outer, Math.Max(outer, ScreenRect.Width - pw - outer));
            _panels.Add(new Rectangle(px, py, pw, h));
            int rx = px + pad;
            for (int i = 0; i < Ratios.Length; i++)
            {
                var (name, value) = Ratios[i];
                _buttons.Add(new UiButton("ratio" + name, new Rectangle(rx, py + pad, widths[i], b),
                    () => ToggleRatio(name, value), () => _ratioName == name,
                    (g, r, c) => DrawText(g, name, r, c, 12)));
                rx += widths[i] + gap;
            }
        }

        // Elmosás panel: [normál] [inverz / fókusz]
        if (_tool == Tool.Blur)
        {
            int pw = pad * 2 + b * 2 + gap;
            int py = y - h - (int)(8 * S);
            if (py < outer) py = y + h + (int)(8 * S);
            int px = Math.Clamp(blurBtn.X + b / 2 - pw / 2, outer, Math.Max(outer, ScreenRect.Width - pw - outer));
            _panels.Add(new Rectangle(px, py, pw, h));
            _buttons.Add(new UiButton("blurNormal", new Rectangle(px + pad, py + pad, b, b),
                () => SetBlurInverse(false), () => !_blurInverse, (g, r, c) => DrawBlurModeIcon(g, r, c, false)));
            _buttons.Add(new UiButton("blurInverse", new Rectangle(px + pad + b + gap, py + pad, b, b),
                () => SetBlurInverse(true), () => _blurInverse, (g, r, c) => DrawBlurModeIcon(g, r, c, true)));
        }

        // Szöveg panel: [A méret A] | [nincs · fehér · fekete · sárga háttér] | [− fedés% +]
        _fontLabelRect = _opacityLabelRect = Rectangle.Empty;
        _textSepX.Clear();
        if (_tool == Tool.Text)
        {
            int lw = (int)(40 * S), ow = (int)(48 * S), sp = (int)(13 * S);
            int nBg = TextBackgrounds.Length;
            int pw = pad * 2 + b * 2 + lw + sp + nBg * b + (nBg - 1) * gap + sp + b * 2 + ow;
            int py = y - h - (int)(8 * S);
            if (py < outer) py = y + h + (int)(8 * S);
            int px = Math.Clamp(textBtn.X + b / 2 - pw / 2, outer, Math.Max(outer, ScreenRect.Width - pw - outer));
            _panels.Add(new Rectangle(px, py, pw, h));
            _textSepTop = py + (int)(12 * S);
            _textSepBottom = py + h - (int)(12 * S);

            // Betűméret
            int tx = px + pad;
            _buttons.Add(new UiButton("fontDown", new Rectangle(tx, py + pad, b, b),
                () => StepFont(-1), () => false, (g, r, c) => DrawText(g, "A", r, c, 11)));
            _fontLabelRect = new Rectangle(tx + b, py, lw, h);
            _buttons.Add(new UiButton("fontUp", new Rectangle(tx + b + lw, py + pad, b, b),
                () => StepFont(1), () => false, (g, r, c) => DrawText(g, "A", r, c, 17)));
            tx += b * 2 + lw + sp;
            _textSepX.Add(tx - sp / 2);

            // Háttérszín
            for (int i = 0; i < nBg; i++)
            {
                var bgc = TextBackgrounds[i];
                _buttons.Add(new UiButton("bg" + i, new Rectangle(tx, py + pad, b, b),
                    () => SetTextBackground(bgc), () => false,
                    (g, r, c) => DrawBackgroundSwatch(g, r, bgc, c.A), Plain: true));
                tx += b + gap;
            }
            tx += sp - gap;
            _textSepX.Add(tx - sp / 2);

            // Fedés (átlátszóság) – nincs háttér esetén halvány
            bool hasBg = !_textBg.IsEmpty;
            _buttons.Add(new UiButton("opDown", new Rectangle(tx, py + pad, b, b),
                () => StepOpacity(-1), () => false, (g, r, c) => DrawText(g, "−", r, hasBg ? c : DisabledText, 16), Plain: !hasBg));
            _opacityLabelRect = new Rectangle(tx + b, py, ow, h);
            _buttons.Add(new UiButton("opUp", new Rectangle(tx + b + ow, py + pad, b, b),
                () => StepOpacity(1), () => false, (g, r, c) => DrawText(g, "+", r, hasBg ? c : DisabledText, 16), Plain: !hasBg));
        }

        // Nyílbeállítás panel a vonal eszközhöz
        if (_tool == Tool.Line)
        {
            int pw = pad * 2 + b * 3 + gap * 2;
            int py = y - h - (int)(8 * S);
            if (py < outer) py = y + h + (int)(8 * S);
            int px = Math.Clamp(lineBtn.X + b / 2 - pw / 2, outer, Math.Max(outer, ScreenRect.Width - pw - outer));
            _panels.Add(new Rectangle(px, py, pw, h));
            _buttons.Add(new UiButton("arrowStart", new Rectangle(px + pad, py + pad, b, b),
                () => ToggleArrow(true), () => _arrowStart, (g, r, c) => DrawArrowIcon(g, r, c, true)));
            _buttons.Add(new UiButton("arrowSwap", new Rectangle(px + pad + b + gap, py + pad, b, b),
                SwapArrows, () => false, DrawSwapIcon));
            _buttons.Add(new UiButton("arrowEnd", new Rectangle(px + pad + (b + gap) * 2, py + pad, b, b),
                () => ToggleArrow(false), () => _arrowEnd, (g, r, c) => DrawArrowIcon(g, r, c, false)));
        }
    }

    private void DrawToolbar(Graphics g, int a)
    {
        using (var bg = new SolidBrush(Color.FromArgb(a * 235 / 255, Ui.Bg)))
            foreach (var p in _panels)
            {
                // A gombsorok kapszula alakúak, az előzmények rácsa viszont téglalap, enyhe lekerekítéssel
                float radius = _historyOpen && p == _historyPanel ? HistPanelRadius : p.Height / 2f;
                using var path = Ui.Rounded(p, radius);
                g.FillPath(bg, path);
            }

        using (var sp = new Pen(Color.FromArgb(a * 70 / 255, Color.White), 1f * S))
        {
            g.DrawLine(sp, _sepX, _sepTop, _sepX, _sepBottom);
            if (_objSepX >= 0) g.DrawLine(sp, _objSepX, _objSepY.top, _objSepX, _objSepY.bottom);
        }

        // Korábbi képek: cím (vagy üres állapot)
        if (_historyOpen && _history != null)
        {
            var titleR = new Rectangle(_historyPanel.X, _historyPanel.Y + HistPad - (int)(2 * S), _historyPanel.Width, (int)(28 * S));
            string title = _history.Count == 0 ? "Még nincs korábbi kép – a Másolás után jelennek meg itt"
                : "Korábbi képek  ·  kattints egyre, és tovább szerkesztheted";
            DrawText(g, title, titleR, Ui.Muted, 12, semibold: false);
        }

        foreach (var btn in _buttons)
        {
            bool active = btn.Active();
            bool hover = btn.Id == _hoverId, press = btn.Id == _pressId;
            if (!btn.Plain && (active || hover))
            {
                var fill = active ? Color.FromArgb(a, 70, 70, 80) : Color.FromArgb(a, 45, 45, 52);
                using var path = Ui.Rounded(btn.R, btn.R.Height / 2f);
                using var br = new SolidBrush(press ? ControlPaint.Dark(fill, 0.05f) : fill);
                g.FillPath(br, path);
            }
            var iconColor = active || hover ? Color.FromArgb(a, Ui.Text) : Color.FromArgb(a * 170 / 255, Ui.Text);
            btn.Icon(g, btn.R, iconColor);
        }
        if (!_fontLabelRect.IsEmpty)
            DrawText(g, _fontSize.ToString(), _fontLabelRect, Color.FromArgb(a, Ui.Text), 13, semibold: false);
        if (!_opacityLabelRect.IsEmpty)
        {
            var oc = _textBg.IsEmpty ? DisabledText : Color.FromArgb(a, Ui.Text);
            DrawText(g, $"{_textBgOpacity}%", _opacityLabelRect, oc, 13, semibold: false);
        }
        using (var tsp = new Pen(Color.FromArgb(a * 70 / 255, Color.White), 1f * S))
            foreach (var sx in _textSepX) g.DrawLine(tsp, sx, _textSepTop, sx, _textSepBottom);

        DrawTooltip(g, a);
    }

    private void DrawText(Graphics g, string text, Rectangle r, Color c, float px, bool semibold = true)
    {
        using var font = new Font(semibold ? "Segoe UI Semibold" : "Segoe UI", px * S, GraphicsUnit.Pixel);
        TextRenderer.DrawText(g, text, font, r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// <summary>Méretkijelző: kattintásra nyílik a képarány-menü. Aktív aránynál kiemelt.</summary>
    private void DrawSizeLabel(Graphics g, Rectangle r, Color c)
    {
        var col = _ratioName != null || _ratioMenuOpen || _hoverId == "size" ? c : Color.FromArgb(c.A, Ui.Muted);
        DrawText(g, _sizeText, r, col, 12, semibold: false);
    }

    /// <summary>
    /// Háttérszín-minta: lekerekített négyzet (a szövegszín köreitől eltérő forma).
    /// "Nincs háttér": üres négyzet áthúzással.
    /// </summary>
    private void DrawBackgroundSwatch(Graphics g, Rectangle r, Color col, int a)
    {
        a = (int)(255 * _ui);
        float d = 16 * S;
        var sq = new RectangleF(r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
        using var path = Ui.Rounded(sq, 3.5f * S);
        if (col.IsEmpty)
        {
            using var p = new Pen(Color.FromArgb(a * 200 / 255, Ui.Text), 1.4f * S);
            g.DrawPath(p, path);
            g.DrawLine(p, sq.Left + 2 * S, sq.Bottom - 2 * S, sq.Right - 2 * S, sq.Top + 2 * S);
        }
        else
        {
            using var br = new SolidBrush(Color.FromArgb(a, col));
            g.FillPath(br, path);
            using var edge = new Pen(Color.FromArgb(a * 90 / 255, Color.White), 1f * S);
            g.DrawPath(edge, path);
        }

        bool active = _textBg == col, hover = _hoverId == "bg" + Array.IndexOf(TextBackgrounds, col);
        if (active || hover)
        {
            var ring = RectangleF.Inflate(sq, 4 * S, 4 * S);
            using var rp = Ui.Rounded(ring, 6 * S);
            using var pen = new Pen(Color.FromArgb(active ? a : a * 110 / 255, Color.White), 1.6f * S);
            g.DrawPath(pen, rp);
        }
    }

    /// <summary>
    /// Elmosás mód ikonja. Normál: keret, benne mozaik.
    /// Inverz: mozaik körben, a közepén üres (éles) téglalap.
    /// </summary>
    private void DrawBlurModeIcon(Graphics g, Rectangle r, Color c, bool inverse)
    {
        var outer = RectangleF.Inflate(r, -8 * S, -8 * S);
        var inner = RectangleF.Inflate(outer, -outer.Width * 0.24f, -outer.Height * 0.24f);
        using var pen = new Pen(c, 1.4f * S);

        // Mozaikminta 4×4 cellában; normálnál csak belül, inverznél csak a belső téglalapon kívül
        float[] shade = { 0.9f, 0.4f, 0.7f, 0.35f, 0.5f, 0.85f, 0.3f, 0.75f, 0.8f, 0.3f, 0.9f, 0.45f, 0.4f, 0.7f, 0.5f, 0.9f };
        var area = inverse ? outer : inner;
        float cw = area.Width / 4, ch = area.Height / 4;
        for (int i = 0; i < 16; i++)
        {
            var cell = new RectangleF(area.X + (i % 4) * cw, area.Y + (i / 4) * ch, cw, ch);
            if (inverse && inner.IntersectsWith(RectangleF.Inflate(cell, -0.5f, -0.5f))) continue;
            using var br = new SolidBrush(Color.FromArgb((int)(c.A * shade[i] * 0.85f), c));
            g.FillRectangle(br, cell);
        }

        using (var p1 = Ui.Rounded(outer, 2 * S)) g.DrawPath(pen, p1);
        if (inverse) using (var p2 = Ui.Rounded(inner, 1.5f * S)) g.DrawPath(pen, p2);
    }

    /// <summary>Mentési mappa választása a fagyasztott réteg fölött.</summary>
    /// <summary>
    /// Objektum mini panel: csak akkor látszik, ha van kijelölt objektum, és mellette lebeg
    /// (a jobb felső sarka fölött; ha ott nincs hely, alatta).
    /// [leghátra · hátrébb · előrébb · legelőre | törlés]
    /// Húzás, mozgatás és rajzolás közben elrejtjük, hogy ne zavarjon.
    /// </summary>
    private void LayoutObjectPanel(int h, int pad, int b, int gap)
    {
        _objSepX = -1;
        if (_selected == null || _drawing != null || _movingAnn || _resizingAnn || _drag != Hit.None) return;

        int sep = (int)(11 * S), margin = (int)(10 * S), outer = (int)(14 * S);
        int pw = pad * 2 + b * 5 + gap * 3 + sep;
        var box = _selected.Box;
        box.Inflate((int)(8 * S), (int)(8 * S));
        var scr = ScreenRect;
        // Dokumentum → képernyő (hosszú képnél eltolás és görgetés)
        var bs = new Rectangle(box.X + OffX, box.Y - _viewY, box.Width, box.Height);

        int px = Math.Clamp(bs.Right - pw, outer, Math.Max(outer, scr.Width - pw - outer));
        int py = bs.Top - h - margin;
        if (py < outer) py = bs.Bottom + margin;                       // fölötte nincs hely → alatta
        if (py + h > scr.Height - outer) py = Math.Max(outer, bs.Top + margin);   // alatta sincs → belül
        var panel = new Rectangle(px, py, pw, h);

        // Ne takarja el a fő eszköztárat
        if (_panels.Any(p => p.IntersectsWith(panel))) panel.Y = Math.Max(outer, _panels.Min(p => p.Top) - h - margin);
        _panels.Add(panel);

        var orders = new[] { ZOrder.ToBack, ZOrder.Backward, ZOrder.Forward, ZOrder.ToFront };
        for (int i = 0; i < orders.Length; i++)
        {
            var z = orders[i];
            var r = new Rectangle(panel.X + pad + i * (b + gap), panel.Y + pad, b, b);
            _buttons.Add(new UiButton("z" + z, r, () => Reorder(z), () => false,
                (g, rr, c) => DrawOrderIcon(g, z, rr, c), Plain: !CanReorder(z)));
        }
        _objSepX = panel.X + pad + 4 * b + 3 * gap + sep / 2;
        _objSepY = (panel.Top + (int)(12 * S), panel.Bottom - (int)(12 * S));
        _buttons.Add(new UiButton("delete", new Rectangle(panel.X + pad + 4 * b + 3 * gap + sep, panel.Y + pad, b, b),
            DeleteSelected, () => false, DrawTrashIcon));
    }

    private int _objSepX = -1;
    private (int top, int bottom) _objSepY;

    private void DeleteSelected()
    {
        if (_selected == null) return;
        var target = _selected;
        StopEditing();
        if (_selected == null) { Invalidate(); return; }   // üres szöveg volt, már törlődött
        _anns.Remove(target);
        _selected = null;
        Invalidate();
    }

    /// <summary>Kuka ikon: fedél fogantyúval, lefelé szűkülő test két csíkkal.</summary>
    private void DrawTrashIcon(Graphics g, Rectangle r, Color c)
    {
        var ib = RectangleF.Inflate(r, -10.5f * S, -9.5f * S);
        using var pen = new Pen(c, 1.6f * S) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float lidY = ib.Top + ib.Height * 0.2f;
        g.DrawLine(pen, ib.Left, lidY, ib.Right, lidY);
        g.DrawLines(pen, new[] { new PointF(ib.Left + ib.Width * 0.36f, lidY), new PointF(ib.Left + ib.Width * 0.36f, ib.Top),
                                 new PointF(ib.Right - ib.Width * 0.36f, ib.Top), new PointF(ib.Right - ib.Width * 0.36f, lidY) });
        g.DrawLines(pen, new[] { new PointF(ib.Left + ib.Width * 0.1f, lidY), new PointF(ib.Left + ib.Width * 0.18f, ib.Bottom),
                                 new PointF(ib.Right - ib.Width * 0.18f, ib.Bottom), new PointF(ib.Right - ib.Width * 0.1f, lidY) });
        float cy0 = lidY + ib.Height * 0.2f, cy1 = ib.Bottom - ib.Height * 0.16f;
        g.DrawLine(pen, ib.Left + ib.Width * 0.4f, cy0, ib.Left + ib.Width * 0.4f, cy1);
        g.DrawLine(pen, ib.Right - ib.Width * 0.4f, cy0, ib.Right - ib.Width * 0.4f, cy1);
    }

    // ================= Korábbi képek =================

    /// <summary>Egy korábbi képre kattintott: töltődjön be szerkeszthetően (kép, vágás, objektumok).</summary>
    public event Action<FreezeForm, HistoryItem>? HistoryPicked;

    private bool _historyOpen;
    private List<(HistoryItem item, Bitmap? thumb)>? _history;

    private void ToggleHistory()
    {
        _historyOpen = !_historyOpen;
        if (_historyOpen)
        {
            _ratioMenuOpen = false;
            _history = History.List().Select(i => (i, History.LoadThumb(i))).ToList();
        }
        else DisposeHistory();
        Invalidate();
    }

    private void DisposeHistory()
    {
        if (_history != null) foreach (var (_, t) in _history) t?.Dispose();
        _history = null;
    }

    /// <summary>Legördülő rács a legutóbbi képekkel (5 oszlop), a gomb fölött; ha ott nincs hely, alatta.</summary>
    private void LayoutHistory(Rectangle btn, Rectangle copyPanel, int h, int pad, int gap)
    {
        if (_history == null) return;
        pad = HistPad;   // a rácsnak több levegő kell, mint a gombsornak
        int cols = 5, cw = (int)(150 * S), ch = (int)(112 * S), cg = (int)(6 * S), outer = (int)(14 * S);
        int n = _history.Count, rows = Math.Max(1, (n + cols - 1) / cols);
        int pw = pad * 2 + cols * cw + (cols - 1) * cg;
        int ph = pad * 2 + rows * ch + (rows - 1) * cg + (int)(28 * S);   // fölül cím
        var scr = ScreenRect;

        int px = Math.Clamp(btn.X - pad, outer, Math.Max(outer, scr.Width - pw - outer));
        int py = copyPanel.Top - ph - (int)(8 * S);
        if (py < outer) py = copyPanel.Bottom + (int)(8 * S);
        var panel = new Rectangle(px, py, pw, ph);
        _panels.Add(panel);
        _historyPanel = panel;

        for (int i = 0; i < n; i++)
        {
            var (item, thumb) = _history[i];
            var r = new Rectangle(px + pad + (i % cols) * (cw + cg), py + pad + (int)(28 * S) + (i / cols) * (ch + cg), cw, ch);
            string id = "hist" + i;
            _buttons.Add(new UiButton(id, r, () =>
            {
                _historyOpen = false;
                StopEditing();
                HistoryPicked?.Invoke(this, item);
            }, () => false, (g, rr, c) => DrawHistoryCell(g, rr, thumb, item, id), Plain: true));
        }
    }

    private Rectangle _historyPanel;

    // A panel és a cellák lekerekítése egymáshoz igazodik: külső sugár = cella sugara + belső margó
    private int HistPad => (int)(10 * S);
    private float HistCellRadius => 8 * S;
    private float HistPanelRadius => HistCellRadius + HistPad;

    private void DrawHistoryCell(Graphics g, Rectangle r, Bitmap? thumb, HistoryItem item, string id)
    {
        bool hover = _hoverId == id;
        int labelH = (int)(22 * S);
        var imgArea = new Rectangle(r.X + (int)(4 * S), r.Y + (int)(4 * S), r.Width - (int)(8 * S), r.Height - labelH - (int)(4 * S));

        using (var bg = new SolidBrush(hover ? Color.FromArgb(55, 55, 64) : Color.FromArgb(36, 36, 42)))
        using (var p = Ui.Rounded(r, HistCellRadius))
            g.FillPath(bg, p);

        if (thumb != null)
        {
            // Arányosan a cella képterületébe
            double k = Math.Min(imgArea.Width / (double)thumb.Width, imgArea.Height / (double)thumb.Height);
            int tw = (int)(thumb.Width * k), th = (int)(thumb.Height * k);
            var dst = new Rectangle(imgArea.X + (imgArea.Width - tw) / 2, imgArea.Y + (imgArea.Height - th) / 2, tw, th);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(thumb, dst);
            using var edge = new Pen(Color.FromArgb(70, 255, 255, 255), 1f);
            g.DrawRectangle(edge, dst);
        }

        if (hover)
        {
            using var hp = new Pen(Ui.Accent, 2f * S);
            using var p = Ui.Rounded(r, HistCellRadius);
            g.DrawPath(hp, p);
        }

        // Időpont: ma "14:32", korábban "nov. 21. 14:32"
        var t = item.Time;
        string label = t.Date == DateTime.Today ? t.ToString("HH:mm")
            : t.ToString("MMM d. HH:mm", System.Globalization.CultureInfo.GetCultureInfo("hu-HU"));
        DrawText(g, label, new Rectangle(r.X, r.Bottom - labelH, r.Width, labelH),
            hover ? Ui.Text : Ui.Muted, 11.5f, semibold: false);
    }

    /// <summary>Óra ikon kis lefelé nyíllal (legördülő).</summary>
    private void DrawHistoryIcon(Graphics g, Rectangle r, Color c)
    {
        // Visszafelé forduló kör alakú nyíl (bal oldalt nyitott) + óramutatók, a gomb közepén
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, R = 7.5f * S;
        using var pen = new Pen(c, 1.6f * S) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

        const float start = 215f, sweep = 290f;   // GDI-szög: óramutató irányában, 0° = jobbra
        g.DrawArc(pen, cx - R, cy - R, R * 2, R * 2, start, sweep);

        // Nyílhegy az ív elején, az óramutatóval ellentétes irányba (a rés felé) mutat
        double t = start * Math.PI / 180;
        var tip = new PointF(cx + R * (float)Math.Cos(t), cy + R * (float)Math.Sin(t));
        double dx = Math.Sin(t), dy = -Math.Cos(t);   // érintő, visszafelé
        float a = 3.2f * S;
        PointF Wing(double ang)
        {
            double bx = -dx, by = -dy, cs = Math.Cos(ang), sn = Math.Sin(ang);
            return new PointF(tip.X + a * (float)(bx * cs - by * sn), tip.Y + a * (float)(bx * sn + by * cs));
        }
        g.DrawLines(pen, new[] { Wing(0.75), tip, Wing(-0.75) });

        // Óramutatók
        g.DrawLines(pen, new[] { new PointF(cx, cy - R * 0.5f), new PointF(cx, cy), new PointF(cx + R * 0.38f, cy + R * 0.26f) });
    }

    /// <summary>Az előzménybe mentés (a Másoláskor): eredeti kép, vágás, objektumok, kész kép.</summary>
    public void SaveHistory(Bitmap final)
    {
        StopEditing();
        History.Add(new Bitmap(_shot), _sel, _anns.Select(a => a.Clone()).ToList(), new Bitmap(final));
    }

    /// <summary>Korábbi kép állapotának visszatöltése: vágás és szerkeszthető objektumok.</summary>
    public void LoadState(Rectangle sel, List<Annotation> anns)
    {
        _sel = Rectangle.Intersect(sel, _full);
        if (_sel.Width < 4 || _sel.Height < 4) _sel = _full;
        _anns.Clear();
        _anns.AddRange(anns);
        // Hosszú képnél a nézet a vágás tetejéhez ugrik
        if (_longDoc)
        {
            _viewY = Math.Clamp(_sel.Top - (int)(40 * S), 0, MaxViewY);
            _viewX = Math.Clamp(_sel.Left - (int)(40 * S), 0, MaxViewX);
        }
        Invalidate();
    }

    /// <summary>Hosszú kép (felvétel) ikon: gyűrű, benne piros felvétel-pont.</summary>
    private void DrawLongIcon(Graphics g, Rectangle r, Color c)
    {
        float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f, ro = 8.5f * S, ri = 4.6f * S;
        using (var pen = new Pen(c, 1.6f * S))
            g.DrawEllipse(pen, cx - ro, cy - ro, ro * 2, ro * 2);
        // A piros pont a gomb átlátszóságát követi (letiltva halványabb)
        using var dot = new SolidBrush(Color.FromArgb(c.A, 239, 68, 68));
        g.FillEllipse(dot, cx - ri, cy - ri, ri * 2, ri * 2);
    }

    private void ChooseFolder()
    {
        StopEditing();
        _hoverId = null;
        Settings.ChooseSaveFolder(this);
        Activate();   // vissza a fókusz a rétegre (Esc, Enter, gyorsbillentyűk)
        Invalidate();
    }

    /// <summary>Mappa ikon: fül a bal felső sarkon, lekerekített test.</summary>
    private void DrawFolderIcon(Graphics g, Rectangle r, Color c)
    {
        var ib = RectangleF.Inflate(r, -9.5f * S, -10.5f * S);
        float tabW = ib.Width * 0.42f, tabH = 2.6f * S, rad = 1.8f * S;
        using var pen = new Pen(c, 1.6f * S) { LineJoin = LineJoin.Round };
        using var path = new GraphicsPath();
        path.AddLine(ib.Left, ib.Bottom - rad, ib.Left, ib.Top + rad);
        path.AddArc(ib.Left, ib.Top, rad * 2, rad * 2, 180, 90);
        path.AddLine(ib.Left + rad, ib.Top, ib.Left + tabW, ib.Top);
        path.AddLine(ib.Left + tabW + tabH, ib.Top + tabH, ib.Right - rad, ib.Top + tabH);
        path.AddArc(ib.Right - rad * 2, ib.Top + tabH, rad * 2, rad * 2, 270, 90);
        path.AddLine(ib.Right, ib.Top + tabH + rad, ib.Right, ib.Bottom - rad);
        path.AddArc(ib.Right - rad * 2, ib.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
        path.AddLine(ib.Right - rad, ib.Bottom, ib.Left + rad, ib.Bottom);
        path.AddArc(ib.Left, ib.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
        path.CloseFigure();
        g.DrawPath(pen, path);
    }

    private void DrawCopyButton(Graphics g, Rectangle r, Color c)
    {
        var accent = _pressId == "copy" ? ControlPaint.Dark(Ui.Accent, 0.1f)
            : _hoverId == "copy" ? ControlPaint.Light(Ui.Accent, 0.25f) : Ui.Accent;
        using (var path = Ui.Rounded(r, r.Height / 2f))
        using (var bb = new SolidBrush(Color.FromArgb(c.A, accent)))
            g.FillPath(bb, path);
        // Tartalom: "[másolás ikon]  Másolás" – középre igazítva
        using var font = new Font("Segoe UI Semibold", 14 * S, GraphicsUnit.Pixel);
        var flags = TextFormatFlags.NoPadding;
        int wText = TextRenderer.MeasureText("Másolás", font, Size.Empty, flags).Width;
        float icon = 17 * S, iconGap = 8 * S;
        float x = r.X + (r.Width - (icon + iconGap + wText)) / 2, cy = r.Y + r.Height / 2f;
        DrawClipboardGlyph(g, new RectangleF(x, cy - icon / 2, icon, icon), Color.FromArgb(c.A, Color.White));
        TextRenderer.DrawText(g, "Másolás", font, new Point((int)(x + icon + iconGap), (int)(cy - font.Height / 2f)), Color.White, flags);
    }

    /// <summary>
    /// Mentés kapcsoló: modern csúszka + floppy ikon. Bekapcsolva a Másolás PNG-be is ment
    /// (lila csúszka, fehér floppy), kikapcsolva csak vágólapra másol (szürke csúszka, halvány floppy).
    /// </summary>
    private void DrawSaveToggle(Graphics g, Rectangle r, Color c)
    {
        bool on = Settings.SaveEnabled;
        float tw = 30 * S, th = 18 * S, icon = 17 * S, gap = 7 * S;
        float x = r.X + (r.Width - (tw + gap + icon)) / 2, cy = r.Y + r.Height / 2f;

        // Csúszka
        var track = new RectangleF(x, cy - th / 2, tw, th);
        var trackCol = on ? Ui.Accent : Color.FromArgb(72, 72, 82);
        if (_hoverId == "save") trackCol = ControlPaint.Light(trackCol, 0.2f);
        using (var p = Ui.Rounded(track, th / 2))
        using (var b = new SolidBrush(Color.FromArgb(c.A, trackCol)))
            g.FillPath(b, p);
        float k = th - 5 * S;
        float kx = on ? track.Right - k - 2.5f * S : track.Left + 2.5f * S;
        using (var kb = new SolidBrush(Color.FromArgb(c.A, Color.White)))
            g.FillEllipse(kb, kx, cy - k / 2, k, k);

        // Floppy: be → fehér, ki → halvány
        var fc = on ? Color.FromArgb(c.A, Color.White) : Color.FromArgb(c.A * 110 / 255, Ui.Text);
        DrawFloppyGlyph(g, new RectangleF(track.Right + gap, cy - icon / 2, icon, icon), fc, Ui.Bg);
    }

    /// <summary>
    /// Másolás ikon (a kódblokkok másolás gombjáról ismert forma):
    /// két egymásra csúsztatott lekerekített lap, a hátsóból csak a bal felső sarok látszik.
    /// 24-es rácson rajzolva, az ikon dobozára méretezve.
    /// </summary>
    private void DrawClipboardGlyph(Graphics g, RectangleF r, Color c)
    {
        float u = r.Width / 20f;                       // a 2..22 tartomány kitölti a dobozt
        float ox = r.Left - 2 * u, oy = r.Top - 2 * u;
        PointF P(float x, float y) => new(ox + x * u, oy + y * u);
        RectangleF R(float x, float y, float w, float h) => new(ox + x * u, oy + y * u, w * u, h * u);

        using var pen = new Pen(c, 1.7f * S) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };

        // Elülső lap: x=8, y=8, 14×14, sarok 2
        using (var front = Ui.Rounded(R(8, 8, 14, 14), 2 * u)) g.DrawPath(pen, front);

        // Hátsó lap: csak a látható bal-felső "L" rész
        using var back = new GraphicsPath();
        back.AddArc(R(2, 12, 4, 4), 90, 90);           // (4,16) → (2,14)
        back.AddLine(P(2, 14), P(2, 4));
        back.AddArc(R(2, 2, 4, 4), 180, 90);           // (2,4) → (4,2)
        back.AddLine(P(4, 2), P(14, 2));
        back.AddArc(R(12, 2, 4, 4), 270, 90);          // (14,2) → (16,4)
        g.DrawPath(pen, back);
    }

    /// <summary>Floppy: lekerekített, jobb felső sarkán levágott test, fent kitöltött zár, lent címke.</summary>
    private void DrawFloppyGlyph(Graphics g, RectangleF r, Color c, Color? slotColor = null)
    {
        using var pen = new Pen(c, 1.6f * S) { LineJoin = LineJoin.Round };
        var body = RectangleF.Inflate(r, -r.Width * 0.06f, -r.Height * 0.06f);
        float cut = body.Width * 0.24f, rad = 2 * S;

        using (var path = new GraphicsPath())
        {
            path.AddArc(body.Left, body.Top, rad * 2, rad * 2, 180, 90);
            path.AddLine(body.Left + rad, body.Top, body.Right - cut, body.Top);
            path.AddLine(body.Right - cut, body.Top, body.Right, body.Top + cut);
            path.AddLine(body.Right, body.Top + cut, body.Right, body.Bottom - rad);
            path.AddArc(body.Right - rad * 2, body.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
            path.AddArc(body.Left, body.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
            path.CloseFigure();
            g.DrawPath(pen, path);
        }

        // Fém zár fent (kitöltve, rajta rés)
        var shutter = RectangleF.FromLTRB(body.Left + body.Width * 0.24f, body.Top, body.Right - body.Width * 0.32f, body.Top + body.Height * 0.32f);
        using (var br = new SolidBrush(c)) g.FillRectangle(br, shutter);
        using (var slot = new SolidBrush(slotColor ?? Ui.Accent))
            g.FillRectangle(slot, shutter.Right - shutter.Width * 0.38f, shutter.Top + shutter.Height * 0.2f, shutter.Width * 0.2f, shutter.Height * 0.6f);

        // Címke lent
        var lbl = RectangleF.FromLTRB(body.Left + body.Width * 0.2f, body.Top + body.Height * 0.56f, body.Right - body.Width * 0.2f, body.Bottom);
        using (var p = Ui.Rounded(lbl, 1.2f * S)) g.DrawPath(pen, p);
    }

    private void DrawSwatch(Graphics g, Rectangle r, Color col, int a)
    {
        float d = 16 * S;
        var cr = new RectangleF(r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
        using (var br = new SolidBrush(Color.FromArgb(a, col)))
            g.FillEllipse(br, cr);
        using (var edge = new Pen(Color.FromArgb(a * 90 / 255, Color.White), 1f * S))
            g.DrawEllipse(edge, cr);

        bool active = _color == col, hover = _hoverId == "color" + col.ToArgb();
        if (active || hover)
        {
            var ring = RectangleF.Inflate(cr, 4 * S, 4 * S);
            using var rp = new Pen(Color.FromArgb(active ? a : a * 110 / 255, Color.White), 1.6f * S);
            g.DrawEllipse(rp, ring);
        }
    }

    private void DrawToolIcon(Graphics g, Tool t, Rectangle r, Color c)
    {
        var ib = RectangleF.Inflate(r, -10 * S, -10 * S);
        using var pen = new Pen(c, 1.7f * S) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float u = 3 * S;

        switch (t)
        {
            case Tool.Crop:
                g.DrawLines(pen, new[] { new PointF(ib.Left + u, ib.Top - u / 2), new PointF(ib.Left + u, ib.Bottom - u), new PointF(ib.Right + u / 2, ib.Bottom - u) });
                g.DrawLines(pen, new[] { new PointF(ib.Left - u / 2, ib.Top + u), new PointF(ib.Right - u, ib.Top + u), new PointF(ib.Right - u, ib.Bottom + u / 2) });
                break;

            case Tool.Blur:
                float[] shade = { 0.9f, 0.35f, 0.7f, 0.3f, 0.8f, 0.45f, 0.6f, 0.3f, 0.9f };
                float cell = ib.Width / 3;
                for (int i = 0; i < 9; i++)
                {
                    using var br = new SolidBrush(Color.FromArgb((int)(c.A * shade[i]), c));
                    g.FillRectangle(br, ib.X + (i % 3) * cell + 0.5f * S, ib.Y + (i / 3) * cell + 0.5f * S, cell - 1f * S, cell - 1f * S);
                }
                break;

            case Tool.Oval:
                g.DrawEllipse(pen, ib);
                break;

            case Tool.Rect:
                using (var path = Ui.Rounded(ib, 2 * S)) g.DrawPath(pen, path);
                break;

            case Tool.Line:
                using (var cap = new AdjustableArrowCap(3.5f, 3.5f, true))
                {
                    if (_arrowStart) pen.CustomStartCap = cap;
                    if (_arrowEnd) pen.CustomEndCap = cap;
                    g.DrawLine(pen, ib.Left, ib.Bottom, ib.Right, ib.Top);
                }
                break;

            case Tool.Pen:
                g.DrawBezier(pen,
                    new PointF(ib.Left, ib.Bottom - u), new PointF(ib.Left + ib.Width * 0.35f, ib.Top - u * 2),
                    new PointF(ib.Left + ib.Width * 0.6f, ib.Bottom + u * 2), new PointF(ib.Right, ib.Top + u));
                break;

            case Tool.Hand:
            {
                // Nyitott tenyér: négy ujj (különböző hosszal), tenyér nyitott tetővel, oldalt hüvelykujj
                float l = ib.Left + ib.Width * 0.24f, rr = ib.Right - ib.Width * 0.04f;
                float palmTop = ib.Top + ib.Height * 0.48f, bot = ib.Bottom + u * 0.4f, rad = (rr - l) * 0.38f;
                using (var palm = new GraphicsPath())
                {
                    palm.AddLine(l, palmTop, l, bot - rad);
                    palm.AddArc(l, bot - rad * 2, rad * 2, rad * 2, 180, -90);
                    palm.AddLine(l + rad, bot, rr - rad, bot);
                    palm.AddArc(rr - rad * 2, bot - rad * 2, rad * 2, rad * 2, 90, -90);
                    palm.AddLine(rr, bot - rad, rr, palmTop);
                    g.DrawPath(pen, palm);
                }
                float[] tops = { 0.16f, -0.06f, 0.0f, 0.18f };
                for (int i = 0; i < 4; i++)
                {
                    float fx = l + (rr - l) * i / 3f;
                    g.DrawLine(pen, fx, palmTop + u * 0.3f, fx, ib.Top + ib.Height * tops[i]);
                }
                g.DrawLine(pen, l, ib.Top + ib.Height * 0.74f, ib.Left - u * 0.3f, ib.Top + ib.Height * 0.5f);
                break;
            }

            case Tool.Text:
                // Nagy "T": vízszintes felső szár és függőleges törzs
                float cx = ib.Left + ib.Width / 2;
                g.DrawLine(pen, ib.Left + u * 0.3f, ib.Top + u * 0.3f, ib.Right - u * 0.3f, ib.Top + u * 0.3f);
                g.DrawLine(pen, cx, ib.Top + u * 0.3f, cx, ib.Bottom);
                break;
        }
    }

    private void DrawArrowIcon(Graphics g, Rectangle r, Color c, bool start)
    {
        var ib = RectangleF.Inflate(r, -9 * S, -9 * S);
        using var pen = new Pen(c, 1.7f * S) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var cap = new AdjustableArrowCap(3.5f, 3.5f, true);
        if (start) pen.CustomStartCap = cap; else pen.CustomEndCap = cap;
        float y = ib.Top + ib.Height / 2;
        g.DrawLine(pen, ib.Left, y, ib.Right, y);
    }

    /// <summary>Körbeforduló csere-ikon: két ív, mindkettő végén nyílhegy.</summary>
    private void DrawSwapIcon(Graphics g, Rectangle r, Color c)
    {
        var ib = RectangleF.Inflate(r, -10 * S, -10 * S);
        using var pen = new Pen(c, 1.7f * S) { StartCap = LineCap.Round };
        using var cap = new AdjustableArrowCap(3f, 3f, true);
        pen.CustomEndCap = cap;
        g.DrawArc(pen, ib, 200, 130);
        g.DrawArc(pen, ib, 20, 130);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { DisposeHistory(); _caretTimer.Dispose(); _edgeTimer.Dispose(); _shot.Dispose(); _dim?.Dispose(); _blurSmall?.Dispose(); }
        base.Dispose(disposing);
    }
}
