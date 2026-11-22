namespace Screen;

/// <summary>Eszközök. A Crop az alapértelmezett: csak ilyenkor méretezhető a vágás.</summary>
internal enum Tool { Crop, Blur, Oval, Rect, Line, Pen, Text, Hand }

/// <summary>Egy lerakott jelölés a fagyasztott képen (képernyő-koordinátákban).</summary>
internal sealed class Annotation
{
    public Tool Kind;
    public Color Color;
    public Point A, B;                       // kezdő- és végpont (alakzat, vonal, elmosás)
    public List<Point> Points = new();  // ceruza pontjai
    public bool ArrowStart, ArrowEnd;
    public bool Inverse;             // elmosás: inverz = minden el van mosva, csak a téglalap éles (fókusz)

    // Szöveg
    public string Text = "";
    public int FontSize = 20;        // logikai pixel, a kijelző DPI-jével szorozva rajzoljuk
    public bool ManualSize;          // fogópontos méretezés után már nem méreteződik automatikusan
    public Color Background = Color.Empty;   // szövegdoboz háttérszíne (Empty = nincs háttér)
    public int BackgroundOpacity = 80;       // háttér fedése százalékban

    public Rectangle Box
    {
        get
        {
            if (Kind != Tool.Pen)
                return Rectangle.FromLTRB(Math.Min(A.X, B.X), Math.Min(A.Y, B.Y), Math.Max(A.X, B.X), Math.Max(A.Y, B.Y));
            if (Points.Count == 0) return Rectangle.Empty;
            return Rectangle.FromLTRB(Points.Min(p => p.X), Points.Min(p => p.Y), Points.Max(p => p.X), Points.Max(p => p.Y));
        }
    }

    /// <summary>Véletlen kattintásból ne legyen jelölés.</summary>
    public bool IsTiny => Kind == Tool.Pen
        ? Points.Count < 2
        : Math.Abs(A.X - B.X) < 3 && Math.Abs(A.Y - B.Y) < 3;

    /// <summary>Mély másolat a visszavonáshoz.</summary>
    public Annotation Clone()
    {
        var c = (Annotation)MemberwiseClone();
        c.Points = new List<Point>(Points);   // saját lista, ne az eredetivel közös
        return c;
    }

    /// <summary>Ugyanúgy néz-e ki (a visszavonáshoz: változott-e valami).</summary>
    public bool SameAs(Annotation o) =>
        Kind == o.Kind && Color.ToArgb() == o.Color.ToArgb() && A == o.A && B == o.B &&
        ArrowStart == o.ArrowStart && ArrowEnd == o.ArrowEnd && Inverse == o.Inverse &&
        Text == o.Text && FontSize == o.FontSize && ManualSize == o.ManualSize &&
        Background.ToArgb() == o.Background.ToArgb() && BackgroundOpacity == o.BackgroundOpacity &&
        Points.SequenceEqual(o.Points);

    public void Offset(int dx, int dy)
    {
        A.Offset(dx, dy);
        B.Offset(dx, dy);
        for (int i = 0; i < Points.Count; i++) Points[i] = new Point(Points[i].X + dx, Points[i].Y + dy);
    }

    public bool HitTest(Point p, float tol)
    {
        switch (Kind)
        {
            case Tool.Line:
                return DistToSegment(p, A, B) <= tol;
            case Tool.Pen:
                for (int i = 1; i < Points.Count; i++)
                    if (DistToSegment(p, Points[i - 1], Points[i]) <= tol) return true;
                return false;
            default:
                var r = Box;
                r.Inflate((int)tol, (int)tol);
                return r.Contains(p);
        }
    }

    private static double DistToSegment(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
        double t = l2 == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2, 0, 1);
        double x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
        return Math.Sqrt(x * x + y * y);
    }
}
