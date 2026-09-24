namespace HdrCapture.Core;

internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = "Local\\HDRCapture-8AD812CE-0EF3-4C80-9CF0-FB64D4B0B749";
    private Mutex? _mutex;

    private SingleInstance(bool isOwner, Mutex? mutex)
    {
        IsOwner = isOwner;
        _mutex = mutex;
    }

    public bool IsOwner { get; }

    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return new SingleInstance(false, null);
        }

        return new SingleInstance(true, mutex);
    }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _mutex.Dispose();
        _mutex = null;
    }
}
