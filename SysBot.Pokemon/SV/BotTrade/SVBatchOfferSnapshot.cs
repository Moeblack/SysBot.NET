using System;
using System.Threading;
using System.Threading.Tasks;
using PKHeX.Core;
using PKHeX.Core.Searching;

namespace SysBot.Pokemon;

/// <summary>
/// Immutable identity captured before selection. Address denotes the observed preview buffer,
/// not a promise that its contents remain the partner offer: SV also writes our preview there.
/// The confirmation state machine distinguishes both identities; the original offer is retained
/// for the independent final receipt check.
/// </summary>
public sealed class SVBatchOfferSnapshot
{
    public ulong Address { get; }
    private readonly uint encryptionConstant;
    private readonly string details;

    public SVBatchOfferSnapshot(ulong address, PK9 offered)
    {
        if (address == 0 || offered.Species == 0 || !offered.ChecksumValid)
            throw new ArgumentException("Cannot capture an invalid trade offer.");
        Address = address;
        encryptionConstant = offered.EncryptionConstant;
        details = SearchUtil.HashByDetails(offered);
    }

    public bool Matches(PK9 offered) => offered.Species != 0 && offered.ChecksumValid &&
        offered.EncryptionConstant == encryptionConstant && SearchUtil.HashByDetails(offered) == details;

    public static bool IsFreshNextOffer(PK9 candidate, SVBatchOfferSnapshot? previousOffer, PK9? lastSent, PK9 workSlot)
    {
        if (candidate.Species == 0 || !candidate.ChecksumValid) return false;
        if (previousOffer?.Matches(candidate) == true) return false;
        static bool Same(PK9 a, PK9 b) => a.EncryptionConstant == b.EncryptionConstant &&
            SearchUtil.HashByDetails(a) == SearchUtil.HashByDetails(b);
        return !Same(candidate, workSlot) && (lastSent is null || !Same(candidate, lastSent));
    }

    public async Task<bool> ValidateAsync(Func<ulong, CancellationToken, Task<PK9>> read, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Matches(await read(Address, token).ConfigureAwait(false));
    }
}
