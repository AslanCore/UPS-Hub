namespace UpsHub.Core.Monitoring;

/// <summary>Exponential reconnect delay with jitter. Perpetual: it never gives up (see BigWebstas fork, nutdotnet #233).</summary>
public sealed class Backoff
{
    private readonly TimeSpan _min, _max;
    private readonly Random _rng;
    public int Attempt { get; private set; }

    public Backoff(TimeSpan? min = null, TimeSpan? max = null, int? seed = null)
    {
        _min = min ?? TimeSpan.FromSeconds(1);
        _max = max ?? TimeSpan.FromSeconds(60);
        _rng = seed is null ? Random.Shared : new Random(seed.Value);
    }

    public TimeSpan Next()
    {
        Attempt++;
        var exp = Math.Min(_max.TotalMilliseconds, _min.TotalMilliseconds * Math.Pow(2, Math.Min(Attempt - 1, 20)));
        var jitter = 0.8 + _rng.NextDouble() * 0.4;
        return TimeSpan.FromMilliseconds(Math.Max(_min.TotalMilliseconds * 0.5, Math.Min(_max.TotalMilliseconds, exp * jitter)));
    }

    public void Reset() => Attempt = 0;
}
