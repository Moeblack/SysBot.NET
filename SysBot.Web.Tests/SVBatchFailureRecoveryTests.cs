using System;
using System.Threading;
using System.Threading.Tasks;
using SysBot.Pokemon;
using Xunit;

namespace SysBot.Web.Tests;

public sealed class SVBatchFailureRecoveryTests
{
    [Theory]
    [InlineData(PokeTradeResult.TrainerTooSlow)]
    [InlineData(PokeTradeResult.TrainerLeft)]
    [InlineData(PokeTradeResult.SuspiciousActivity)]
    [InlineData(PokeTradeResult.NoTrainerFound)]
    [InlineData(PokeTradeResult.IllegalTrade)]
    [InlineData(PokeTradeResult.RecoverOpenBox)]
    public async Task OrdinaryFailuresUseNativeExitExactlyOnce(PokeTradeResult result)
    {
        var exits = 0;
        Assert.True(await SVBatchFailureRecovery.TryExitAsync(result, _ => { exits++; return Task.CompletedTask; }, default));
        Assert.Equal(1, exits);
    }

    [Theory]
    [InlineData(PokeTradeResult.RoutineCancel)]
    [InlineData(PokeTradeResult.ExceptionConnection)]
    [InlineData(PokeTradeResult.ExceptionInternal)]
    [InlineData(PokeTradeResult.Success)]
    public async Task DisconnectedOrStoppedRunDoesNotNavigate(PokeTradeResult result)
    {
        Assert.False(await SVBatchFailureRecovery.TryExitAsync(result, _ => throw new Exception("unexpected exit"), default));
    }

    [Fact]
    public async Task CancellationDoesNotNavigate()
    {
        using var stop = new CancellationTokenSource();stop.Cancel();
        Assert.False(await SVBatchFailureRecovery.TryExitAsync(PokeTradeResult.TrainerTooSlow, _ => throw new Exception("unexpected exit"), stop.Token));
    }

    [Fact]
    public async Task FailedNativeExitIsNotReportedAsRecovery()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => SVBatchFailureRecovery.TryExitAsync(
            PokeTradeResult.TrainerTooSlow, _ => throw new InvalidOperationException("native exit failed"), default));
    }
}
