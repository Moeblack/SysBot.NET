using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using PKHeX.Core;
using PKHeX.Core.Searching;
using SysBot.Base;
using static SysBot.Pokemon.PokeDataOffsetsSV;

namespace SysBot.Pokemon;

public partial class PokeTradeBotSV
{
    private sealed class BatchSessionContext(SVBatchTradeCoordinator<PK9> coordinator)
    {
        public TradePartnerSV? Partner { get; set; }
        public PK9? LastReceived { get; set; }
        public PK9? LastSent { get; set; }
        public PK9? Offered { get; set; }
        public SVBatchOfferSnapshot? OfferSnapshot { get; set; }
        public bool CurrentCompleted { get => coordinator.CurrentCompleted; set => coordinator.CurrentCompleted = value; }
    }

    private async Task PerformBatchTrade(SAV9SV sav, PokeTradeDetail<PK9> root, CancellationToken token)
    {
        SVBatchTradeCoordinator<PK9>? coordinator = null;
        var failure = PokeTradeResult.ExceptionInternal;
        var failedTradeResult = false;
        root.IsRetry = true; // Batch execution is never replayed through the ordinary retry path.
        try
        {
            coordinator = new SVBatchTradeCoordinator<PK9>(root);
            var context = new BatchSessionContext(coordinator);
            if (!ConnectionPolicy.IsLocal || Config.Connection.Protocol != SwitchProtocol.USB || !coordinator.HasValidShape(root))
                throw new InvalidOperationException("SV batches require Specific trades, one root, and local USB mode.");
            foreach (var member in coordinator.Members)
            {
                member.IsRetry = true;
                if (member.TradeData.Species == 0 || !member.TradeData.ChecksumValid || !new LegalityAnalysis(member.TradeData).Valid)
                {
                    failure = PokeTradeResult.IllegalTrade;
                    throw new InvalidOperationException("Batch contains an invalid Pokémon; no trade was started.");
                }
            }
            for (var i = 0; i < coordinator.Members.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                coordinator.BeginMember(i);
                var member = coordinator.Members[i];
                member.IsProcessing = true;
                failure = await PerformLinkCodeTrade(sav, member, token, context, coordinator.IsContinuation, coordinator.KeepConnectionOpen).ConfigureAwait(false);
                if (!ReferenceEquals(member, root))
                    member.IsProcessing = false;
                if (failure != PokeTradeResult.Success)
                {
                    failedTradeResult = true;
                    throw new InvalidOperationException($"SV batch interrupted: {failure}.");
                }
            }
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
                failure = PokeTradeResult.RoutineCancel;
            else if (ex is SocketException)
                failure = PokeTradeResult.ExceptionConnection;
            else if (failure == PokeTradeResult.Success)
                failure = PokeTradeResult.ExceptionInternal;
            try
            {
                var rootIncluded = false;
                if (coordinator is not null)
                {
                    foreach (var member in coordinator.Members)
                        rootIncluded |= ReferenceEquals(member, root);
                    await coordinator.NotifyAbortedAsync((member, message) => member.SendNotification(this, message),
                        (member, result) => member.TradeCanceled(this, result), failure).ConfigureAwait(false);
                }
                if (!rootIncluded)
                    await root.TradeCanceled(this, failure).ConfigureAwait(false);
            }
            catch (Exception notificationError)
            {
                Log($"Batch interruption notification failed: {notificationError.Message}");
            }
            if (failedTradeResult)
            {
                try
                {
                    if (await SVBatchFailureRecovery.TryExitAsync(failure, ct => ExitTradeToPortal(false, ct), token).ConfigureAwait(false))
                    {
                        Log($"SV batch recovered; {failure}. Exited trade to Portal; failed batch is not requeued. Ready for new orders.");
                        return;
                    }
                }
                catch (Exception recoveryError)
                {
                    Log($"Batch exit failed: {recoveryError.Message}");
                }
            }
            // Actual transport faults/cancellation or failed native recovery still stop the run.
            // Never replay this batch through InnerLoop's reconnect-and-dequeue path.
            throw new InvalidOperationException($"SV batch stopped; {ex.Message} Completed trades are retained, remaining trades canceled. Manual restart required.", ex);
        }
        finally
        {
            if (coordinator is not null)
                foreach (var member in coordinator.Members)
                    if (member is not null)
                        member.IsProcessing = false;
            root.IsProcessing = false;
        }
    }

    private async Task<bool> ValidateBatchPartner(BatchSessionContext context, CancellationToken token)
    {
        if (await IsConnectedOnline(ConnectedOffset, token).ConfigureAwait(false))
        {
            Log("Batch guard: game is online; stopped before confirmation.");
            return false;
        }
        if (!await IsInBox(PortalOffset, token).ConfigureAwait(false))
        {
            Log("Batch guard: no longer in the trade box.");
            return false;
        }
        var partner = await TryGetLocalTradePartner(token).ConfigureAwait(false);
        var expected = context.Partner;
        if (partner is null || expected is null || partner.TrainerName != expected.TrainerName ||
            partner.TID7 != expected.TID7 || partner.SID7 != expected.SID7)
        {
            Log("Batch guard: partner identity unavailable or changed.");
            return false;
        }
        // Do NOT treat the current preview as exclusively the partner's offer.
        // Confirmation uses an immutable offer fingerprint plus the exact outbound fingerprint;
        // the same buffer can switch contents when our Pokémon is selected.
        LocalTradePartner = partner;
        return true;
    }

    private async Task<PokeTradeResult> PrepareBatchContinuation(SAV9SV sav, PokeTradeDetail<PK9> member,
        BatchSessionContext context, CancellationToken token)
    {
        // Same-session B1S1 continuation adapted from FusionBot (Secludedly), SHA
        // 31594ca8bf60afdb6a8ddfa32064569398df334e, PokeTradeBotSV.cs lines 971-1014:
        // https://github.com/Secludedly/FusionBot/blob/31594ca8bf60afdb6a8ddfa32064569398df334e/SysBot.Pokemon/SV/BotTrade/PokeTradeBotSV.cs#L971-L1014
        // Confirm already waited 25s after slot change and result verification another
        // 5s. No unverified B navigation. A box flag is NOT animation evidence.
        if (context.LastReceived is null || !await ValidateBatchPartner(context, token).ConfigureAwait(false))
            return PokeTradeResult.TrainerLeft;
        var before = await ReadPokemon(BoxStartOffset, BoxFormatSlotSize, token).ConfigureAwait(false);
        if (!SameBatchReceived(before, context.LastReceived))
            return PokeTradeResult.SuspiciousActivity;
        if (!await ValidateBatchPartner(context, token).ConfigureAwait(false))
            return PokeTradeResult.TrainerLeft;
        await Task.Delay(1_000, token).ConfigureAwait(false);
        var stable = await ReadPokemon(BoxStartOffset, BoxFormatSlotSize, token).ConfigureAwait(false);
        if (!SameBatchReceived(stable, before))
            return PokeTradeResult.SuspiciousActivity;
        // Require a fresh offer BEFORE writing the next mon. Pointer is refreshed above;
        // an unchanged offer times out instead of confirming stale data or re-searching.
        await member.SendNotification(this, "BatchWaitingOffer: Please select the next Pokémon to offer in this existing trade session.").ConfigureAwait(false);
        var waitMilliseconds = Math.Clamp(Hub.Config.Trade.TradeWaitTime, 5, 120) * 1_000;
        var (pointerValid, offeredOffset) = await ValidatePointerAll(Offsets.LinkTradePartnerPokemonPointer, token).ConfigureAwait(false);
        if (!pointerValid || offeredOffset == 0)
            return PokeTradeResult.TrainerTooSlow;
        TradePartnerOfferedOffset = offeredOffset;
        PK9? offer = null;
        for (int elapsed = 0; elapsed < waitMilliseconds; elapsed += 500)
        {
            token.ThrowIfCancellationRequested();
            if (!await ValidateBatchPartner(context, token).ConfigureAwait(false))
                return PokeTradeResult.TrainerLeft;
            var candidate = await ReadPokemon(offeredOffset, BoxFormatSlotSize, token).ConfigureAwait(false);
            if (SVBatchOfferSnapshot.IsFreshNextOffer(candidate, context.OfferSnapshot, context.LastSent, stable))
            {
                // Wait for a stable full candidate rather than a transient preview update.
                await Task.Delay(500, token).ConfigureAwait(false);
                var candidateSnapshot = new SVBatchOfferSnapshot(offeredOffset, candidate);
                var again = await ReadPokemon(offeredOffset, BoxFormatSlotSize, token).ConfigureAwait(false);
                if (candidateSnapshot.Matches(again)) { offer = again; break; }
            }
            await Task.Delay(500, token).ConfigureAwait(false);
        }
        if (offer is null)
        {
            Log("Batch guard: no fresh partner material before timeout; previous offer/outbound/work-slot previews were ignored.");
            return PokeTradeResult.TrainerTooSlow;
        }
        if (!new LegalityAnalysis(offer).Valid)
            return PokeTradeResult.IllegalTrade;
        if (!await ValidateBatchPartner(context, token).ConfigureAwait(false))
            return PokeTradeResult.TrainerLeft;
        // Evolution animations/learning prompts have no validated selection-state signal.
        if (TradeEvolutions.WillTradeEvolve(offer.Species, offer.Form, offer.HeldItem, member.TradeData.Species) ||
            TradeEvolutions.WillTradeEvolve(member.TradeData.Species, member.TradeData.Form, member.TradeData.HeldItem, offer.Species))
            return PokeTradeResult.TradeEvolveNotAllowed;
        var finalSlot = await ReadPokemon(BoxStartOffset, BoxFormatSlotSize, token).ConfigureAwait(false);
        if (!SameBatchReceived(finalSlot, stable))
            return PokeTradeResult.SuspiciousActivity;
        await SetBoxPokemonAbsolute(BoxStartOffset, member.TradeData, token, sav).ConfigureAwait(false);
        await Task.Delay(1_000, token).ConfigureAwait(false);
        await member.SendNotification(this, "Ready for the next Pokémon in this trade session.").ConfigureAwait(false);
        await Task.Delay(5_000, token).ConfigureAwait(false);
        return PokeTradeResult.Success;
    }

    private static bool SameBatchReceived(PK9 current, PK9 expected) => current.Species != 0 && current.ChecksumValid &&
        current.Checksum == expected.Checksum && SearchUtil.HashByDetails(current) == SearchUtil.HashByDetails(expected);
}
