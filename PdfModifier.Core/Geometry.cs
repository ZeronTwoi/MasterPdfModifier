namespace PdfModifier.Core;

/// <summary>Axis-aligned rectangle. In "visual" space the origin is the top-left of the page as displayed, in points, y down.</summary>
public readonly record struct RectD(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public double CenterX => (Left + Right) / 2;
    public double CenterY => (Top + Bottom) / 2;

    public static RectD FromPoints(params (double X, double Y)[] points)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        foreach (var (x, y) in points)
        {
            l = Math.Min(l, x);
            r = Math.Max(r, x);
            t = Math.Min(t, y);
            b = Math.Max(b, y);
        }

        return new RectD(l, t, r, b);
    }

    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;

    public RectD Union(RectD o) =>
        new(Math.Min(Left, o.Left), Math.Min(Top, o.Top), Math.Max(Right, o.Right), Math.Max(Bottom, o.Bottom));
}

/// <summary>PDF-style affine matrix: x' = A·x + C·y + E, y' = B·x + D·y + F.</summary>
public readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

    public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

    /// <summary>Returns the matrix that applies this transform first and <paramref name="next"/> second.</summary>
    public Affine Then(Affine next) => new(
        A * next.A + B * next.C,
        A * next.B + B * next.D,
        C * next.A + D * next.C,
        C * next.B + D * next.D,
        E * next.A + F * next.C + next.E,
        E * next.B + F * next.D + next.F);

    public Affine Invert()
    {
        var det = A * D - B * C;
        if (Math.Abs(det) < 1e-12) throw new InvalidOperationException("Matrix is not invertible.");
        return new Affine(
            D / det, -B / det,
            -C / det, A / det,
            (C * F - D * E) / det,
            (B * E - A * F) / det);
    }

    /// <summary>Uniform scale factor implied by the matrix.</summary>
    public double Scale => Math.Sqrt(Math.Abs(A * D - B * C));

    public RectD MapBounds(double left, double bottom, double right, double top) => RectD.FromPoints(
        Apply(left, bottom), Apply(right, bottom), Apply(left, top), Apply(right, top));
}
