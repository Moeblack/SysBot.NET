using System;
using System.Threading.Tasks;
using SysBot.Base;

namespace SysBot.Pokemon;

/// <summary>
/// Connection mode is captured at bot startup. Local trades must never fall back to online trades.
/// </summary>
public sealed class SVTradeConnectionPolicy
{
    public bool IsLocal { get; }
    public bool UsesOnlineIdentity => !IsLocal;

    public SVTradeConnectionPolicy(bool localTrade, SwitchProtocol protocol)
    {
        if (localTrade && protocol != SwitchProtocol.USB)
            throw new InvalidOperationException("SV local trading requires USB. Refusing to fall back to online trading.");
        IsLocal = localTrade;
    }

    public bool IsExpectedState(bool isOnline) => IsLocal != isOnline;

    /// <summary>Converge on the requested mode, including recovery; verify the resulting state.</summary>
    public async Task<bool> EnsureConnectionAsync(Func<Task<bool>> isOnline, Func<Task<bool>> connect, Func<Task<bool>> disconnect)
    {
        if (IsExpectedState(await isOnline().ConfigureAwait(false)))
            return true;

        var changed = await (IsLocal ? disconnect() : connect()).ConfigureAwait(false);
        return changed && IsExpectedState(await isOnline().ConfigureAwait(false));
    }

    public void RequireOnlineMode()
    {
        if (IsLocal)
            throw new InvalidOperationException("An online connection was requested during SV local trading. Stopping this operation.");
    }

    // Called only after the bot has initiated a search from the Poké Portal.
    // Offline sessions need a trade-box state and a distinct trainer, not a Nintendo online ID.
    public bool IsPartnerReady(bool inTradeBox, ulong onlineID, bool distinctTrainer) =>
        IsLocal ? inTradeBox && distinctTrainer : onlineID != 0;

    public static bool IsDistinctTrainer(TradeMyStatus trainer, string hostName, uint hostTID, uint hostSID) =>
        !string.IsNullOrWhiteSpace(trainer.OT) &&
        !(trainer.OT == hostName && trainer.DisplayTID == hostTID && trainer.DisplaySID == hostSID);
}
