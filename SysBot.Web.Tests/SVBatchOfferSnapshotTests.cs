using System;
using System.Threading;
using System.Threading.Tasks;
using PKHeX.Core;
using SysBot.Pokemon;
using Xunit;

namespace SysBot.Web.Tests;

public sealed class SVBatchOfferSnapshotTests
{
    // Synthetic data for matching policy tests only; no hardware or legality claims.
    private static PK9 Pokemon(ushort species = 25, uint ec = 1234)
    {
        var pk = new PK9 { Species = species, EncryptionConstant = ec, PID = 42 };
        pk.RefreshChecksum();
        return pk;
    }

    [Fact]
    public async Task SelectingOurPokemonDoesNotRedirectTheCapturedOfferRead()
    {
        const ulong partnerBuffer = 0x1000, ownPreviewBuffer = 0x2000;
        var partner = Pokemon();
        var own = Pokemon(979, 999);
        var captured = new SVBatchOfferSnapshot(partnerBuffer, partner);
        ulong nowResolvedPreview = ownPreviewBuffer;
        Assert.NotEqual(captured.Address, nowResolvedPreview);
        int reads = 0;
        Assert.True(await captured.ValidateAsync((address, _) =>
        {
            reads++;
            Assert.Equal(partnerBuffer, address);
            return Task.FromResult(address == partnerBuffer ? partner : own);
        }, default));
        Assert.Equal(1, reads);
        Assert.False(captured.Matches(own));
    }

    [Fact]
    public async Task RealReplacementAtCapturedAddressIsStillRejected()
    {
        var captured = new SVBatchOfferSnapshot(0x1000, Pokemon());
        Assert.False(await captured.ValidateAsync((_, _) => Task.FromResult(Pokemon(133, 9876)), default));
    }

    [Fact]
    public void SnapshotDoesNotChangeWhenSourceObjectIsMutated()
    {
        var pk = Pokemon();
        var captured = new SVBatchOfferSnapshot(0x1000, pk);
        pk.EncryptionConstant++;
        pk.RefreshChecksum();
        Assert.False(captured.Matches(pk));
    }

    [Fact]
    public void InvalidChecksumAtCapturedBufferIsRejected()
    {
        var pk = Pokemon();
        var captured = new SVBatchOfferSnapshot(0x1000, pk);
        pk.Species = 133;
        Assert.False(pk.ChecksumValid);
        Assert.False(captured.Matches(pk));
    }

    [Fact]
    public async Task CancelledRunDoesNotTouchOfferBuffer()
    {
        var captured = new SVBatchOfferSnapshot(0x1000, Pokemon());
        int reads = 0;
        using var stop = new CancellationTokenSource();stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => captured.ValidateAsync((_, _) =>
        { reads++; return Task.FromResult(Pokemon()); }, stop.Token));
        Assert.Equal(0, reads);
    }

    [Fact]
    public void NoCaptureForUnknownAddressOrEmptyMaterial()
    {
        Assert.Throws<ArgumentException>(() => new SVBatchOfferSnapshot(0, Pokemon()));
        Assert.Throws<ArgumentException>(() => new SVBatchOfferSnapshot(0x1000, new PK9()));
    }
}
