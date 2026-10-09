using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PKHeX.Core;
using PKHeX.Core.Searching;
using SysBot.Pokemon;
using SysBot.Pokemon.Web;
using Xunit;

namespace SysBot.Web.Tests;

public sealed class SVBatchCoordinatorTests
{
    private static PokeTradeDetail<PK9>[] Make(int count)
    {
        // These PK9 objects exercise lifecycle only, never generation or a real device.
        var members = Enumerable.Range(0, count).Select(i => new PokeTradeDetail<PK9>
        {
            Code = 3180318,
            TradeData = new PK9 { Species = (ushort)(i + 1) },
            Trainer = new PokeTradeTrainerInfo("Local Web"),
            Notifier = new PokeTradeLogNotifier<PK9>(),
            Type = PokeTradeType.Specific,
            IsRetry = true,
        }).ToArray();
        members[0].BatchTrades = Array.AsReadOnly(members);
        return members;
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(12)]
    public void SessionPolicySearchesOnceKeepsIntermediateTradesAndClosesLast(int count)
    {
        var members = Make(count);
        var coordinator = new SVBatchTradeCoordinator<PK9>(members[0]);
        Assert.True(coordinator.HasValidShape(members[0]));
        int searches = 0, closes = 0;
        for (int i = 0; i < count; i++)
        {
            coordinator.BeginMember(i);
            if (!coordinator.IsContinuation) searches++;
            if (!coordinator.KeepConnectionOpen) closes++;
            Assert.Equal(i != 0, coordinator.IsContinuation);
            Assert.Equal(i < count - 1, coordinator.KeepConnectionOpen);
            coordinator.CurrentCompleted = true;
        }
        Assert.Equal(1, searches);
        Assert.Equal(1, closes);
        Assert.Throws<InvalidOperationException>(() => coordinator.BeginMember(0));
    }

    [Fact]
    public async Task PartialFailureNeverCancelsCompletedMemberOrAdvances()
    {
        var members = Make(4);
        var coordinator = new SVBatchTradeCoordinator<PK9>(members[0]);
        coordinator.BeginMember(0);
        coordinator.CurrentCompleted = true;
        coordinator.BeginMember(1);
        var events = new List<string>();
        await coordinator.NotifyAbortedAsync(
            (member, message) => { Assert.StartsWith("BatchNotStarted:", message); events.Add("not-started:" + Array.IndexOf(members, member)); return Task.CompletedTask; },
            (member, _) => { events.Add("cancel:" + Array.IndexOf(members, member)); return Task.CompletedTask; },
            PokeTradeResult.ExceptionConnection);
        Assert.Equal(new[] { "cancel:1", "not-started:2", "cancel:2", "not-started:3", "cancel:3" }, events);
        Assert.Throws<InvalidOperationException>(() => coordinator.BeginMember(2));
        Assert.All(members.Skip(1), m => Assert.True(m.IsRetry));
    }

    [Fact]
    public async Task FailureBetweenMembersPreservesSuccessAndStopsRemaining()
    {
        var members = Make(3);
        var coordinator = new SVBatchTradeCoordinator<PK9>(members[0]);
        coordinator.BeginMember(0);
        coordinator.CurrentCompleted = true;
        var cancelled = new List<PokeTradeDetail<PK9>>();
        await coordinator.NotifyAbortedAsync((_, _) => Task.CompletedTask,
            (member, _) => { cancelled.Add(member); return Task.CompletedTask; }, PokeTradeResult.RoutineCancel);
        Assert.Equal(members.Skip(1), cancelled);
    }

    [Fact]
    public async Task BrokenNotifierDoesNotStrandOtherMembers()
    {
        var members = Make(3);
        var coordinator = new SVBatchTradeCoordinator<PK9>(members[0]);
        var cancelled = new List<PokeTradeDetail<PK9>>();
        await Assert.ThrowsAsync<AggregateException>(() => coordinator.NotifyAbortedAsync(
            (_, _) => throw new InvalidOperationException("notification failed"),
            (member, _) => { cancelled.Add(member); return Task.CompletedTask; }, PokeTradeResult.ExceptionInternal));
        Assert.Equal(members, cancelled);
    }

    [Fact]
    public void SnapshotIsStableAndInvalidShapesAreRejected()
    {
        var members = Make(2);
        var root = members[0];
        var originalSecond = members[1];
        var coordinator = new SVBatchTradeCoordinator<PK9>(root);
        members[1] = root;
        Assert.Same(originalSecond, coordinator.Members[1]);
        Assert.True(coordinator.HasValidShape(root));
        Assert.False(new SVBatchTradeCoordinator<PK9>(root).HasValidShape(root));
        var one = Make(1); Assert.False(new SVBatchTradeCoordinator<PK9>(one[0]).HasValidShape(one[0]));
        var tooMany = Make(13); Assert.False(new SVBatchTradeCoordinator<PK9>(tooMany[0]).HasValidShape(tooMany[0]));
        var wrongCode = Make(2); wrongCode[1] = wrongCode[1] with { Code = 12345678 };
        Assert.False(new SVBatchTradeCoordinator<PK9>(wrongCode[0]).HasValidShape(wrongCode[0]));
        var wrongTrainer = Make(2); wrongTrainer[1] = wrongTrainer[1] with { Trainer = new PokeTradeTrainerInfo("Other") };
        Assert.False(new SVBatchTradeCoordinator<PK9>(wrongTrainer[0]).HasValidShape(wrongTrainer[0]));
    }

    [Fact]
    public async Task ReceiptHashSurvivesHandlerChangeButChecksumCanChange()
    {
        var generated = await new PokemonGenerator().GenerateAsync("Pikachu\nLevel: 100\n- Thunderbolt");
        var offered = Assert.Single(generated).Pokemon;
        var received = (PK9)offered.Clone();
        var receiver = new SimpleTrainerInfo(GameVersion.VL) { OT = "Receiver", TID16 = 12345, SID16 = 54321, Language = (int)LanguageID.English };
        received.UpdateHandler(receiver);
        received.RefreshChecksum();
        Assert.Equal(offered.EncryptionConstant, received.EncryptionConstant);
        Assert.Equal(SearchUtil.HashByDetails(offered), SearchUtil.HashByDetails(received));
    }
}
