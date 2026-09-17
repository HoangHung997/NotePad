namespace NeraSpreadSheet.Scrolling;

public enum ScrollInputKind
{
    Precision,
    Wheel,
    Touch,
    Programmatic,
}

public readonly record struct ScrollDelta(double DeltaX, double DeltaY, ScrollInputKind Kind);

public readonly record struct ScrollBounds
{
    public ScrollBounds(double maximumX, double maximumY)
    {
        if (!double.IsFinite(maximumX) || maximumX < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumX));
        }

        if (!double.IsFinite(maximumY) || maximumY < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumY));
        }

        MaximumX = maximumX;
        MaximumY = maximumY;
    }

    public double MaximumX { get; }

    public double MaximumY { get; }
}

public readonly record struct ScrollSnapshot(
    double OffsetX,
    double OffsetY,
    double TargetX,
    double TargetY);

public readonly record struct ScrollFrameResult(ScrollSnapshot Snapshot, bool Changed);

public sealed record ScrollPhysicsOptions
{
    public double WheelResponsePerSecond { get; init; } = 22d;

    public double SnapEpsilon { get; init; } = 0.01d;

    public double MaximumFrameSeconds { get; init; } = 0.05d;
}
