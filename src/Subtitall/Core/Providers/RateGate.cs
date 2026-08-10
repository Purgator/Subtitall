namespace Subtitall.Core.Providers;

/// <summary>Spaces requests to a provider so batch searches stay under its rate limit.</summary>
public sealed class RateGate(TimeSpan minInterval)
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private DateTime _lastRequestUtc = DateTime.MinValue;

    public async Task WaitAsync(CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            var wait = _lastRequestUtc + minInterval - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);
            _lastRequestUtc = DateTime.UtcNow;
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
