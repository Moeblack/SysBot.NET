using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SysBot.Base;
using SysBot.Pokemon.Web;
using Xunit;

namespace SysBot.Web.Tests;

public sealed class UsbFaultTests
{
    [Fact]
    public void LegacyCallersRemainOptIn()
    {
        var guard = new UsbTransportGuard();
        guard.RecordFailure(new IOException("Pipe error"));
        guard.ThrowIfFaulted();
        Assert.False(guard.IsFaulted);
    }

    [Fact]
    public void FirstFaultLatchesNotifiesOnceAndBlocksCleanupCommands()
    {
        var guard = new UsbTransportGuard { Enabled = true };
        var first = new IOException("Pipe error");
        var notifications = 0;
        guard.Faulted += error => { Assert.Same(first, error); notifications++; };
        var thrown = Assert.Throws<IOException>(() => guard.RecordFailure(first));
        Assert.Same(first, thrown.InnerException);
        Assert.Throws<IOException>(() => guard.RecordFailure(new IOException("later I/O error")));
        var sent = 0;
        Assert.Throws<IOException>(() => { guard.ThrowIfFaulted(); sent++; });
        Assert.Equal(0, sent);
        Assert.Equal(1, notifications);
        guard.Reset();
        guard.ThrowIfFaulted();
        Assert.False(guard.IsFaulted);
    }

    [Fact]
    public void ReadyFollowsTheLiveConnectionLabelNotItsOriginalUsbName()
    {
        using var stop = new CancellationTokenSource();
        var label = "USB-1";
        DeviceView? state = null;
        var monitor = new UsbTradeRunMonitor(1, () => label, value => state = value, stop);
        label = "Trainer-123456";
        monitor.OnLog("Starting main PokeTradeBotSV loop.", "unrelated-bot");
        Assert.Null(state);
        monitor.OnLog("Starting main PokeTradeBotSV loop.", label);
        Assert.Equal("ready", state!.Status);
        Assert.Equal(1, state.Port);
    }

    [Fact]
    public void UsbFaultCancelsRunAndCannotBeOverwrittenByLateReadyLog()
    {
        using var stop = new CancellationTokenSource();
        DeviceView? state = null;
        var reports = 0;
        var monitor = new UsbTradeRunMonitor(1, () => "Trainer", value => { state = value; reports++; }, stop);
        var guard = new UsbTransportGuard { Enabled = true };
        guard.Faulted += monitor.OnFault;
        Assert.Throws<IOException>(() => guard.RecordFailure(new IOException("Pipe error")));
        Assert.True(stop.IsCancellationRequested);
        Assert.Equal("error", state!.Status);
        Assert.Contains("未开始的订单保留", state.Message);
        monitor.OnLog("Starting main PokeTradeBotSV loop.", "Trainer");
        monitor.OnFault(new IOException("second error"));
        Assert.Equal("error", state.Status);
        Assert.Equal(1, reports);
    }
    [Theory]
    [InlineData(false, 2, 4, true)]
    [InlineData(true, 2, 4, true)]
    [InlineData(false, 0, 4, true)]
    [InlineData(true, 0, 512, false)]
    [InlineData(true, 513, 512, false)]
    public void BadOrZeroProgressTransfersLatchFailure(bool success, int actual, int expected, bool exact)
    {
        var guard = new UsbTransportGuard { Enabled = true };
        Assert.Throws<IOException>(() => guard.ValidateTransfer(success, actual, expected, exact, "test"));
        Assert.True(guard.IsFaulted);
    }

    [Theory]
    [InlineData(4, 4, true)]
    [InlineData(2, 512, false)]
    public void ValidTransfersDoNotLatch(int actual, int expected, bool exact)
    {
        var guard = new UsbTransportGuard { Enabled = true };
        guard.ValidateTransfer(true, actual, expected, exact, "test");
        Assert.False(guard.IsFaulted);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public void CorruptResponseLengthsCannotAllocateOrLoop(int size)
    {
        var guard = new UsbTransportGuard { Enabled = true };
        Assert.Throws<IOException>(() => guard.ValidateResponseSize(size));
    }

    [Fact]
    public void BatchStateRejectionIsNotReportedAsUsbDriverFailure()
    {
        using var stop = new CancellationTokenSource();
        DeviceView? state = null;
        var monitor = new UsbTradeRunMonitor(1, () => "Trainer", value => state = value, stop);
        monitor.OnLog("SV batch stopped; SV batch interrupted: SuspiciousActivity.", "Trainer");
        Assert.True(stop.IsCancellationRequested);
        Assert.Equal("error", state!.Status);
        Assert.Contains("不一定是 USB", state.Message);
        monitor.OnLog("Starting main PokeTradeBotSV loop.", "Trainer");
        Assert.Equal("error", state.Status);
    }

}
