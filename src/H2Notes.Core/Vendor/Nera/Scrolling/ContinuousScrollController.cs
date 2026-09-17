namespace NeraSpreadSheet.Scrolling;

public sealed class ContinuousScrollController
{
    private readonly ScrollPhysicsOptions _options;
    private double _currentX;
    private double _currentY;
    private double _targetX;
    private double _targetY;
    private double _pendingDirectX;
    private double _pendingDirectY;

    public ContinuousScrollController(ScrollPhysicsOptions? options = null)
    {
        _options = options ?? new ScrollPhysicsOptions();
        ValidateOptions(_options);
    }

    public ScrollSnapshot Snapshot => new(_currentX, _currentY, _targetX, _targetY);

    public bool HasPendingMotion =>
        Math.Abs(_pendingDirectX) > _options.SnapEpsilon ||
        Math.Abs(_pendingDirectY) > _options.SnapEpsilon ||
        Math.Abs(_targetX - _currentX) > _options.SnapEpsilon ||
        Math.Abs(_targetY - _currentY) > _options.SnapEpsilon;

    public void QueueDelta(ScrollDelta delta)
    {
        ValidateFinite(delta.DeltaX, nameof(delta));
        ValidateFinite(delta.DeltaY, nameof(delta));

        switch (delta.Kind)
        {
            case ScrollInputKind.Precision:
            case ScrollInputKind.Touch:
                _pendingDirectX += delta.DeltaX;
                _pendingDirectY += delta.DeltaY;
                break;

            case ScrollInputKind.Wheel:
            case ScrollInputKind.Programmatic:
                _targetX += delta.DeltaX;
                _targetY += delta.DeltaY;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(delta));
        }
    }

    public void ScrollTo(double offsetX, double offsetY, bool animated)
    {
        ValidateFinite(offsetX, nameof(offsetX));
        ValidateFinite(offsetY, nameof(offsetY));

        if (offsetX < 0d || offsetY < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(offsetX), "Scroll offsets cannot be negative.");
        }

        _pendingDirectX = 0d;
        _pendingDirectY = 0d;
        _targetX = offsetX;
        _targetY = offsetY;

        if (!animated)
        {
            _currentX = offsetX;
            _currentY = offsetY;
        }
    }

    public ScrollFrameResult AdvanceFrame(TimeSpan elapsed, ScrollBounds bounds)
    {
        var before = Snapshot;

        if (_pendingDirectX != 0d || _pendingDirectY != 0d)
        {
            _currentX += _pendingDirectX;
            _currentY += _pendingDirectY;
            _targetX += _pendingDirectX;
            _targetY += _pendingDirectY;
            _pendingDirectX = 0d;
            _pendingDirectY = 0d;
        }

        ClampToBounds(bounds);

        var seconds = Math.Clamp(elapsed.TotalSeconds, 0d, _options.MaximumFrameSeconds);
        if (seconds > 0d)
        {
            var blend = 1d - Math.Exp(-_options.WheelResponsePerSecond * seconds);
            _currentX += (_targetX - _currentX) * blend;
            _currentY += (_targetY - _currentY) * blend;
        }

        SnapNearTarget();
        ClampToBounds(bounds);

        var after = Snapshot;
        return new ScrollFrameResult(after, before != after);
    }

    public void Reset()
    {
        _currentX = 0d;
        _currentY = 0d;
        _targetX = 0d;
        _targetY = 0d;
        _pendingDirectX = 0d;
        _pendingDirectY = 0d;
    }

    private void SnapNearTarget()
    {
        if (Math.Abs(_targetX - _currentX) <= _options.SnapEpsilon)
        {
            _currentX = _targetX;
        }

        if (Math.Abs(_targetY - _currentY) <= _options.SnapEpsilon)
        {
            _currentY = _targetY;
        }
    }

    private void ClampToBounds(ScrollBounds bounds)
    {
        _currentX = Math.Clamp(_currentX, 0d, bounds.MaximumX);
        _currentY = Math.Clamp(_currentY, 0d, bounds.MaximumY);
        _targetX = Math.Clamp(_targetX, 0d, bounds.MaximumX);
        _targetY = Math.Clamp(_targetY, 0d, bounds.MaximumY);
    }

    private static void ValidateFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must be finite.");
        }
    }

    private static void ValidateOptions(ScrollPhysicsOptions options)
    {
        if (!double.IsFinite(options.WheelResponsePerSecond) || options.WheelResponsePerSecond <= 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.WheelResponsePerSecond,
                "WheelResponsePerSecond must be finite and greater than zero.");
        }

        if (!double.IsFinite(options.SnapEpsilon) || options.SnapEpsilon < 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.SnapEpsilon,
                "SnapEpsilon must be finite and non-negative.");
        }

        if (!double.IsFinite(options.MaximumFrameSeconds) || options.MaximumFrameSeconds <= 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaximumFrameSeconds,
                "MaximumFrameSeconds must be finite and greater than zero.");
        }
    }
}
