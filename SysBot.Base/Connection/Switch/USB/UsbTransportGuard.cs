using System;
using System.IO;
using System.Threading;

namespace SysBot.Base;

/// <summary>Opt-in fail-stop policy. A failed handle must never send further commands until explicitly reopened.</summary>
public sealed class UsbTransportGuard
{
    private Exception? fault;
    public bool Enabled { get; set; }
    public bool IsFaulted => Volatile.Read(ref fault) is not null;
    public event Action<Exception>? Faulted;

    public void Reset() => Interlocked.Exchange(ref fault, null);

    public void ThrowIfFaulted()
    {
        if (Enabled && Volatile.Read(ref fault) is { } cause)
            throw new IOException("USB communication failed; reopen the connection before issuing further commands.", cause);
    }

    public void ValidateTransfer(bool success, int transferred, int expected, bool exact, string operation)
    {
        if (!Enabled) return;
        if (!success || (exact ? transferred != expected : transferred <= 0 || transferred > expected))
            RecordFailure(new IOException($"USB {operation} failed or incomplete ({transferred}/{expected} bytes)."));
    }

    public void ValidateResponseSize(int size, int capacity = 64 * 1024 * 1024)
    {
        if (Enabled && (size <= 0 || size > capacity))
            RecordFailure(new IOException($"Invalid USB response size: {size}."));
    }

    public void RecordFailure(Exception cause)
    {
        if (!Enabled) return;
        if (Interlocked.CompareExchange(ref fault, cause, null) is null)
            Faulted?.Invoke(cause);
        ThrowIfFaulted();
    }
}
