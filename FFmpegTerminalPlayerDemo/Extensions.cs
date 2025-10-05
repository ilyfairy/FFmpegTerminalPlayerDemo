using Sdcb.FFmpeg.Raw;

public static class Extensions
{
    extension(ref AVRational rational)
    {
        public double Double => (double)rational.Num / rational.Den;
    }

    extension(ref AVStream avStream)
    {
        public TimeSpan DurationTimeSpan => TimeSpan.FromSeconds((double)avStream.duration * avStream.time_base.Num / avStream.time_base.Den);
    }
    extension(SemaphoreSlim semaphoreSlim)
    {
        public Task<SemaphoreSlimScope> WaitScopeAsync() => SemaphoreSlimScope.WaitAsync(semaphoreSlim);
    }
}

public struct SemaphoreSlimScope : IDisposable
{
    private SemaphoreSlim _semaphoreSlim;
    public static async Task<SemaphoreSlimScope> WaitAsync(SemaphoreSlim semaphoreSlim, CancellationToken cancellationToken = default)
    {
        SemaphoreSlimScope scope = new()
        {
            _semaphoreSlim = semaphoreSlim,
        };
        await semaphoreSlim.WaitAsync(cancellationToken);
        return scope;
    }

    public void Dispose() => _semaphoreSlim.Release();
}