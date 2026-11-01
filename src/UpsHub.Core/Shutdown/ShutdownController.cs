using UpsHub.Core.Model;
using UpsHub.Core.Platform;
using UpsHub.Core.Settings;

namespace UpsHub.Core.Shutdown;

/// <summary>
/// Owns the one place where the power action is performed. Everything that can end a countdown (the
/// deadline, "Shut down now", a test) goes through <see cref="TickAsync"/> / <see cref="NowAsync"/>,
/// and <see cref="Cancel"/> reliably stops it as long as the action has not started.
///
/// A <b>test</b> is a real countdown that ends with the real configured action (the user asked for this);
/// it just doesn't need a power event to start, and it ignores live UPS readings while it runs so that
/// "power is fine" doesn't cancel it. In demo mode the app passes <see cref="DryRunPowerActions"/>, so
/// nothing is ever performed there.
/// </summary>
public sealed class ShutdownController
{
    /// <summary>Tests always leave at least this long to cancel, even if the warning time is 0.</summary>
    public const int MinTestCountdownSeconds = 10;

    private readonly IPowerActions _power;
    private readonly Func<ShutdownRules> _rules;
    private int _running;

    public ShutdownCoordinator Coordinator { get; }
    public bool IsTest { get; private set; }
    public bool IsRunningAction => Volatile.Read(ref _running) != 0;
    /// <summary>True when the platform can really shut the PC down (false for the demo / dry-run stand-in).</summary>
    public bool PerformsRealActions => _power.IsSupported;
    public string? LastResult { get; private set; }

    /// <summary>Raised after the action ran. Args: description, whether it was simulated.</summary>
    public event Action<string, bool>? ActionCompleted;
    public event Action<Exception>? ActionFailed;

    public ShutdownController(ShutdownCoordinator coordinator, IPowerActions power, Func<ShutdownRules> rules)
    {
        Coordinator = coordinator;
        _power = power;
        _rules = rules;
        Coordinator.StateChanged += (_, now) => { if (now.Phase is ShutdownPhase.Idle or ShutdownPhase.Suppressed) IsTest = false; };
    }

    /// <summary>Feeds a real reading. Ignored while a test runs.</summary>
    public ShutdownState Evaluate(UpsSnapshot? snap, bool connected, DateTimeOffset now) =>
        IsTest ? Coordinator.State : Coordinator.Evaluate(snap, connected, now);

    /// <summary>Starts a test countdown that ends with the real configured action. False if a countdown is already running.</summary>
    public bool StartTest(DateTimeOffset now)
    {
        if (Coordinator.State.Phase is ShutdownPhase.CountingDown or ShutdownPhase.Executing || IsRunningAction) return false;
        var r = _rules();
        var seconds = Math.Max(r.CountdownSeconds, MinTestCountdownSeconds);
        var what = ActionVerb(r.Action);
        IsTest = true;
        var end = PerformsRealActions
            ? $"When this reaches zero the PC will really {what}."
            : "Power actions are simulated here, so at zero the action is only logged.";
        Coordinator.Begin($"Test started from Shutdown rules. {end}", now + TimeSpan.FromSeconds(seconds));
        return true;
    }

    public Task<string?> TickAsync(DateTimeOffset now) => Coordinator.Tick(now) ? RunAsync() : Task.FromResult<string?>(null);

    public Task<string?> NowAsync() => Coordinator.ExecuteNow() ? RunAsync() : Task.FromResult<string?>(null);

    /// <summary>Stops the countdown. A cancelled test returns straight to idle (real outages are still handled).</summary>
    public void Cancel(string why = "Cancelled by user")
    {
        if (Coordinator.State.Phase != ShutdownPhase.CountingDown) return;
        if (IsTest)
        {
            IsTest = false;
            Coordinator.Reset("Test cancelled");
        }
        else Coordinator.Cancel(why);
    }

    private async Task<string?> RunAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return null;
        var r = _rules();
        try
        {
            LastResult = await _power.ExecuteAsync(r.Action, r.ForceCloseApps);
            var simulated = !_power.IsSupported;
            if (simulated) Coordinator.Reset(IsTest ? "Test finished (simulated)" : "Simulated action finished");
            ActionCompleted?.Invoke(LastResult, simulated);
            return LastResult;
        }
        catch (Exception ex)
        {
            LastResult = ex.Message;
            Coordinator.Reset("Action failed");
            ActionFailed?.Invoke(ex);
            return null;
        }
        finally
        {
            IsTest = false;
            Volatile.Write(ref _running, 0);
        }
    }

    public static string ActionVerb(ShutdownAction a) => a switch
    {
        ShutdownAction.Hibernate => "hibernate",
        ShutdownAction.Sleep => "go to sleep",
        _ => "shut down",
    };
}
