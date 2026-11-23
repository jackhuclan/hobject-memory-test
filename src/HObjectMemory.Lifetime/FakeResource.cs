namespace HObjectMemory.Lifetime;

public sealed class FakeResource : IDisposable
{
    private int _disposeCount;

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public void Dispose()
    {
        Interlocked.Increment(ref _disposeCount);
    }
}
