using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PKHeX.Core;
using SysBot.Pokemon;
using Xunit;

namespace SysBot.Web.Tests;

public sealed class SVBatchConfirmationTests
{
    private static PK9 Pokemon(ushort species, uint ec)
    {
        var pk = new PK9 { Species=species, EncryptionConstant=ec, PID=ec+1 };
        pk.RefreshChecksum();
        return pk;
    }

    private sealed class Session
    {
        public readonly PK9 Offered = Pokemon(664, 0xE9B8B189);
        public readonly PK9 Outgoing = Pokemon(979, 0xC8085B78);
        public readonly List<int> Presses = [];
        public readonly List<int> Delays = [];
        public readonly List<string> Logs = [];
        public bool PartnerValid = true;
        public bool Changed;
        public int CompleteAfterPress = 2;
        public PK9? InitialPreview;
        public PK9? AfterSelection;
        public CancellationTokenSource? CancelAfterSelect;
        public Task<PokeTradeResult> Run(int max = 3, CancellationToken token = default) => SVBatchConfirmation.RunAsync(
            new SVBatchOfferSnapshot(0x68DBE53148, Offered), new SVBatchOfferSnapshot(0x68956DC000, Outgoing),
            _ => Task.FromResult(PartnerValid),
            _ => Task.FromResult(Presses.Count == 0 ? InitialPreview ?? Offered : AfterSelection ?? Outgoing),
            _ => Task.FromResult(Changed),
            (ms, ct) => { ct.ThrowIfCancellationRequested(); Presses.Add(ms); Changed = Presses.Count >= CompleteAfterPress; if(Presses.Count==1) CancelAfterSelect?.Cancel(); return Task.CompletedTask; },
            (ms, ct) => { ct.ThrowIfCancellationRequested(); Delays.Add(ms); return Task.CompletedTask; }, max, token, Logs.Add);
    }

    [Fact]
    public async Task ObservedSameAddressPreviewSwitchStillSendsConfirmation()
    {
        var s = new Session();
        Assert.Equal(PokeTradeResult.Success, await s.Run());
        Assert.Equal(new[] {3000,1000}, s.Presses); // the second A was blocked by rc.3
        Assert.Equal(new[] {25000}, s.Delays);
        Assert.Contains(s.Logs, l => l.Contains("outbound preview"));
        var original = new SVBatchOfferSnapshot(0x68DBE53148, s.Offered);
        Assert.True(original.Matches(s.Offered)); // final receipt must still be this individual
        Assert.False(original.Matches(s.Outgoing)); // own preview is NEVER proof of receipt
    }

    [Fact]
    public async Task NormalUnchangedPartnerPreviewAlsoWorks()
    {
        var s = new Session();s.AfterSelection=s.Offered;
        Assert.Equal(PokeTradeResult.Success,await s.Run());
        Assert.Equal(2,s.Presses.Count);
    }

    [Fact]
    public async Task OwnPreviewBeforeWeSelectIsNotAccepted()
    {
        var s = new Session();s.InitialPreview=s.Outgoing;
        Assert.Equal(PokeTradeResult.SuspiciousActivity,await s.Run());
        Assert.Empty(s.Presses);
    }

    [Fact]
    public async Task UnexpectedThirdIndividualAfterSelectionStopsFurtherButtons()
    {
        var s = new Session {AfterSelection=Pokemon(133,98765)};
        Assert.Equal(PokeTradeResult.SuspiciousActivity,await s.Run());
        Assert.Single(s.Presses);
        Assert.Empty(s.Delays);
    }

    [Fact]
    public async Task CorruptOwnPreviewIsNotAccepted()
    {
        var s = new Session();s.AfterSelection=(PK9)s.Outgoing.Clone();s.AfterSelection.Species=25;
        Assert.Equal(PokeTradeResult.SuspiciousActivity,await s.Run());
        Assert.Single(s.Presses);
    }

    [Fact]
    public async Task PartnerLossBlocksSelection()
    {
        var s=new Session {PartnerValid=false};
        Assert.Equal(PokeTradeResult.TrainerLeft,await s.Run());
        Assert.Empty(s.Presses);
    }

    [Fact]
    public async Task ValidPreviewWithoutWorkSlotChangeNeverMeansCompleted()
    {
        var s=new Session {CompleteAfterPress=int.MaxValue};
        Assert.Equal(PokeTradeResult.TrainerTooSlow,await s.Run(3));
        Assert.Equal(4,s.Presses.Count);
        Assert.Empty(s.Delays);
    }

    [Fact]
    public async Task CancellationAfterSelectionStopsConfirmation()
    {
        using var stop=new CancellationTokenSource();
        var s=new Session {CancelAfterSelect=stop};
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>s.Run(token:stop.Token));
        Assert.Single(s.Presses);
    }
    [Fact]
    public void NextRoundRejectsPreviousOutboundAndReceivedPreviews()
    {
        var s = new Session();
        var previousOffer = new SVBatchOfferSnapshot(0x1000, s.Offered);
        Assert.False(SVBatchOfferSnapshot.IsFreshNextOffer(s.Outgoing, previousOffer, s.Outgoing, s.Offered));
        Assert.False(SVBatchOfferSnapshot.IsFreshNextOffer(s.Offered, previousOffer, s.Outgoing, s.Offered));
        Assert.False(SVBatchOfferSnapshot.IsFreshNextOffer(new PK9(), previousOffer, s.Outgoing, s.Offered));
        Assert.True(SVBatchOfferSnapshot.IsFreshNextOffer(Pokemon(133, 4567), previousOffer, s.Outgoing, s.Offered));
    }

}
