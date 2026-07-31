namespace AutoDim.Core.Geometry;

/// <summary>二维点（值类型）。</summary>
public readonly struct Point2D : IEquatable<Point2D>
{
    public double X { get; }
    public double Y { get; }

    public Point2D(double x, double y)
    {
        X = x;
        Y = y;
    }

    /// <summary>按网格公差吸附（round 到公差整数倍）。</summary>
    public static Point2D Snap(Point2D p, double tol) =>
        new(Math.Round(p.X / tol) * tol, Math.Round(p.Y / tol) * tol);

    public double DistanceTo(Point2D other)
    {
        double dx = other.X - X, dy = other.Y - Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public bool Equals(Point2D other) => X.Equals(other.X) && Y.Equals(other.Y);
    public override bool Equals(object? obj) => obj is Point2D p && Equals(p);
    public override int GetHashCode() => HashCode.Combine(X, Y);
    public override string ToString() => $"({X:0.##},{Y:0.##})";

    public static bool operator ==(Point2D a, Point2D b) => a.Equals(b);
    public static bool operator !=(Point2D a, Point2D b) => !a.Equals(b);
}
