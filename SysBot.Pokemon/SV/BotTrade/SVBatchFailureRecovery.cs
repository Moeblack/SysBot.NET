using System;
using System.Threading;
using System.Threading.Tasks;

namespace SysBot.Pokemon;

public static class SVBatchFailureRecovery
{
    public static async Task<bool> TryExitAsync(PokeTradeResult result,
        Func<CancellationToken, Task> exitTradeToPortal, CancellationToken token)
    {
        // Disconnected hardware and an explicitly stopped run cannot navigate the game.
        if (token.IsCancellationRequested || result is PokeTradeResult.Success or
            PokeTradeResult.RoutineCancel or PokeTradeResult.ExceptionConnection or PokeTradeResult.ExceptionInternal)
            return false;
        await exitTradeToPortal(token).ConfigureAwait(false);
        return true;
    }
}
