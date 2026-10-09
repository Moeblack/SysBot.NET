using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PKHeX.Core;
using SysBot.Pokemon;
using SysBot.Base;
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
        Assert.Equal(1, fixture.Queue.Count);
        Assert.Equal("03180318", fixture.Orders.Snapshot().Settings.TradeCode);
        Assert.Equal(new[] { 1, 2, 3 }, batch.Orders.Select(o => o.BatchIndex));
        Assert.All(batch.Orders, o => Assert.Equal(3, o.BatchSize));
        var root = fixture.Dequeue();
        var members = Members(root);
        Assert.Same(root, members[0]);
        Assert.Equal(new ushort[] { 1, 2, 3 }, members.Select(m => m.TradeData.Species));
        Assert.True(root.IsReady);
        Assert.All(members, m =>
        {
            Assert.True(m.IsRetry);
            Assert.Equal("03180318", m.Code.ToString("D8"));
        });
        Assert.Equal(0, fixture.Queue.Count);
        Assert.All(fixture.Orders.Snapshot().Orders, o => Assert.Equal("queued", o.Status));
    }

    [Fact]
    public async Task DifferentBatchesRemainFifoWithoutInterleavingMembers()
    {
        await using var fixture = new Fixture();
        var first = await fixture.Orders.SubmitAsync(Request());
        var second = await fixture.Orders.SubmitAsync(Request());
        Assert.NotEqual(first.Orders[0].BatchId, second.Orders[0].BatchId);
        Assert.Equal(2, fixture.Queue.Count);
        var firstRoot = fixture.Dequeue();
        await firstRoot.TradeInitialize(null!);
        Assert.Equal(new[] { "preparing", "waiting", "waiting", "queued", "queued", "queued" }, fixture.Orders.Snapshot().Orders.Select(o => o.Status));
        var secondRoot = fixture.Dequeue();
        Assert.NotSame(firstRoot, secondRoot);
        Assert.Equal(3, Members(firstRoot).Count);
        Assert.Equal(3, Members(secondRoot).Count);
        await secondRoot.TradeInitialize(null!);
        Assert.Equal("preparing", fixture.Status(3));
        Assert.Equal("waiting", fixture.Status(1));
        Assert.Equal(0, fixture.Queue.Count);
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
    public async Task FailedGenerationDoesNotPollutePreviouslyQueuedBatch()
    {
        var generator = new Generator(text => text == "valid"
            ? Task.FromResult<IReadOnlyList<GeneratedPokemon>>(Pokemon(3))
            : Task.FromException<IReadOnlyList<GeneratedPokemon>>(new OrderException("later member failed")));
        await using var fixture = new Fixture(generator);
        var existing = await fixture.Orders.SubmitAsync(Request("valid"));
        await Assert.ThrowsAsync<OrderException>(() => fixture.Orders.SubmitAsync(Request()));
        Assert.Equal(existing.Orders, fixture.Orders.Snapshot().Orders);
        Assert.Equal(1, fixture.Queue.Count);
        Assert.Equal(3, Members(fixture.Dequeue()).Count);
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
        Assert.Equal(1, fixture.Queue.Count);
        Assert.Equal(3, fixture.Orders.Snapshot().Orders.Length);
        Assert.Equal(3, Members(fixture.Dequeue()).Count);
        await Assert.ThrowsAsync<OrderException>(() => fixture.Orders.SubmitAsync(request with { Text = "changed" }));
        Assert.Equal(1, generator.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task QueuedCancellationRemovesRealEntryAndIsIdempotent(int index)
    {
        await using var fixture = new Fixture();
        var batch = await fixture.Orders.SubmitAsync(Request());
        var later = await fixture.Orders.SubmitAsync(Request());
        fixture.Orders.Cancel(batch.Orders[index].Id);
        fixture.Orders.Cancel(batch.Orders[index].Id);
        foreach (var order in batch.Orders) fixture.Orders.Cancel(order.Id);
        Assert.All(fixture.Orders.Snapshot().Orders.Take(3), o => Assert.Equal("cancelled", o.Status));
        Assert.All(fixture.Orders.Snapshot().Orders.Skip(3), o => Assert.Equal("queued", o.Status));
        Assert.Equal(1, fixture.Queue.Count);
        await fixture.Dequeue().TradeInitialize(null!);
        Assert.Equal("preparing", fixture.Orders.Snapshot().Orders.Single(o => o.Id == later.Orders[0].Id).Status);
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
        foreach (var order in batch.Orders)
            Assert.Throws<OrderException>(() => fixture.Orders.Cancel(order.Id));
        Assert.DoesNotContain(fixture.Orders.Snapshot().Orders, o => o.Status == "cancelled");
        Assert.Equal(0, fixture.Queue.Count);
    }

    [Fact]
    public async Task RealNotifierRequiresExplicitCompletionAndPreservesTerminalStates()
    {
        await using var fixture = new Fixture();
        await fixture.Orders.SubmitAsync(Request());
        await fixture.Orders.SubmitAsync(Request());
        var detail = fixture.Dequeue();
        var members = Members(detail);
        await detail.TradeInitialize(null!);
        Assert.Equal("preparing", fixture.Status(0));
        Assert.Equal("waiting", fixture.Status(1));
        Assert.Equal("waiting", fixture.Status(2));
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
        var failed = members[1];
        await failed.TradeInitialize(null!);
        await failed.TradeSearching(null!);
        await failed.TradeCanceled(null!, PokeTradeResult.NoTrainerFound);
        Assert.Equal("failed", fixture.Status(1));
        await failed.TradeFinished(null!, new PK9());
        Assert.Equal("failed", fixture.Status(1));
        Assert.Equal("completed", fixture.Status(0));
        Assert.Equal("stopped", fixture.Status(2));
        Assert.Contains("勿重复", fixture.Orders.Snapshot().Orders[0].Message);
        await members[2].TradeInitialize(null!);
        await members[2].TradeFinished(null!, new PK9());
        Assert.Equal("stopped", fixture.Status(2));
        Assert.DoesNotContain(fixture.Orders.Snapshot().Orders, o => o.Message.Contains("全部完成"));
        Assert.All(fixture.Orders.Snapshot().Orders.Skip(3), o => Assert.Equal("queued", o.Status));
        Assert.Equal(1, fixture.Queue.Count);
    }

    [Fact]
    public async Task OnlyAllMembersCompletedAllowsExit()
    {
        await using var fixture = new Fixture();
        await fixture.Orders.SubmitAsync(Request());
        var members = Members(fixture.Dequeue());
        for (var i = 0; i < members.Count; i++)
        {
            await members[i].TradeInitialize(null!);
            await members[i].TradeFinished(null!, new PK9());
            Assert.Equal("completed", fixture.Status(i));
            if (i < members.Count - 1)
            {
                Assert.Contains("继续下一只", fixture.Orders.Snapshot().Orders[i].Message);
                Assert.DoesNotContain(fixture.Orders.Snapshot().Orders, o => o.Message.Contains("可以退出"));
            }
        }
        Assert.All(fixture.Orders.Snapshot().Orders, o =>
        {
            Assert.Equal("completed", o.Status);
            Assert.Contains("全部完成", o.Message);
            Assert.Contains("可以退出", o.Message);
        });
        Assert.Equal(0, fixture.Queue.Count);
    }

    [Fact]
    public async Task DeviceFailureStopsCurrentBatchWithoutOrphans()
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
        Assert.Equal("stopped", fixture.Status(1));
        Assert.Equal("stopped", fixture.Status(2));
        Assert.Equal(0, fixture.Queue.Count);
        Assert.DoesNotContain(fixture.Orders.Snapshot().Orders, o => o.Status == "completed");
    }

    [Theory]
    [InlineData(0)] // Root dequeued, before its first notifier.
    [InlineData(1)] // Root completed, next member active.
    [InlineData(2)] // Root completed, between members.
    public async Task StoppingDevicePreservesCompletedAndStopsRemainingMembers(int phase)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = new Device(async (hub, report, token) =>
        {
            Assert.True(hub.Queues.GetQueue(PokeRoutineType.LinkTrade).TryDequeue(out var active, out _, true));
            active.IsProcessing = true;
            if (phase > 0)
            {
                await active.TradeInitialize(null!);
                await active.TradeFinished(null!, new PK9());
                if (phase == 1)
                {
                    var next = Members(active)[1];
                    next.IsProcessing = true;
                    await next.TradeInitialize(null!);
                }
            }
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        await using var fixture = new Fixture(device: device);
        await fixture.Orders.SubmitAsync(Request());
        await fixture.Orders.SubmitAsync(Request());
        fixture.Orders.Connect(1234);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.DisposeAsync();
        Assert.Equal(phase == 0 ? "failed" : "completed", fixture.Status(0));
        Assert.Equal(phase == 1 ? "failed" : "stopped", fixture.Status(1));
        Assert.Equal("stopped", fixture.Status(2));
        if (phase > 0)
        {
            Assert.Contains("勿重复", fixture.Orders.Snapshot().Orders[0].Message);
            Assert.DoesNotContain("继续下一只", fixture.Orders.Snapshot().Orders[0].Message);
        }
        Assert.All(fixture.Orders.Snapshot().Orders.Skip(3), o => Assert.Equal("queued", o.Status));
        Assert.DoesNotContain(fixture.Orders.Snapshot().Orders, o => o.Message.Contains("全部完成") || o.Message.Contains("可以退出"));
        Assert.Equal(1, fixture.Queue.Count);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(12, true)]
    [InlineData(13, false)]
    public async Task BatchSizeLimit(int count, bool accepted)
    {
        await using var fixture = new Fixture(new Generator(_ => Task.FromResult<IReadOnlyList<GeneratedPokemon>>(Pokemon(count))));
        if (accepted) Assert.Equal(count, (await fixture.Orders.SubmitAsync(Request())).Count);
        else await Assert.ThrowsAsync<OrderException>(() => fixture.Orders.SubmitAsync(Request()));
        Assert.Equal(accepted ? 1 : 0, fixture.Queue.Count);
        if (accepted)
        {
            var root = fixture.Dequeue();
            Assert.True(root.IsReady);
            Assert.True(root.IsRetry);
            if (count == 1) Assert.Null(root.BatchTrades);
            else Assert.Equal(count, Members(root).Count);
        }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UsbFaultStopsWholeRunAndNewSubmissionDoesNotReconnectOrDequeueNext(bool rootCompleted)
    {
        int runs = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = new Device(async (hub, report, token) =>
        {
            if (Interlocked.Increment(ref runs) != 1)
            {
                report(new DeviceView("ready", "manually reconnected fake device", 1234));
                resumed.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return;
            }
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var monitor = new UsbTradeRunMonitor(1234, () => "renamed-trainer", report, stop);
            var guard = new UsbTransportGuard { Enabled = true };
            guard.Faulted += monitor.OnFault;
            var queue = hub.Queues.GetQueue(PokeRoutineType.LinkTrade);
            Assert.True(queue.TryDequeue(out var active, out _, true));
            active.IsProcessing = true;
            await active.TradeInitialize(null!);
            if (rootCompleted)
            {
                await active.TradeFinished(null!, new PK9());
                active = Members(active)[1];
                active.IsProcessing = true;
                await active.TradeInitialize(null!);
            }
            monitor.OnLog("Starting main PokeTradeBotSV loop.", "renamed-trainer");
            started.SetResult();
            await release.Task.WaitAsync(token);
            try { guard.RecordFailure(new IOException("Pipe error")); }
            catch (IOException) { await active.TradeCanceled(null!, PokeTradeResult.ExceptionInternal); }
            Assert.True(stop.IsCancellationRequested); // Generic bot catch cannot continue its while loop.
            Assert.Equal(1, queue.Count);
            Assert.Throws<IOException>(() => guard.ThrowIfFaulted()); // HardStop(None) is blocked too.
            finished.SetResult();
        });
        await using var fixture = new Fixture(device: device);
        await fixture.Orders.SubmitAsync(Request());
        await fixture.Orders.SubmitAsync(Request());
        fixture.Orders.Connect(1234);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("ready", fixture.Orders.Snapshot().Device.Status);
        release.SetResult();
        var failedIndex = rootCompleted ? 1 : 0;
        await Eventually(() => fixture.Status(failedIndex) == "failed" && fixture.Orders.Snapshot().Device.Status == "error");
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("USB", fixture.Orders.Snapshot().Orders[failedIndex].Message);
        Assert.Contains("未确认是否收货", fixture.Orders.Snapshot().Orders[failedIndex].Message);
        Assert.Equal(rootCompleted ? "completed" : "failed", fixture.Status(0));
        Assert.Equal("stopped", fixture.Status(2));
        if (rootCompleted) Assert.Contains("勿重复", fixture.Orders.Snapshot().Orders[0].Message);
        else Assert.Equal("stopped", fixture.Status(1));
        Assert.All(fixture.Orders.Snapshot().Orders.Skip(3), o => Assert.Equal("queued", o.Status));
        Assert.DoesNotContain(fixture.Orders.Snapshot().Orders, o => o.Message.Contains("全部完成") || o.Message.Contains("可以退出"));
        Assert.Equal(1, fixture.Queue.Count);
        await fixture.Orders.SubmitAsync(Request());
        Assert.Equal(1, Volatile.Read(ref runs));
        Assert.Equal(2, fixture.Queue.Count);
        Assert.Equal("error", fixture.Orders.Snapshot().Device.Status);
        await Eventually(() => { fixture.Orders.Connect(1234); return resumed.Task.IsCompleted; });
        Assert.Equal(2, Volatile.Read(ref runs));
        Assert.Equal("ready", fixture.Orders.Snapshot().Device.Status);
        Assert.Equal(2, fixture.Queue.Count);
    }

    private static IReadOnlyList<PokeTradeDetail<PK9>> Members(PokeTradeDetail<PK9> root) =>
        Assert.IsAssignableFrom<IReadOnlyList<PokeTradeDetail<PK9>>>(root.BatchTrades);

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
