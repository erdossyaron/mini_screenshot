using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Screen;

/// <summary>A hosszú kép összerakója: görgetés (LongStitcher) vagy pásztázás (PanStitcher).</summary>
internal interface IStitcher : IDisposable
{
    LongStitcher.Result Add(Bitmap frame);
    Bitmap Compose();
    bool Calibrated { get; }
    /// <summary>Az előnézetben mutatott rész mérete (az arányhoz).</summary>
    Size PreviewSize { get; }
    /// <summary>A kész kép várható mérete, szövegként ("1 234 px" vagy "3 200 × 2 100 px").</summary>
    string SizeLabel { get; }
    void DrawPreview(Graphics g, RectangleF dest);
    /// <summary>Napló: az utolsó képkocka feldolgozása.</summary>
    string FrameInfo { get; }
    /// <summary>Napló: összegzés a végén.</summary>
    string Summary { get; }
}

/// <summary>Egy képkocka pixelei (ARGB), gyors eléréshez.</summary>
internal sealed class Pixmap
{
    public readonly int W, H;
    public readonly int[] Px;

    public Pixmap(int w, int h, int[] px) { W = w; H = h; Px = px; }

    public static Pixmap From(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var px = new int[bmp.Width * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, px, y * bmp.Width, bmp.Width);
            return new Pixmap(bmp.Width, bmp.Height, px);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public static bool Near(int p, int q, int tol = 30)
    {
        int d = Math.Abs((p & 0xFF) - (q & 0xFF)) + Math.Abs(((p >> 8) & 0xFF) - ((q >> 8) & 0xFF))
              + Math.Abs(((p >> 16) & 0xFF) - ((q >> 16) & 0xFF));
        return d <= tol;
    }

    public static int Luma(int p) => (((p >> 16) & 0xFF) * 2 + ((p >> 8) & 0xFF) * 5 + (p & 0xFF)) >> 3;
}

/// <summary>
/// Két képkocka közötti kétirányú eltolás becslése.
/// 1. Fáziskorreláció kicsinyített szürke képen (FFT): bármekkora, a képkocka felénél kisebb eltolást
///    megtalál, és nem zavarja, ha a kép egy része animált vagy mozdulatlan (eszköztár).
/// 2. A legerősebb csúcsokat teljes felbontásban, mintapontokon pontosítja és ellenőrzi:
///    a mintapontok (élek, szöveg) hány százaléka egyezik az eltolt helyen.
/// Megállapodás: a "b" képkocka p pontja az "a" képkocka p + d pontjának felel meg.
/// </summary>
internal sealed class ShiftEstimator
{
    private const int Peaks = 8;
    public const double MinScore = 0.55;     // a mintapontok legalább ekkora része egyezzen

    private readonly int _w, _h, _f, _gw, _gh, _n, _m;
    private readonly double[] _win;

    public ShiftEstimator(int w, int h)
    {
        _w = w;
        _h = h;
        _f = Math.Max(4, (int)Math.Ceiling(Math.Max(w / 512.0, h / 512.0)));
        _gw = Math.Max(1, w / _f);
        _gh = Math.Max(1, h / _f);
        _n = (int)BitOperations.RoundUpToPowerOf2((uint)_gw);
        _m = (int)BitOperations.RoundUpToPowerOf2((uint)_gh);
        _win = new double[_gw * _gh];
        for (int y = 0; y < _gh; y++)
            for (int x = 0; x < _gw; x++)
                _win[y * _gw + x] = (0.5 - 0.5 * Math.Cos(2 * Math.PI * (x + 0.5) / _gw)) * (0.5 - 0.5 * Math.Cos(2 * Math.PI * (y + 0.5) / _gh));
    }

    /// <summary>A képkocka spektruma (egyszer számoljuk, a következő becslésnél újra felhasználható).</summary>
    public (double[] re, double[] im) Spectrum(Pixmap p)
    {
        var re = new double[_n * _m];
        var im = new double[_n * _m];
        double mean = 0;
        var g = new double[_gw * _gh];
        for (int gy = 0; gy < _gh; gy++)
            for (int gx = 0; gx < _gw; gx++)
            {
                int sum = 0;
                for (int yy = 0; yy < _f; yy++)
                {
                    int row = (gy * _f + yy) * _w + gx * _f;
                    for (int xx = 0; xx < _f; xx++) sum += Pixmap.Luma(p.Px[row + xx]);
                }
                double v = sum / (double)(_f * _f);
                g[gy * _gw + gx] = v;
                mean += v;
            }
        mean /= g.Length;
        for (int gy = 0; gy < _gh; gy++)
            for (int gx = 0; gx < _gw; gx++)
                re[gy * _n + gx] = (g[gy * _gw + gx] - mean) * _win[gy * _gw + gx];
        Fft.Transform2D(re, im, _n, _m, false);
        return (re, im);
    }

    /// <summary>Mintapontok a "b" képkockán: jól látható élek, rácsban (legfeljebb néhány ezer).</summary>
    public List<int> Points(Pixmap b, Func<int, int, bool>? skip)
    {
        int step = Math.Max(3, (int)Math.Sqrt(_w * (double)_h / 18000));
        var pts = new List<int>();
        for (int y = 1; y < _h - 1; y += step)
            for (int x = 1; x < _w - 1; x += step)
            {
                int i = y * _w + x;
                int l = Pixmap.Luma(b.Px[i]);
                int gr = Math.Abs(l - Pixmap.Luma(b.Px[i + 1])) + Math.Abs(l - Pixmap.Luma(b.Px[i + _w]))
                       + Math.Abs(l - Pixmap.Luma(b.Px[i - 1])) + Math.Abs(l - Pixmap.Luma(b.Px[i - _w]));
                if (gr < 24 || (skip != null && skip(x, y))) continue;
                pts.Add(i);
            }
        if (pts.Count > 6000)
        {
            int k = pts.Count / 6000 + 1;
            pts = pts.Where((_, j) => j % k == 0).ToList();
        }
        return pts;
    }

    /// <summary>Egyezési arány az adott eltolásnál (érvényes = az "a" képen belül eső, nem kizárt pont).</summary>
    public (double score, int valid) Score(Pixmap a, Pixmap b, List<int> pts, int dx, int dy, Func<int, int, bool>? skipA)
    {
        int ok = 0, valid = 0;
        foreach (int i in pts)
        {
            int x = i % _w + dx, y = i / _w + dy;
            if (x < 0 || y < 0 || x >= _w || y >= _h || (skipA != null && skipA(x, y))) continue;
            valid++;
            if (Pixmap.Near(b.Px[i], a.Px[y * _w + x])) ok++;
        }
        return (valid == 0 ? 0 : ok / (double)valid, valid);
    }

    /// <summary>
    /// Az eltolás a-ból b-be. Null, ha nincs megbízható egyezés. Moved = false: nem mozdult
    /// (vagy csak egy része változott, pl. animáció).
    /// <paramref name="skip"/>: kizárt pontok (pl. mozdulatlan eszköztár) képkocka-koordinátában.
    /// <paramref name="prior"/>: az előző mozgás; folyamatos pásztázásnál ez a legvalószínűbb jelölt.
    /// </summary>
    public (int dx, int dy, double score, bool moved)? Estimate(Pixmap a, (double[] re, double[] im) sa, Pixmap b,
        (double[] re, double[] im) sb, Func<int, int, bool>? skip, Point prior)
    {
        var all = Points(b, skip);
        if (all.Count < 40) return null;

        // Csak a képernyőn megváltozott pontok mondanak valamit a mozgásról: a helyben maradt
        // eszköztár vagy háttér mindig a "nem mozdult" mellett szavazna
        var pts = new List<int>();
        int same = 0;
        foreach (int i in all)
            if (Pixmap.Near(b.Px[i], a.Px[i])) same++;
            else pts.Add(i);
        double sameRatio = same / (double)all.Count;
        if (pts.Count < Math.Max(20, all.Count / 12)) return (0, 0, sameRatio, false);   // nem mozdult
        int minValid = Math.Max(20, pts.Count / 6);

        // Keresztspektrum (normalizálva) → visszatranszformálva a csúcs az eltolásnál
        int len = _n * _m;
        var re = new double[len];
        var im = new double[len];
        for (int i = 0; i < len; i++)
        {
            double r = sa.re[i] * sb.re[i] + sa.im[i] * sb.im[i];
            double q = sa.im[i] * sb.re[i] - sa.re[i] * sb.im[i];
            double mag = Math.Sqrt(r * r + q * q) + 1e-9;
            re[i] = r / mag;
            im[i] = q / mag;
        }
        Fft.Transform2D(re, im, _n, _m, true);

        // A legerősebb helyi csúcsok
        var cands = new List<(double v, int tx, int ty)>();
        for (int y = 0; y < _m; y++)
            for (int x = 0; x < _n; x++)
            {
                double v = re[y * _n + x];
                bool peak = true;
                for (int yy = -1; yy <= 1 && peak; yy++)
                    for (int xx = -1; xx <= 1; xx++)
                    {
                        if (xx == 0 && yy == 0) continue;
                        int nx = (x + xx + _n) % _n, ny = (y + yy + _m) % _m;
                        if (re[ny * _n + nx] > v) { peak = false; break; }
                    }
                if (!peak) continue;
                int tx = x > _n / 2 ? x - _n : x, ty = y > _m / 2 ? y - _m : y;
                cands.Add((v, tx, ty));
            }
        var ranked = cands.OrderByDescending(c => c.v).Select(c => (c.tx * _f, c.ty * _f)).ToList();
        var top = ranked.Take(Peaks).ToList();
        if (prior != Point.Empty) top.Insert(0, (prior.X, prior.Y));
        // Durva kereséshez a pontok harmada is elég (gyorsabb), a pontosításhoz mind
        var coarse = pts.Where((_, j) => j % 3 == 0).ToList();
        int coarseValid = Math.Max(10, minValid / 3);

        (int dx, int dy, double score, int valid) best = (0, 0, -1, 0);
        void Try(int cx, int cy)
        {
            // A csúcs körül pixelenként (vékony vonalon, pontrácson a szomszédos eltolás már nem egyezik)
            (int dx, int dy, double s, int v) local = (cx, cy, -1, 0);
            for (int ey = -_f; ey <= _f; ey++)
                for (int ex = -_f; ex <= _f; ex++)
                {
                    var (s, v) = Score(a, b, coarse, cx + ex, cy + ey, null);
                    if (v >= coarseValid && s > local.s) local = (cx + ex, cy + ey, s, v);
                }
            if (local.s < 0) return;
            var c0 = local;
            local.s = -1;
            for (int ey = -1; ey <= 1; ey++)
                for (int ex = -1; ex <= 1; ex++)
                {
                    var (s, v) = Score(a, b, pts, c0.dx + ex, c0.dy + ey, null);
                    if (v >= minValid && s > local.s) local = (c0.dx + ex, c0.dy + ey, s, v);
                }
            if (local.s > best.score) best = local;
        }
        foreach (var (cx, cy) in top) Try(cx, cy);
        // Bizonytalan eredmény (pl. sok egyforma csúcs egy pontrácstól): további csúcsok is
        if (best.score < 0.8)
            foreach (var (cx, cy) in ranked.Skip(Peaks).Take(16)) Try(cx, cy);
        if (best.score >= MinScore && (best.dx != 0 || best.dy != 0)) return (best.dx, best.dy, best.score, true);
        // Nincs illeszkedő elmozdulás: ha a kép nagy része a helyén maradt, csak egy része változott (animáció)
        return sameRatio >= 0.7 ? (0, 0, sameRatio, false) : null;
    }
}

/// <summary>
/// Pásztázott (kétirányú) felvétel összerakása, pl. Miro tábla, folyamatábra, térkép.
/// - Az eltolást az előző jó képkockához képest becsli, majd a már összerakott képhez igazítja
///   (így visszafelé pásztázva sem csúszik el, és nem kerül be semmi kétszer).
/// - A mozdulatlan elemeket (eszköztár, nagyító gombok, minitérkép) a mozgásból ismeri fel,
///   és kihagyja: a kész képen csak a tábla van.
/// - Csak a ténylegesen mozgó területet (az ablak "nézetét") írja: ha a kijelölés nagyobb, pl. az egész
///   képernyő, a körülötte álló böngésző, menü, lista nem kerül a képre.
/// - A kép csempékben tárolódik; ahová nem jutott el a felvétel, ott a tábla háttérszíne lesz.
/// - Ugyanarra a helyre a legfrissebb képkocka kerül (a később betöltődő élesebb részlet felülírja).
/// </summary>
internal sealed class PanStitcher : IStitcher
{
    public const long MaxPixels = 48_000_000;
    private const int TS = 256;               // csempeméret
    private const int ThumbDiv = 8;           // előnézet kicsinyítése

    private readonly int _w, _h;
    private readonly ShiftEstimator _est;
    private readonly Dictionary<(int, int), int[]> _tiles = new();
    private readonly Dictionary<(int, int), int[]> _thumbs = new();
    private readonly HashSet<(int, int)> _dirty = new();

    // Mozdulatlan elemek: fél felbontású számlálók (mozdulatlan / tartalommal mozgó)
    private readonly int _mw, _mh;
    private readonly byte[] _still, _moving, _changed;
    private readonly bool[] _colored;         // nem a tábla háttérszíne (a legutóbbi képkockán)
    private long _travel;                     // eddigi összes elmozdulás (px)
    private readonly bool[] _mask;            // kizárt pontok (fél felbontás, 1 ponttal kiszélesítve)

    private Pixmap? _ref;                     // az utolsó jól illesztett képkocka
    private (double[] re, double[] im) _refSpec;
    private Point _refPos;                    // a helye a táblán
    private readonly List<(Pixmap px, Point pos)> _pending = new();   // kalibrálás előtt: még nem írt képkockák
    private int _movingPairs, _lost;
    private Point _lastMove;                  // az előző elmozdulás (a következő becslés első jelöltje)
    private int _minX = int.MaxValue, _minY = int.MaxValue, _maxX = int.MinValue, _maxY = int.MinValue;
    private int _bg = unchecked((int)0xFFFFFFFF);
    private bool _bgFromViewport;
    private Rectangle _viewport;              // a ténylegesen mozgó terület a képkockán (üres: még nem ismert)
    private string _info = "";

    /// <summary>
    /// Írni csak elég elmozdulás után kezdünk: addigra megbízható a mozdulatlan elemek maszkja
    /// (az eszköztárak egyszínű belseje csak hosszabb pásztázás után derül ki).
    /// </summary>
    public bool Calibrated => _movingPairs >= 4 && _travel >= MinTravel && !_viewport.IsEmpty;
    private const int MinTravel = 300;
    public Point Position => _refPos;

    /// <summary>A nézet legnagyobb oldalirányú távolsága a kiindulástól (px): ebből derül ki, hogy pásztázás volt.</summary>
    public int SidewaysReach { get; private set; }

    public PanStitcher(int w, int h)
    {
        _w = w;
        _h = h;
        _est = new ShiftEstimator(w, h);
        _mw = (w + 1) / 2;
        _mh = (h + 1) / 2;
        _still = new byte[_mw * _mh];
        _moving = new byte[_mw * _mh];
        _changed = new byte[_mw * _mh];
        _colored = new bool[_mw * _mh];
        _mask = new bool[_mw * _mh];
    }

    private bool Masked(int x, int y) => _mask[(y >> 1) * _mw + (x >> 1)];

    /// <summary>Kihagyott pont: mozdulatlan elem, vagy a mozgó területen kívül esik.</summary>
    private bool Skip(int x, int y) => Masked(x, y) || (!_viewport.IsEmpty && !_viewport.Contains(x, y));

    // ================= Képkockák =================

    public LongStitcher.Result Add(Bitmap frame)
    {
        var px = Pixmap.From(frame);
        var spec = _est.Spectrum(px);
        if (_ref == null)
        {
            _ref = px;
            _refSpec = spec;
            _refPos = Point.Empty;
            _bg = Background(px);
            _pending.Add((px, Point.Empty));
            _info = "indulás";
            return LongStitcher.Result.Continue;
        }

        var est = _est.Estimate(_ref, _refSpec, px, spec, Calibrated ? Skip : null, _lastMove);
        if (est == null)
        {
            _lost++;
            _info = "nem illeszthető";
            return LongStitcher.Result.Lost;
        }
        var (dx, dy, score, moved) = est.Value;
        _lost = 0;
        if (!moved)
        {
            // Nem mozdult: csak akkor írjuk be (pl. később betöltődött élesebb részlet), ha szinte minden
            // a helyén maradt; egy bizonytalan "nem mozdult" képkocka inkább kimarad
            _info = $"nem mozdult, egyezés {score:P0}";
            if (Calibrated && score >= 0.9) Write(px, _refPos);
            return LongStitcher.Result.Continue;
        }
        var pos = new Point(_refPos.X + dx, _refPos.Y + dy);

        // Igazítás a már összerakott képhez (felhalmozódó hiba ellen, visszafelé pásztázáskor is)
        string snap = "";
        if (Calibrated)
        {
            var fixedPos = Snap(px, pos);
            if (fixedPos != pos) snap = $", igazítva {fixedPos.X - pos.X:+0;-0} {fixedPos.Y - pos.Y:+0;-0}";
            pos = fixedPos;
        }
        _info = $"eltolás {pos.X - _refPos.X} {pos.Y - _refPos.Y}, hely {pos.X} {pos.Y}, egyezés {score:P0}{snap}";
        LearnStatic(_ref, px, pos.X - _refPos.X, pos.Y - _refPos.Y);
        _movingPairs++;

        if (!Calibrated)
        {
            // Addig csak eltesszük, ritkítva: a kihagyott képkocka területét az előtte és utána
            // eltett képkocka együtt lefedi (a végén a legutolsó is bekerül)
            var lastAt = _pending[^1].pos;
            if (Math.Abs(pos.X - lastAt.X) >= _w / 6 || Math.Abs(pos.Y - lastAt.Y) >= _h / 6) _pending.Add((px, pos));
        }
        else
        {
            // A tábla háttérszíne a mozgó területről (nem a körülötte lévő ablakokból)
            if (!_bgFromViewport) { _bg = Background(px, _viewport); _bgFromViewport = true; }
            FlushPending();
            Write(px, pos);
        }

        // Új viszonyítási kép csak valódi elmozdulás után (különben egy téves "nem mozdult"
        // becslés észrevétlenül elcsúsztatná a helyet)
        _ref = px;
        _refSpec = spec;
        _lastMove = new Point(pos.X - _refPos.X, pos.Y - _refPos.Y);
        _refPos = pos;
        SidewaysReach = Math.Max(SidewaysReach, Math.Abs(pos.X));
        return (long)(_maxX - _minX) * (_maxY - _minY) >= MaxPixels ? LongStitcher.Result.Max : LongStitcher.Result.Continue;
    }

    /// <summary>A becsült helyet ±2 px-en belül a már tárolt képhez igazítja, ha ott elég átfedés van.</summary>
    private Point Snap(Pixmap px, Point pos)
    {
        var pts = _est.Points(px, Skip);
        if (pts.Count < 40) return pos;
        Point best = pos;
        double bestS = -1;
        int[]? tile = null;
        (int, int) tileKey = (int.MinValue, 0);
        for (int ey = -2; ey <= 2; ey++)
            for (int ex = -2; ex <= 2; ex++)
            {
                int ok = 0, valid = 0;
                foreach (int i in pts)
                {
                    int gx = pos.X + ex + i % _w, gy = pos.Y + ey + i / _w;
                    var key = (FloorDiv(gx, TS), FloorDiv(gy, TS));
                    if (key != tileKey) { tileKey = key; _tiles.TryGetValue(key, out tile); }
                    if (tile == null) continue;
                    int v = tile[(gy - key.Item2 * TS) * TS + (gx - key.Item1 * TS)];
                    if (v == 0) continue;
                    valid++;
                    if (Pixmap.Near(px.Px[i], v)) ok++;
                }
                if (valid < Math.Max(60, pts.Count / 5)) continue;
                double s = ok / (double)valid;
                if (s > bestS || (s == bestS && ex == 0 && ey == 0)) { bestS = s; best = new Point(pos.X + ex, pos.Y + ey); }
            }
        return bestS >= ShiftEstimator.MinScore ? best : pos;
    }

    /// <summary>
    /// Mozdulatlan elemek tanulása egy elmozdult képkockapárból: ami a képernyőn ugyanott maradt, de a
    /// tartalommal együtt mozgatva nem egyezik, az eszköztár-szerű (kihagyjuk); a fordítottja tartalom.
    /// Egyszínű felület mindkettőnek megfelel: az tartalomnak számít (beírható, nem árt).
    /// A tábla háttérszínű pontja nem lehet "mozdulatlan" bizonyíték: ha az eltolt hely épp egy
    /// eszköztárra esik, a háttér tévesen mozdulatlannak látszana (az eszköztár "szellemképe").
    /// </summary>
    private void LearnStatic(Pixmap a, Pixmap b, int dx, int dy)
    {
        for (int my = 0; my < _mh; my++)
        {
            int y = my * 2, ay = y + dy;
            for (int mx = 0; mx < _mw; mx++)
            {
                int x = mx * 2, i = y * _w + x, ax = x + dx;
                if (ax < 0 || ay < 0 || ax >= _w || ay >= _h) continue;   // nincs mihez hasonlítani
                int v = b.Px[i];
                bool same = Pixmap.Near(v, a.Px[i], 12);
                bool shifted = Pixmap.Near(v, a.Px[ay * _w + ax], 12);
                int k = my * _mw + mx;
                _colored[k] = !Pixmap.Near(v, _bg, 12);
                if (!same && _changed[k] < 255) _changed[k]++;
                if (same && !shifted) { if (_still[k] < 255 && !Pixmap.Near(v, _bg, 12)) _still[k]++; }
                else if (shifted && !same) { if (_moving[k] < 255) _moving[k]++; }
            }
        }
        // Maszk: legalább kétszer mozdulatlan volt és többször, mint mozgó
        _travel += Math.Abs(dx) + Math.Abs(dy);
        var raw = new bool[_mask.Length];
        for (int k = 0; k < raw.Length; k++) raw[k] = _still[k] >= 2 && _still[k] > _moving[k];
        FillBoxes(raw);
        UpdateViewport();
        // Egyszínű eszköztár-részlet, amelyről eltolva sem derül ki semmi: hosszú pásztázás alatt
        // egyszer sem változott a képernyőn, és nem háttérszínű → mozdulatlan
        if (_travel >= 300)
            for (int k = 0; k < raw.Length; k++)
                if (_changed[k] == 0 && _colored[k] && _moving[k] == 0) raw[k] = true;
        // 1 ponttal kiszélesítve (élsimítás a széleken)
        for (int my = 0; my < _mh; my++)
            for (int mx = 0; mx < _mw; mx++)
            {
                bool m = false;
                for (int yy = Math.Max(0, my - 1); yy <= Math.Min(_mh - 1, my + 1) && !m; yy++)
                    for (int xx = Math.Max(0, mx - 1); xx <= Math.Min(_mw - 1, mx + 1); xx++)
                        if (raw[yy * _mw + xx]) { m = true; break; }
                _mask[my * _mw + mx] = m;
            }
    }

    /// <summary>
    /// A mozgó terület: azok a sorok és oszlopok, amelyekben a tartalom bizonyítottan együtt mozgott
    /// a pásztázással. Csak bővül (a ritka ábrán a széle később derülhet ki).
    /// </summary>
    private void UpdateViewport()
    {
        var col = new int[_mw];
        var row = new int[_mh];
        for (int k = 0; k < _moving.Length; k++)
            if (_moving[k] >= 2 && _moving[k] > _still[k]) { col[k % _mw]++; row[k / _mw]++; }
        int minCol = Math.Max(2, _mh / 60), minRow = Math.Max(2, _mw / 60);
        int x0 = Array.FindIndex(col, c => c >= minCol), x1 = Array.FindLastIndex(col, c => c >= minCol);
        int y0 = Array.FindIndex(row, c => c >= minRow), y1 = Array.FindLastIndex(row, c => c >= minRow);
        if (x0 < 0 || y0 < 0 || x1 - x0 < 8 || y1 - y0 < 8) return;
        var r = Rectangle.FromLTRB(Math.Max(0, x0 * 2 - 4), Math.Max(0, y0 * 2 - 4), Math.Min(_w, x1 * 2 + 6), Math.Min(_h, y1 * 2 + 6));
        _viewport = _viewport.IsEmpty ? r : Rectangle.Union(_viewport, r);
    }

    /// <summary>
    /// Az eszköztárak egyszínű belseje nem ad bizonyítékot (eltolva is önmagával egyezik), ezért
    /// a felismert mozdulatlan foltok befoglaló téglalapját kitöltjük, kivéve ahol a tartalom
    /// bizonyítottan mozgott. A túl nagy foltot nem töltjük ki (az inkább téves).
    /// </summary>
    private void FillBoxes(bool[] raw)
    {
        var seen = new bool[raw.Length];
        var stack = new Stack<int>();
        var boxes = new List<Rectangle>();
        for (int k0 = 0; k0 < raw.Length; k0++)
        {
            if (!raw[k0] || seen[k0]) continue;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, cnt = 0;
            stack.Push(k0);
            seen[k0] = true;
            while (stack.Count > 0)
            {
                int k = stack.Pop(), x = k % _mw, y = k / _mw;
                cnt++;
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                for (int yy = Math.Max(0, y - 1); yy <= Math.Min(_mh - 1, y + 1); yy++)
                    for (int xx = Math.Max(0, x - 1); xx <= Math.Min(_mw - 1, x + 1); xx++)
                    {
                        int n = yy * _mw + xx;
                        if (raw[n] && !seen[n]) { seen[n] = true; stack.Push(n); }
                    }
            }
            if (cnt < 4) continue;
            var box = Rectangle.FromLTRB(Math.Max(0, x0 - 1), Math.Max(0, y0 - 1), Math.Min(_mw, x1 + 2), Math.Min(_mh, y1 + 2));
            if ((long)box.Width * box.Height > (long)_mw * _mh * 3 / 10) continue;
            boxes.Add(box);
        }
        foreach (var b in boxes)
            for (int y = b.Top; y < b.Bottom; y++)
                for (int x = b.Left; x < b.Right; x++)
                {
                    int k = y * _mw + x;
                    if (!(_moving[k] >= 2 && _moving[k] > _still[k])) raw[k] = true;
                }
    }

    /// <summary>A képkocka beírása a táblára (a mozdulatlan elemek nélkül); a legfrissebb nyer.</summary>
    private void Write(Pixmap p, Point at)
    {
        var vp = _viewport.IsEmpty ? new Rectangle(0, 0, _w, _h) : _viewport;
        for (int y = vp.Top; y < vp.Bottom; y++)
        {
            int gy = at.Y + y, ty = FloorDiv(gy, TS), ry = gy - ty * TS;
            int mrow = (y >> 1) * _mw;
            int x = vp.Left;
            while (x < vp.Right)
            {
                int gx = at.X + x, tx = FloorDiv(gx, TS), rx = gx - tx * TS;
                int run = Math.Min(TS - rx, vp.Right - x);
                var key = (tx, ty);
                if (!_tiles.TryGetValue(key, out var tile)) _tiles[key] = tile = new int[TS * TS];
                int src = y * _w + x, dst = ry * TS + rx;
                for (int k = 0; k < run; k++)
                {
                    if (_mask[mrow + ((x + k) >> 1)]) continue;
                    int v = p.Px[src + k] | unchecked((int)0xFF000000), old = tile[dst + k];
                    // A tartalom erősebb a háttérnél: egy háttérszínű (pl. álló, fehér sáv) pont nem írja
                    // felül a már rögzített tartalmat; tartalmat tartalom (pl. élesebb betöltés) igen
                    if (old != 0 && v == _bg && old != _bg) continue;
                    tile[dst + k] = v;
                }
                _dirty.Add(key);
                x += run;
            }
        }
        // A befoglaló téglalap (a kizárt sávok miatt kicsit bővebb lehet, a háttérszín kitölti)
        _minX = Math.Min(_minX, at.X + vp.Left);
        _minY = Math.Min(_minY, at.Y + vp.Top);
        _maxX = Math.Max(_maxX, at.X + vp.Right);
        _maxY = Math.Max(_maxY, at.Y + vp.Bottom);
    }

    /// <summary>A tábla háttérszíne: a képkocka (vagy a megadott terület) leggyakoribb színe.</summary>
    private static int Background(Pixmap p, Rectangle? area = null)
    {
        var r = area ?? new Rectangle(0, 0, p.W, p.H);
        var counts = new Dictionary<int, int>();
        for (int y = r.Top; y < r.Bottom; y += 3)
            for (int x = r.Left; x < r.Right; x += 3)
            {
                int v = p.Px[y * p.W + x] | unchecked((int)0xFF000000);
                counts[v] = counts.GetValueOrDefault(v) + 1;
            }
        return counts.Count == 0 ? unchecked((int)0xFFFFFFFF) : counts.MaxBy(kv => kv.Value).Key;
    }

    // ================= A kész kép =================

    private Rectangle Bounds => _maxX < _minX ? new Rectangle(0, 0, _w, _h) : Rectangle.FromLTRB(_minX, _minY, _maxX, _maxY);

    private void FlushPending()
    {
        foreach (var (p, at) in _pending) Write(p, at);
        _pending.Clear();
    }

    public Bitmap Compose()
    {
        if (_movingPairs == 0)
        {
            // Nem mozdult: a legutóbbi képkocka, ahogy van
            var last = _pending.Count > 0 ? _pending[^1].px : _ref;
            return last == null ? new Bitmap(1, 1) : ToBitmap(last.Px, last.W, last.H);
        }
        // Rövid pásztázás: az eltett képkockák és a legutolsó, az eddig tanult maszkkal
        if (_pending.Count > 0 && _ref != null) _pending.Add((_ref, _refPos));
        FlushPending();
        var b = Bounds;
        var px = new int[b.Width * b.Height];
        for (int y = 0; y < b.Height; y++)
        {
            int gy = b.Y + y, ty = FloorDiv(gy, TS), ry = gy - ty * TS;
            for (int x = 0; x < b.Width; x++)
            {
                int gx = b.X + x, tx = FloorDiv(gx, TS);
                int v = _tiles.TryGetValue((tx, ty), out var tile) ? tile[ry * TS + gx - tx * TS] : 0;
                px[y * b.Width + x] = v != 0 ? v : _bg;
            }
        }
        return ToBitmap(px, b.Width, b.Height);
    }

    private static Bitmap ToBitmap(int[] px, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++) Marshal.Copy(px, y * w, data.Scan0 + y * data.Stride, w);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    // ================= Előnézet, napló =================

    public Size PreviewSize => Bounds.Size;

    public string SizeLabel
    {
        get
        {
            var b = Bounds;
            var hu = CultureInfo.GetCultureInfo("hu-HU");
            return $"{b.Width.ToString("#,0", hu)} × {b.Height.ToString("#,0", hu)} px";
        }
    }

    public string FrameInfo => _info;

    public string Summary
    {
        get
        {
            int masked = _mask.Count(m => m);
            return $"pásztázás, tábla {Bounds}, mozgó terület a képkockán {_viewport}, csempék {_tiles.Count}, kizárt (mozdulatlan) terület {masked * 100 / Math.Max(1, _mask.Length)}%";
        }
    }

    public void DrawPreview(Graphics g, RectangleF dest)
    {
        if (!Calibrated || _tiles.Count == 0) return;
        var b = Bounds;
        int tw = Math.Max(1, b.Width / ThumbDiv), th = Math.Max(1, b.Height / ThumbDiv);
        var px = new int[tw * th];
        Array.Fill(px, _bg);
        foreach (var (key, tile) in _tiles)
        {
            var t = Thumb(key, tile);
            int ox = (key.Item1 * TS - b.X) / ThumbDiv, oy = (key.Item2 * TS - b.Y) / ThumbDiv, n = TS / ThumbDiv;
            for (int y = 0; y < n; y++)
            {
                int yy = oy + y;
                if (yy < 0 || yy >= th) continue;
                for (int x = 0; x < n; x++)
                {
                    int xx = ox + x;
                    if (xx < 0 || xx >= tw || t[y * n + x] == 0) continue;
                    px[yy * tw + xx] = t[y * n + x];
                }
            }
        }
        using var bmp = ToBitmap(px, tw, th);
        g.DrawImage(bmp, dest);
    }

    private int[] Thumb((int, int) key, int[] tile)
    {
        if (_thumbs.TryGetValue(key, out var t) && !_dirty.Contains(key)) return t;
        int n = TS / ThumbDiv;
        t = new int[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                t[y * n + x] = tile[(y * ThumbDiv + ThumbDiv / 2) * TS + x * ThumbDiv + ThumbDiv / 2];
        _thumbs[key] = t;
        _dirty.Remove(key);
        return t;
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    public void Dispose()
    {
        _tiles.Clear();
        _thumbs.Clear();
        _pending.Clear();
    }
}

/// <summary>Egyszerű, helyben számoló radix-2 FFT (2D: sorok, majd oszlopok).</summary>
internal static class Fft
{
    public static void Transform2D(double[] re, double[] im, int n, int m, bool inverse)
    {
        var rr = new double[n];
        var ri = new double[n];
        for (int y = 0; y < m; y++)
        {
            Array.Copy(re, y * n, rr, 0, n);
            Array.Copy(im, y * n, ri, 0, n);
            Transform(rr, ri, inverse);
            Array.Copy(rr, 0, re, y * n, n);
            Array.Copy(ri, 0, im, y * n, n);
        }
        var cr = new double[m];
        var ci = new double[m];
        for (int x = 0; x < n; x++)
        {
            for (int y = 0; y < m; y++) { cr[y] = re[y * n + x]; ci[y] = im[y * n + x]; }
            Transform(cr, ci, inverse);
            for (int y = 0; y < m; y++) { re[y * n + x] = cr[y]; im[y * n + x] = ci[y]; }
        }
    }

    public static void Transform(double[] re, double[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi;
                    re[a] += xr; im[a] += xi;
                    double t = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = t;
                }
            }
        }
    }
}
