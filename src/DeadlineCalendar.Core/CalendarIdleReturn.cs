namespace DeadlineCalendar.Core;

/// <summary>Tracks inactivity after scrolling using one caller-owned monotonic elapsed clock.</summary>
public sealed class CalendarIdleReturn
{
    private readonly TimeSpan _idleDelay;
    private TimeSpan? _lastActivity;

    public CalendarIdleReturn(TimeSpan idleDelay)
    {
        if (idleDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleDelay), "The idle delay must be positive.");
        _idleDelay = idleDelay;
    }

    public bool IsArmed { get; private set; }

    public void RecordScroll(TimeSpan now)
    {
        RecordActivity(now);
        IsArmed = true;
    }

    public void RecordActivity(TimeSpan now)
    {
        // A regressing timestamp must not move the idle deadline earlier.
        if (_lastActivity is null || now > _lastActivity.Value)
            _lastActivity = now;
    }

    public void Cancel() => IsArmed = false;

    public bool TryBeginReturn(TimeSpan now, bool viewingOrInteracting)
    {
        if (viewingOrInteracting)
        {
            RecordActivity(now);
            return false;
        }

        if (!IsArmed || _lastActivity is not { } lastActivity || now < lastActivity)
            return false;

        // Check the target before adding ticks, so very large timestamps cannot overflow.
        if (lastActivity.Ticks > TimeSpan.MaxValue.Ticks - _idleDelay.Ticks ||
            now.Ticks < lastActivity.Ticks + _idleDelay.Ticks)
            return false;

        IsArmed = false;
        return true;
    }
}
