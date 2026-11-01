using UpsHub.Core.Model;
using UpsHub.Core.Settings;

namespace UpsHub.Core.Shutdown;

public enum ShutdownPhase { Idle, CountingDown, Executing, Suppressed }

public sealed record ShutdownState(
    ShutdownPhase Phase,
    string? Reason = null,
    DateTimeOffset? Deadline = null,
    bool ExtensionUsed = false,
    string? LastCancelReason = null)
{
    public TimeSpan Remaining(DateTimeOffset now) =>
        Deadline is { } d && d > now ? d - now : TimeSpan.Zero;
}

/// <summary>
/// Decides when to shut the PC down, from snapshots of the UPS that powers it. Pure logic: the caller
/// feeds snapshots and the clock, and performs the action when <see cref="Tick"/> returns true.
/// </summary>
public sealed class ShutdownCoordinator
{
    private DateTimeOffset? _onBatterySince;
    private bool _lastKnownOnBattery;

    public ShutdownRules Rules { get; set; }
    public ShutdownState State { get; private set; } = new(ShutdownPhase.Idle);
    public event Action<ShutdownState, ShutdownState>? StateChanged;

    public ShutdownCoordinator(ShutdownRules rules) => Rules = rules;

    private void Set(ShutdownState s)
    {
        var old = State;
        if (old == s) return;
        State = s;
        StateChanged?.Invoke(old, s);
    }

    /// <summary>Returns why a shutdown is warranted right now, or null.</summary>
    public string? Trigger(UpsSnapshot? snap, bool connected, DateTimeOffset now)
    {
        if (!connected || snap is null || snap.Stale)
            return Rules.ActIfLostWhileOnBattery && _lastKnownOnBattery
                ? "Lost contact with the UPS while it was on battery"
                : null;

        var s = snap.Status;
        if (Rules.FollowFsd && s.HasFlag(UpsStatus.FSD)) return "The NUT server ordered a forced shutdown (FSD)";
        if (!s.HasFlag(UpsStatus.OB)) return null;

        if (Rules.OnLowBatteryFlag && s.HasFlag(UpsStatus.LB)) return "The UPS reports low battery";
        if (Rules.BatteryPercentBelow > 0 && snap.BatteryCharge is { } c && c < Rules.BatteryPercentBelow)
            return $"Battery at {c:0}% (rule: below {Rules.BatteryPercentBelow}%)";
        if (Rules.RuntimeSecondsBelow > 0 && snap.BatteryRuntimeSeconds is { } r && r < Rules.RuntimeSecondsBelow)
            return $"Runtime {Fmt(r)} left (rule: below {Fmt(Rules.RuntimeSecondsBelow)})";
        if (Rules.OnBatteryForSeconds > 0 && _onBatterySince is { } since && (now - since).TotalSeconds >= Rules.OnBatteryForSeconds)
            return $"On battery for {Fmt((now - since).TotalSeconds)} (rule: {Fmt(Rules.OnBatteryForSeconds)})";
        return null;
    }

    public ShutdownState Evaluate(UpsSnapshot? snap, bool connected, DateTimeOffset now)
    {
        if (connected && snap is { Stale: false })
        {
            var ob = snap.Status.HasFlag(UpsStatus.OB);
            if (ob && _onBatterySince is null) _onBatterySince = now;
            if (!ob) _onBatterySince = null;
            _lastKnownOnBattery = ob;
        }

        if (!Rules.Enabled)
        {
            if (State.Phase is ShutdownPhase.CountingDown or ShutdownPhase.Suppressed)
                Set(new ShutdownState(ShutdownPhase.Idle, LastCancelReason: "Shutdown rules turned off"));
            return State;
        }
        if (State.Phase == ShutdownPhase.Executing) return State;

        var powerBack = connected && snap is { Stale: false } && snap.Status.OnLine() && !snap.Status.HasFlag(UpsStatus.FSD);
        if (powerBack && State.Phase is ShutdownPhase.CountingDown or ShutdownPhase.Suppressed)
        {
            Set(new ShutdownState(ShutdownPhase.Idle, LastCancelReason: "Power restored"));
            return State;
        }

        var reason = Trigger(snap, connected, now);
        var fsd = reason is not null && snap?.Status.HasFlag(UpsStatus.FSD) == true;
        if (reason is null) return State;
        if (State.Phase == ShutdownPhase.Idle || (State.Phase == ShutdownPhase.Suppressed && fsd))
        {
            Set(new ShutdownState(ShutdownPhase.CountingDown, reason, now + TimeSpan.FromSeconds(Rules.CountdownSeconds)));
        }
        return State;
    }

    /// <summary>Starts a countdown without a power event (used by the shutdown test).</summary>
    public void Begin(string reason, DateTimeOffset deadline) =>
        Set(new ShutdownState(ShutdownPhase.CountingDown, reason, deadline));

    /// <summary>Advance the clock. Returns true exactly once, when the action must run now.</summary>
    public bool Tick(DateTimeOffset now)
    {
        if (State.Phase == ShutdownPhase.CountingDown && State.Deadline is { } d && now >= d)
        {
            Set(State with { Phase = ShutdownPhase.Executing });
            return true;
        }
        return false;
    }

    /// <summary>User pressed "now": skip the rest of the countdown. Returns true if the action must run.</summary>
    public bool ExecuteNow()
    {
        if (State.Phase != ShutdownPhase.CountingDown) return false;
        Set(State with { Phase = ShutdownPhase.Executing });
        return true;
    }

    /// <summary>User cancelled: don't re-trigger until mains power is back (or FSD arrives).</summary>
    public void Cancel(string why = "Cancelled by user")
    {
        if (State.Phase == ShutdownPhase.CountingDown)
            Set(new ShutdownState(ShutdownPhase.Suppressed, State.Reason, LastCancelReason: why));
    }

    public bool CanExtend => State.Phase == ShutdownPhase.CountingDown && Rules.AllowExtension && !State.ExtensionUsed && Rules.ExtensionSeconds > 0;

    /// <summary>One-time grace extension.</summary>
    public bool Extend()
    {
        if (!CanExtend) return false;
        Set(State with { Deadline = State.Deadline!.Value + TimeSpan.FromSeconds(Rules.ExtensionSeconds), ExtensionUsed = true });
        return true;
    }

    /// <summary>After the action ran (or failed), go back to idle so a later outage is handled again.</summary>
    public void Reset(string? why = null) => Set(new ShutdownState(ShutdownPhase.Idle, LastCancelReason: why));

    public static string Fmt(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" :
               t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:00}s" : $"{t.Seconds}s";
    }
}
