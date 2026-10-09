using System;
using System.Threading;
using System.Threading.Tasks;
using PKHeX.Core;

namespace SysBot.Pokemon;

/// <summary>
/// SV reuses the preview buffer for the partner's offer and our selected Pokémon.
/// Before selecting, require the original offer. Afterwards accept only that offer or
/// our exact outbound preview; completion still requires a changed work slot AND the
/// caller's separate received-Pokémon comparison against the original partner offer.
/// </summary>
public static class SVBatchConfirmation
{
    public static async Task<PokeTradeResult> RunAsync(
        SVBatchOfferSnapshot offered, SVBatchOfferSnapshot outgoing,
        Func<CancellationToken, Task<bool>> validatePartner,
        Func<CancellationToken, Task<PK9>> readPreview,
        Func<CancellationToken, Task<bool>> workSlotChanged,
        Func<int, CancellationToken, Task> pressA,
        Func<int, CancellationToken, Task> delay,
        int maxConfirmAttempts, CancellationToken token,
        Action<string>? log = null)
    {
        token.ThrowIfCancellationRequested();
        if (!await validatePartner(token).ConfigureAwait(false)) return PokeTradeResult.TrainerLeft;
        var before = await readPreview(token).ConfigureAwait(false);
        if (!offered.Matches(before))
        {
            log?.Invoke("Batch confirmation rejected before selection: preview is not the captured partner offer.");
            return PokeTradeResult.SuspiciousActivity;
        }
        token.ThrowIfCancellationRequested();
        log?.Invoke("Batch confirmation: captured offer verified; selecting our Pokémon with A.");
        await pressA(3_000, token).ConfigureAwait(false);
        bool ownPreviewLogged = false;
        for (int i = 0; i < maxConfirmAttempts; i++)
        {
            token.ThrowIfCancellationRequested();
            if (await workSlotChanged(token).ConfigureAwait(false))
            {
                await delay(25_000, token).ConfigureAwait(false);
                return PokeTradeResult.Success;
            }
            if (!await validatePartner(token).ConfigureAwait(false)) return PokeTradeResult.TrainerLeft;
            var preview = await readPreview(token).ConfigureAwait(false);
            if (!offered.Matches(preview))
            {
                if (!outgoing.Matches(preview))
                {
                    log?.Invoke($"Batch confirmation rejected: preview is neither captured offer nor outbound Pokémon (species={preview.Species}, EC={preview.EncryptionConstant:X8}, checksum={preview.ChecksumValid}).");
                    return PokeTradeResult.SuspiciousActivity;
                }
                if (!ownPreviewLogged)
                {
                    log?.Invoke("Batch confirmation: shared buffer now contains our outbound preview; continuing confirmation, not a partner-offer change.");
                    ownPreviewLogged = true;
                }
            }
            token.ThrowIfCancellationRequested();
            await pressA(1_000, token).ConfigureAwait(false);
            if (await workSlotChanged(token).ConfigureAwait(false))
            {
                await delay(25_000, token).ConfigureAwait(false);
                return PokeTradeResult.Success;
            }
        }
        return PokeTradeResult.TrainerTooSlow;
    }
}
