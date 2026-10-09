using PKHeX.Core;

namespace SysBot.Pokemon.Web;

public sealed record OrderRequest(string Text, string RequestId);
public sealed record ConnectRequest(int Port);
public sealed record GeneratedPokemon(string Name, string Species, PK9 Pokemon);
public sealed record OrderView(string Id, string BatchId, string Name, string Species, string Status, string Message, DateTimeOffset CreatedAt, int BatchIndex = 1, int BatchSize = 1);
public sealed record BatchView(OrderView[] Orders, int Count);
public sealed record DeviceView(string Status, string Message, int? Port);
public sealed record WebSettings(int? UsbPort = null, string TradeCode = "03180318");
public sealed record StateView(DeviceView Device, WebSettings Settings, OrderView[] Orders, int Pending);
public sealed record UsbView(int[] Ports, string Message);
public sealed class OrderException(string message, string[]? details = null) : Exception(message)
{
    public string[] Details { get; } = details ?? [];
}
public interface IPokemonGenerator
{
    Task<IReadOnlyList<GeneratedPokemon>> GenerateAsync(string text);
}
public interface IWebTradeDevice
{
    Task RunAsync(PokeTradeHub<PK9> hub, int port, Action<DeviceView> report, CancellationToken token);
}

/// <summary>Single user, single console, FIFO queue. Reuses the actual SysBot trade queue and notifier.</summary>
public sealed class WebOrders : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly IPokemonGenerator generator;
    private readonly IWebTradeDevice device;
    private readonly Func<UsbView> scanUsb;
    private readonly string settingsFile;
    private readonly string receiptsDirectory;
    private readonly Dictionary<string, (string Text, Task<BatchView> Task)> submissions = [];
    private readonly List<OrderView> orders = [];
    private readonly Dictionary<string, PokeTradeDetail<PK9>> details = [];
    private readonly Dictionary<string, PokeTradeDetail<PK9>> batchRoots = [];
    private readonly CancellationTokenSource shutdown = new();
    private Task? deviceTask;
    private bool requiresExplicitReconnect;
    private DeviceView deviceState = new("disconnected", "派送机未连接；可以先排好队，再接上 USB。", null);
    private WebSettings settings;
    public PokeTradeHub<PK9> Hub { get; }

    public WebOrders(IPokemonGenerator generator, IWebTradeDevice device, string dataDirectory, Func<UsbView>? scanUsb = null)
    {
        this.generator = generator;
        this.device = device;
        this.scanUsb = scanUsb ?? UsbDevices.Scan;
        Directory.CreateDirectory(dataDirectory);
        settingsFile = Path.Combine(dataDirectory, "web-settings.json");
        receiptsDirectory = Path.Combine(dataDirectory, "received");
        try { settings = System.Text.Json.JsonSerializer.Deserialize<WebSettings>(File.ReadAllText(settingsFile)) ?? new(); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { settings = new(); }
        if (!int.TryParse(settings.TradeCode, out var code) || code is < 0 or > 99999999 || settings.TradeCode.Length != 8)
            settings = settings with { TradeCode = "03180318" };
        Hub = new PokeTradeHub<PK9>(new PokeTradeHubConfig
        {
            Trade = { PerformLocalTradeSV = true, TradeWaitTime = 90, MaxTradeConfirmTime = 60 },
            Distribution = { DistributeWhileIdle = false },
            Legality = PokemonGenerator.CreateSettings(),
        });
    }

    public StateView Snapshot()
    {
        lock (sync)
            return new(deviceState, settings, orders.ToArray(), orders.Count(o => o.Status is "queued" or "waiting" or "preparing" or "searching" or "trading"));
    }

    public Task<BatchView> SubmitAsync(OrderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 65536)
            throw new OrderException("请粘贴一只或一队的 Showdown 配置（不超过 64 KB）。");
        if (!Guid.TryParse(request.RequestId, out _))
            throw new OrderException("这次下单标识已失效，请刷新页面后重试；已排队的订单不会被重复提交。");
        var text = request.Text.Trim();
        lock (sync)
        {
            if (submissions.TryGetValue(request.RequestId, out var old))
            {
                if (old.Text != text) throw new OrderException("配置已经改变，请重新提交这次订单。");
                return old.Task;
            }
            if (submissions.Count >= 256) throw new OrderException("本次运行的订单记录已满，派完当前队列后重启网页程序即可继续。");
            var task = GenerateAndEnqueueAsync(text, request.RequestId);
            submissions.Add(request.RequestId, (text, task));
            return task;
        }
    }

    private async Task<BatchView> GenerateAndEnqueueAsync(string text, string batchId)
    {
        // All members must be generated and checked before any are allowed into the live queue.
        var generated = await generator.GenerateAsync(text).ConfigureAwait(false);
        if (generated.Count is < 1 or > 12) throw new OrderException("一次可以排 1～12 只宝可梦。");
        var added = new List<OrderView>();
        int? autoPort;
        lock (sync)
        {
            if (orders.Count(o => o.Status is "queued" or "waiting" or "preparing" or "searching" or "trading") + generated.Count > 60)
                throw new OrderException("待派送已经很多了，先收几只再继续添加（最多待派 60 只）。");
            foreach (var item in generated)
            {
                var id = Guid.NewGuid().ToString("N");
                int index = added.Count + 1;
                var view = new OrderView(id, batchId, item.Name, item.Species, "queued",
                    $"已加入本批第 {index}/{generated.Count} 只；同一批只配对一次。", DateTimeOffset.Now, index, generated.Count);
                var trade = new PokeTradeDetail<PK9>
                {
                    Code = int.Parse(settings.TradeCode),
                    TradeData = item.Pokemon,
                    Trainer = new PokeTradeTrainerInfo("Local Web"),
                    Notifier = new WebTradeNotifier(this, id),
                    Type = PokeTradeType.Specific,
                    // Avoid ambiguous automatic retries and priority reordering of a batch.
                    IsRetry = true,
                    IsReady = false,
                };
                orders.Add(view);
                details.Add(id, trade);
                added.Add(view);
            }
            // One real queue entry owns the whole session; members are never dequeued independently.
            var members = added.Select(item => details[item.Id]).ToArray();
            var root = members[0];
            if (members.Length > 1) root.BatchTrades = Array.AsReadOnly(members);
            batchRoots.Add(batchId, root);
            Hub.Queues.Enqueue(PokeRoutineType.LinkTrade, root, PokeTradePriorities.TierFree);
            root.IsReady = true;
            autoPort = settings.UsbPort;
        }
        if (autoPort is null)
        {
            var ports = scanUsb().Ports;
            if (ports.Length == 1) autoPort = ports[0];
        }
        if (autoPort is not null)
        {
            try
            {
                lock (sync)
                    if (!requiresExplicitReconnect) Connect(autoPort.Value);
            }
            catch (OrderException) { /* Queue survives a unavailable device; state carries the recovery action. */ }
        }
        return new BatchView(added.ToArray(), added.Count);
    }

    public StateView Connect(int port)
    {
        if (port is < 1 or > 65534) throw new OrderException("请填写有效的 USB 端口号，或先点「识别 USB」。");
        lock (sync)
        {
            if (deviceTask is { IsCompleted: false })
            {
                if (deviceState.Port != port) throw new OrderException("当前派送机还在运行；更换 USB 端口前，请关闭并重新启动网页程序。");
                return Snapshot();
            }
            requiresExplicitReconnect = false;
            settings = settings with { UsbPort = port };
            File.WriteAllText(settingsFile, System.Text.Json.JsonSerializer.Serialize(settings));
            deviceState = new("connecting", "正在连接 USB 派送机，请让游戏停在大地图。", port);
            deviceTask = Task.Run(async () =>
            {
                try
                {
                    await device.RunAsync(Hub, port, state =>
                    {
                        lock (sync)
                        {
                            deviceState = state;
                            if (state.Status != "error") return;
                            requiresExplicitReconnect = true;
                            // Freeze uncertainty before the bot's generic abort notifier can overwrite it.
                            foreach (var item in orders.Where(o => o.Status is "preparing" or "searching" or "trading" || (o.Status == "queued" && details[o.Id].IsProcessing)).ToArray())
                                Fail(item.Id, "USB 通信中断，未确认是否收货；请先检查游戏盒子，避免重复派送。");
                        }
                    }, shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
                catch (Exception e) { Console.Error.WriteLine(e); }
                finally
                {
                    lock (sync)
                    {
                        if (!shutdown.IsCancellationRequested) requiresExplicitReconnect = true;
                        if (!shutdown.IsCancellationRequested && deviceState.Status != "error")
                            deviceState = new("error", "USB 连接已停止。检查线缆、端口、usb-botbase 与朱紫 4.0.0，再点「连接派送机」。", port);
                        foreach (var item in orders.Where(o => o.Status is "preparing" or "searching" or "trading" || (o.Status == "queued" && details[o.Id].IsProcessing)).ToArray())
                            Fail(item.Id, "连接中断，未确认是否收货；请先检查游戏盒子，避免重复派送。");
                        foreach (var item in orders.Where(o => o.Status == "waiting").ToArray())
                            StopNotStarted(item.Id, "本批已停止，这只尚未完成派送；请核对已收货部分，不要整批重复提交。");
                    }
                }
            });
            return Snapshot();
        }
    }

    public StateView Cancel(string id)
    {
        lock (sync)
        {
            var item = orders.Find(o => o.Id == id) ?? throw new OrderException("没有找到这笔订单，请刷新队列。");
            if (item.Status == "cancelled") return Snapshot();
            var root = batchRoots[item.BatchId];
            var members = orders.Where(o => o.BatchId == item.BatchId).ToArray();
            if (root.IsProcessing || members.Any(o => o.Status != "queued") ||
                Hub.Queues.GetQueue(PokeRoutineType.LinkTrade).Remove(root) != 1)
                throw new OrderException("本批已经开始，不能拆开取消；请在游戏内退出交换以停止余下部分。");
            foreach (var member in members)
                Update(member.Id, "cancelled", $"本批 {members.Length} 只已全部从等待队列移除。");
            return Snapshot();
        }
    }

    internal void Update(string id, string status, string message)
    {
        lock (sync)
        {
            var i = orders.FindIndex(o => o.Id == id);
            if (i < 0 || orders[i].Status is "completed" or "cancelled" or "failed" or "stopped") return;
            orders[i] = orders[i] with { Status = status, Message = message };
        }
    }

    internal void Begin(string id)
    {
        lock (sync)
        {
            var current = orders.Find(o => o.Id == id)!;
            foreach (var item in orders.Where(o => o.BatchId == current.BatchId && o.Id != id && o.Status == "queued").ToArray())
                Update(item.Id, "waiting", "本批已开始；轮到这只时直接在当前交换会话继续，不必重新搜索。");
            Update(id, "preparing", $"正在准备本批第 {current.BatchIndex}/{current.BatchSize} 只。");
        }
    }

    internal void SaveReceived(string id, PK9 received)
    {
        // The next member replaces B1S1. Preserve the actual received individual locally first.
        Directory.CreateDirectory(receiptsDirectory);
        var path = Path.Combine(receiptsDirectory, id + ".pk9");
        var data = new byte[received.SIZE_STORED];
        received.WriteEncryptedDataStored(data);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(data);
        stream.Flush(true);
    }

    internal bool Complete(string id)
    {
        lock (sync)
        {
            var current = orders.Find(o => o.Id == id)!;
            if (current.Status is "completed" or "cancelled" or "failed" or "stopped") return false;
            Update(id, "completed", $"第 {current.BatchIndex}/{current.BatchSize} 只已完成；请留在交换界面继续下一只。");
            var batch = orders.Where(o => o.BatchId == current.BatchId).ToArray();
            if (batch.All(o => o.Status == "completed"))
                foreach (var item in batch)
                {
                    var index = orders.FindIndex(o => o.Id == item.Id);
                    orders[index] = orders[index] with { Message = $"本批 {batch.Length} 只全部完成，可以退出交换。" };
                }
            return true;
        }
    }

    internal void StopNotStarted(string id, string message)
    {
        lock (sync)
        {
            var current = orders.Find(o => o.Id == id)!;
            if (current.Status is "completed" or "cancelled") return;
            Update(id, "stopped", message);
            foreach (var item in orders.Where(o => o.BatchId == current.BatchId && o.Status == "completed").ToArray())
            {
                var index = orders.FindIndex(o => o.Id == item.Id);
                orders[index] = orders[index] with { Message = "这只已完成，但本批后续中断；请勿重复提交已收到的宝可梦。" };
            }
        }
    }

    internal void Fail(string id, string message)
    {
        lock (sync)
        {
            var current = orders.Find(o => o.Id == id)!;
            if (current.Status is "completed" or "cancelled" or "failed" or "stopped") return;
            Update(id, "failed", message);
            foreach (var item in orders.Where(o => o.BatchId == current.BatchId && o.Status is "queued" or "waiting").ToArray())
                Update(item.Id, "stopped", "本批已中断，这只尚未开始交换；请核对收货后只重提未收到的部分。");
            foreach (var item in orders.Where(o => o.BatchId == current.BatchId && o.Status == "completed").ToArray())
            {
                var index = orders.FindIndex(o => o.Id == item.Id);
                orders[index] = orders[index] with { Message = "这只已完成，但本批后续中断；请勿重复提交已收到的宝可梦。" };
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        var running = deviceTask;
        if (running is not null)
        {
            try { await running.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false); }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException) { }
        }
        shutdown.Dispose();
    }
}

internal sealed class WebTradeNotifier(WebOrders owner, string id) : IPokeTradeNotifier<PK9>
{
    public Action<PokeRoutineExecutor<PK9>>? OnFinish { private get; set; }
    public Task TradeInitialize(PokeRoutineExecutor<PK9> routine, PokeTradeDetail<PK9> info)
    {
        owner.Begin(id);
        return Task.CompletedTask;
    }
    public Task TradeSearching(PokeRoutineExecutor<PK9> routine, PokeTradeDetail<PK9> info)
    {
        owner.Update(id, "searching", "正在找你：在接收机选择本地连接交换，输入页面上的密码。");
        return Task.CompletedTask;
    }
    public Task TradeCanceled(PokeRoutineExecutor<PK9> routine, PokeTradeDetail<PK9> info, PokeTradeResult result)
    {
        var message = result switch
        {
            PokeTradeResult.NoTrainerFound => "没有找到接收机。确认两台游戏都离线，用相同密码重新下单。",
            PokeTradeResult.TrainerTooSlow => "等待确认超时；请先看游戏是否已收货，再决定是否重新下单。",
            PokeTradeResult.TrainerLeft => "接收方已退出交换；需要时重新下单。",
            PokeTradeResult.SuspiciousActivity => "交换前的一致性检查未通过，程序已停止确认；请保留游戏画面与日志，不要反复下单。",
            PokeTradeResult.TradeEvolveNotAllowed => "交换材料会进化，请换一只不会交换进化的宝可梦。",
            _ => "本次派送没有确认完成。请先检查游戏盒子，确认收货情况后再决定是否重试。",
        };
        owner.Fail(id, message);
        OnFinish?.Invoke(routine);
        return Task.CompletedTask;
    }
    public Task TradeFinished(PokeRoutineExecutor<PK9> routine, PokeTradeDetail<PK9> info, PK9 result)
    {
        // If archiving fails, the real success remains visible, but the batch must not overwrite this slot.
        if (owner.Complete(id)) owner.SaveReceived(id, result);
        OnFinish?.Invoke(routine);
        return Task.CompletedTask;
    }
    public Task SendNotification(PokeRoutineExecutor<PK9> routine, PokeTradeDetail<PK9> info, string message)
    {
        if (message.StartsWith("BatchNotStarted:", StringComparison.Ordinal))
            owner.StopNotStarted(id, "本批已中断，这只尚未开始交换；请只重提未收到的部分。");
        else if (message.StartsWith("BatchWaitingOffer:", StringComparison.Ordinal))
            owner.Update(id, "preparing", "同一连接内准备下一只：请先选择下一只交换材料，等对方显示目标宝可梦后再确认。");
        else if (message.Contains("Waiting for a Pokémon", StringComparison.OrdinalIgnoreCase))
            owner.Update(id, "trading", "请在当前交换会话选择下一只材料并确认；本批结束前不要退出。");
        return Task.CompletedTask;
    }
    public Task SendNotification(PokeRoutineExecutor<PK9> routine, PokeTradeDetail<PK9> info, PokeTradeSummary summary) => Task.CompletedTask;
    public Task SendNotification(PokeRoutineExecutor<PK9> routine, PokeTradeDetail<PK9> info, PK9 result, string message) => Task.CompletedTask;
}
