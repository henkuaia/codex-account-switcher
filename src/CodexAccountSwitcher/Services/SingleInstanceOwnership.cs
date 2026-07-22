using System.Security.Principal;

namespace CodexAccountSwitcher.Services;

internal sealed class SingleInstanceOwnership : IDisposable
{
    private Mutex? _mutex;

    private SingleInstanceOwnership(Mutex mutex) => _mutex = mutex;

    internal static string CreatePerUserName(string applicationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        return $@"Local\{applicationName}-{userSid}";
    }

    internal static IDisposable? TryAcquire(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        if (createdNew)
        {
            return new SingleInstanceOwnership(mutex);
        }

        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is null)
        {
            return;
        }

        try
        {
            mutex.ReleaseMutex();
        }
        finally
        {
            mutex.Dispose();
        }
    }
}
