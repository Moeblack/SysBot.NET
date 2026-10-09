using System;
using System.Threading;
using System.Threading.Tasks;
using PKHeX.Core;
using PKHeX.Core.Searching;

namespace SysBot.Pokemon;

/// <summary>
/// A validated offer buffer belongs to one exchange, not to the currently selected preview.
/// Freeze its address before pressing A; resolve a new buffer only at the next offer boundary.
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

    public async Task<bool> ValidateAsync(Func<ulong, CancellationToken, Task<PK9>> read, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Matches(await read(Address, token).ConfigureAwait(false));
    }
}
