using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PKHeX.Core;
using SysBot.Pokemon;
using SysBot.Pokemon.Web;
using Xunit;

namespace SysBot.Web.Tests;

public sealed class WebOrdersTests
{
    // These deliberately incomplete PK9 instances test queue mechanics, not legality.
    private static GeneratedPokemon[] Pokemon(int count) => Enumerable.Range(1, count)
        .Select(i => new GeneratedPokemon($"member-{i}", $"species-{i}", new PK9 { Species = (ushort)i })).ToArray();
    private static OrderRequest Request(string text = "first\n\nsecond\n\nthird") => new(text, Guid.NewGuid().ToString());

    [Fact]
    public async Task BatchEntersRealHubFifoReadyWithUnifiedCode()
    {
        await using var fixture = new Fixture(new Generator(text => Task.FromResult<IReadOnlyList<GeneratedPokemon>>(Pokemon(text.Split("\n\n").Length))));
        var batch = await fixture.Orders.SubmitAsync(Request());
        Assert.Equal(new[] { "member-1", "member-2", "member-3" }, batch.Orders.Select(o => o.Name));
        Assert.Equal(3, fixture.Queue.Count);
        Assert.Equal("03180318", fixture.Orders.Snapshot().Settings.TradeCode);
        foreach (var species in new ushort[] { 1, 2, 3 })
        {
            var detail = fixture.Dequeue();
            Assert.Equal(species, detail.TradeData.Species);
            Assert.True(detail.IsReady);
            Assert.Equal("03180318", detail.Code.ToString("D8"));
        }
        Assert.Equal(0, fixture.Queue.Count);
        Assert.All(fixture.Orders.Snapshot().Orders, o => Assert.Equal("queued", o.Status));
    }

    [Theory]
    [InlineData("parse")]
    [InlineData("generate")]
    public async Task AnyMemberFailureEnqueuesNothing(string stage)
    {
        await using var fixture = new Fixture(new Generator(async text =>
        {
            _ = Pokemon(2); // A preceding member can have been produced before a later failure.
            await Task.Yield();
            throw new OrderException($"third member {stage} failed");
        }));
        await Assert.ThrowsAsync<OrderException>(() => fixture.Orders.SubmitAsync(Request()));
        Assert.Empty(fixture.Orders.Snapshot().Orders);
        Assert.Equal(0, fixture.Queue.Count);
    }

    [Fact]
    public async Task ConcurrentSameRequestReusesGenerationAndNeverDuplicates()
    {
        var gate = new TaskCompletionSource<IReadOnlyList<GeneratedPokemon>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new Generator(_ => gate.Task);
        await using var fixture = new Fixture(generator);
        var request = Request();
        var calls = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() => new Pending(fixture.Orders.SubmitAsync(request)))));
        gate.SetResult(Pokemon(3));
        var batches = await Task.WhenAll(calls.Select(t => t.Task));
        Assert.Equal(1, generator.Calls);
        Assert.All(batches, b => Assert.Same(batches[0], b));
        Assert.Equal(3, fixture.Queue.Count);
        Assert.Equal(3, fixture.Orders.Snapshot().Orders.Length);
        await Assert.ThrowsAsync<OrderException>(() => fixture.Orders.SubmitAsync(request with { Text = "changed" }));
        Assert.Equal(1, generator.Calls);
    }

    [Fact]
    public async Task QueuedCancellationRemovesRealEntryAndIsIdempotent()
    {
        await using var fixture = new Fixture();
        var batch = await fixture.Orders.SubmitAsync(Request());
        fixture.Orders.Cancel(batch.Orders[1].Id);
        fixture.Orders.Cancel(batch.Orders[1].Id);
        Assert.Equal("cancelled", fixture.Orders.Snapshot().Orders[1].Status);
        Assert.Equal(2, fixture.Queue.Count);
        Assert.Equal((ushort)1, fixture.Dequeue().TradeData.Species);
        Assert.Equal((ushort)3, fixture.Dequeue().TradeData.Species);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DequeuedOrProcessingCannotBeCancelled(bool processing)
    {
        await using var fixture = new Fixture();
        var batch = await fixture.Orders.SubmitAsync(Request());
        var detail = fixture.Dequeue();
        detail.IsProcessing = processing;
        if (processing) await detail.TradeInitialize(null!);
        Assert.Throws<OrderException>(() => fixture.Orders.Cancel(batch.Orders[0].Id));
        Assert.NotEqual("cancelled", fixture.Orders.Snapshot().Orders[0].Status);
    }

    [Fact]
    public async Task RealNotifierRequiresExplicitCompletionAndPreservesTerminalStates()
    {
        await using var fixture = new Fixture();
        await fixture.Orders.SubmitAsync(Request());
        var detail = fixture.Dequeue();
        await detail.TradeInitialize(null!);
        Assert.Equal("preparing", fixture.Status(0));
        await detail.TradeSearching(null!);
        Assert.Equal("searching", fixture.Status(0));
        await detail.SendNotification(null!, "unrelated informational message");
        Assert.Equal("searching", fixture.Status(0));
        await detail.SendNotification(null!, "Waiting for a Pokémon from the partner");
        Assert.Equal("trading", fixture.Status(0));
        await detail.TradeFinished(null!, new PK9());
        Assert.Equal("completed", fixture.Status(0));
        await detail.TradeCanceled(null!, PokeTradeResult.NoTrainerFound);
        Assert.Equal("completed", fixture.Status(0));
        var failed = fixture.Dequeue();
        await failed.TradeSearching(null!);
        await failed.TradeCanceled(null!, PokeTradeResult.NoTrainerFound);
        Assert.Equal("failed", fixture.Status(1));
        await failed.TradeFinished(null!, new PK9());
        Assert.Equal("failed", fixture.Status(1));
    }

    [Fact]
    public async Task DeviceFailureKeepsWaitingEntriesAndFailsActiveOrder()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = new Device(async (hub, report, token) =>
        {
            var queue = hub.Queues.GetQueue(PokeRoutineType.LinkTrade);
            Assert.True(queue.TryDequeue(out var active, out _, true));
            active.IsProcessing = true;
            await active.TradeInitialize(null!);
            report(new DeviceView("connected", "fake device", 1234));
            started.SetResult();
            await release.Task.WaitAsync(token);
            throw new IOException("fake device failure");
        });
        await using var fixture = new Fixture(device: device);
        await fixture.Orders.SubmitAsync(Request());
        fixture.Orders.Connect(1234);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        release.SetResult();
        await Eventually(() => fixture.Orders.Snapshot().Device.Status == "error");
        Assert.Equal("failed", fixture.Status(0));
        Assert.Equal("queued", fixture.Status(1));
        Assert.Equal("queued", fixture.Status(2));
        Assert.Equal(2, fixture.Queue.Count);
        Assert.DoesNotContain(fixture.Orders.Snapshot().Orders, o => o.Status == "completed");
    }

    [Fact]
    public async Task StoppingDeviceFailsProcessingOrderWithoutInventingSuccess()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = new Device(async (hub, report, token) =>
        {
            Assert.True(hub.Queues.GetQueue(PokeRoutineType.LinkTrade).TryDequeue(out var active, out _, true));
            active.IsProcessing = true;
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        await using var fixture = new Fixture(device: device);
        await fixture.Orders.SubmitAsync(Request());
        fixture.Orders.Connect(1234);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.DisposeAsync();
        Assert.Equal("failed", fixture.Status(0));
        Assert.Equal("queued", fixture.Status(1));
        Assert.Equal(2, fixture.Queue.Count);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(12, true)]
    [InlineData(13, false)]
    public async Task BatchSizeLimit(int count, bool accepted)
    {
        await using var fixture = new Fixture(new Generator(_ => Task.FromResult<IReadOnlyList<GeneratedPokemon>>(Pokemon(count))));
        if (accepted) Assert.Equal(count, (await fixture.Orders.SubmitAsync(Request())).Count);
        else await Assert.ThrowsAsync<OrderException>(() => fixture.Orders.SubmitAsync(Request()));
        Assert.Equal(accepted ? count : 0, fixture.Queue.Count);
    }

    [Theory]
    [InlineData("", "valid")]
    [InlineData("   ", "valid")]
    [InlineData("text", "")]
    [InlineData("text", "not-a-guid")]
    public async Task InvalidInputIsRejectedBeforeGenerator(string text, string id)
    {
        var generator = new Generator(_ => Task.FromResult<IReadOnlyList<GeneratedPokemon>>(Pokemon(3)));
        await using var fixture = new Fixture(generator);
        await Assert.ThrowsAsync<OrderException>(() => fixture.Orders.SubmitAsync(new(text, id == "valid" ? Guid.NewGuid().ToString() : id)));
        Assert.Equal(0, generator.Calls);
        Assert.Equal(0, fixture.Queue.Count);
    }

    private static async Task Eventually(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Generator(Func<string, Task<IReadOnlyList<GeneratedPokemon>>> generate) : IPokemonGenerator
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public Task<IReadOnlyList<GeneratedPokemon>> GenerateAsync(string text)
        {
            Interlocked.Increment(ref calls);
            return generate(text);
        }
    }

    private sealed class Device(Func<PokeTradeHub<PK9>, Action<DeviceView>, CancellationToken, Task>? run = null) : IWebTradeDevice
    {
        public Task RunAsync(PokeTradeHub<PK9> hub, int port, Action<DeviceView> report, CancellationToken token) =>
            run?.Invoke(hub, report, token) ?? Task.Delay(Timeout.Infinite, token);
    }

    private sealed record Pending(Task<BatchView> Task);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "SysBot.Web.Tests", Guid.NewGuid().ToString("N"));
        private bool disposed;
        public WebOrders Orders { get; }
        public PokeTradeQueue<PK9> Queue => Orders.Hub.Queues.GetQueue(PokeRoutineType.LinkTrade);
        public Fixture(IPokemonGenerator? generator = null, IWebTradeDevice? device = null) =>
            Orders = new WebOrders(generator ?? new Generator(_ => Task.FromResult<IReadOnlyList<GeneratedPokemon>>(Pokemon(3))), device ?? new Device(), directory, () => new UsbView([], ""));
        public string Status(int index) => Orders.Snapshot().Orders[index].Status;
        public PokeTradeDetail<PK9> Dequeue()
        {
            Assert.True(Queue.TryDequeue(out var detail, out _, true));
            return detail;
        }
        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            await Orders.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
