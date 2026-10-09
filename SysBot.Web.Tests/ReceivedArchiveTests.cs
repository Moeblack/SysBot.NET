using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PKHeX.Core;
using PKHeX.Core.Searching;
using SysBot.Pokemon;
using SysBot.Pokemon.Web;
using Xunit;

namespace SysBot.Web.Tests;

public sealed class ReceivedArchiveTests
{
    private sealed class Generator : IPokemonGenerator
    {
        public Task<IReadOnlyList<GeneratedPokemon>> GenerateAsync(string text) =>
            Task.FromResult<IReadOnlyList<GeneratedPokemon>>([new("a", "Pikachu", new PK9 { Species = 25 }), new("b", "Eevee", new PK9 { Species = 133 })]);
    }
    private sealed class NoDevice : IWebTradeDevice
    {
        public Task RunAsync(PokeTradeHub<PK9> hub, int port, Action<DeviceView> report, CancellationToken token) => throw new InvalidOperationException("Do not connect hardware in this test.");
    }

    [Fact]
    public async Task ReceiptStoredOnceBeforeNextMemberCanReplaceWorkSlot()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SysBot.Receipt.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var orders = new WebOrders(new Generator(), new NoDevice(), directory, () => new UsbView([], ""));
            var batch = await orders.SubmitAsync(new("unused", Guid.NewGuid().ToString()));
            Assert.True(orders.Hub.Queues.GetQueue(PokeRoutineType.LinkTrade).TryDequeue(out var root, out _, true));
            var received = (await new PokemonGenerator().GenerateAsync("Pikachu\nLevel: 100\n- Thunderbolt"))[0].Pokemon;
            await root.TradeInitialize(null!);
            await root.TradeFinished(null!, received);
            var file = Path.Combine(directory, "received", batch.Orders[0].Id + ".pk9");
            Assert.True(File.Exists(file));
            var restored = new PK9(File.ReadAllBytes(file));
            Assert.True(restored.ChecksumValid);
            Assert.Equal(received.EncryptionConstant, restored.EncryptionConstant);
            Assert.Equal(SearchUtil.HashByDetails(received), SearchUtil.HashByDetails(restored));
            await root.TradeFinished(null!, received); // no second FileMode.CreateNew attempt
            Assert.Single(Directory.GetFiles(Path.Combine(directory, "received")));
            var second = root.BatchTrades![1];
            await second.TradeCanceled(null!, PokeTradeResult.TrainerLeft);
            await second.TradeFinished(null!, received); // late callback cannot turn failure into receipt/success
            Assert.Single(Directory.GetFiles(Path.Combine(directory, "received")));
            Assert.Equal("failed", orders.Snapshot().Orders[1].Status);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ArchiveFailurePreservesCompletedTradeButStopsBatchContinuation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SysBot.Receipt.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var orders = new WebOrders(new Generator(), new NoDevice(), directory, () => new UsbView([], ""));
            await orders.SubmitAsync(new("unused", Guid.NewGuid().ToString()));
            Assert.True(orders.Hub.Queues.GetQueue(PokeRoutineType.LinkTrade).TryDequeue(out var root, out _, true));
            File.WriteAllText(Path.Combine(directory, "received"), "block directory creation");
            var coordinator = new SVBatchTradeCoordinator<PK9>(root);
            coordinator.BeginMember(0);
            await root.TradeInitialize(null!);
            coordinator.CurrentCompleted = true; // core has already confirmed the real result
            await Assert.ThrowsAnyAsync<IOException>(() => root.TradeFinished(null!, new PK9 { Species = 25 }));
            await coordinator.NotifyAbortedAsync((member, message) => member.SendNotification(null!, message),
                (member, result) => member.TradeCanceled(null!, result), PokeTradeResult.ExceptionInternal);
            Assert.Equal(new[] { "completed", "stopped" }, orders.Snapshot().Orders.Select(o => o.Status));
            Assert.Contains("勿重复", orders.Snapshot().Orders[0].Message);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
