using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace UiAtlas.Core.Recording.Windows;

/// <summary>One nonqueued operation per user/session across recorder, mapper and MCP processes.</summary>
public sealed class DataGridOperationGate : IDisposable
{
    private Semaphore? _semaphore;
    private DataGridOperationGate(Semaphore semaphore) => _semaphore = semaphore;

    public static DataGridOperationGate? TryAcquire()
    {
        var semaphore = new Semaphore(1, 1, SharedName());
        if (semaphore.WaitOne(0)) return new(semaphore);
        semaphore.Dispose();
        return null;
    }

    internal static string SharedName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User?.Value ?? throw new InvalidOperationException("user-identity-unavailable");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..24];
        return $"Local\\UiAtlas.DataGridOperation.v1.{Process.GetCurrentProcess().SessionId}.{hash}";
    }

    public void Dispose()
    {
        var semaphore = Interlocked.Exchange(ref _semaphore, null);
        if (semaphore is null) return;
        try { semaphore.Release(); }
        finally { semaphore.Dispose(); }
    }
}
