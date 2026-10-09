namespace SysBot.Pokemon.Web;

/// <summary>One bot run; label can change after trainer identification. No automatic replay after a USB fault.</summary>
public sealed class UsbTradeRunMonitor(int port, Func<string> currentLabel, Action<DeviceView> report, CancellationTokenSource stop)
{
    private readonly object sync = new();
    private bool failed;

    public void OnLog(string message, string identity)
    {
        lock (sync)
        {
            if (failed || identity != currentLabel()) return;
            if (message.StartsWith("SV batch recovered;", StringComparison.Ordinal))
            {
                report(new("ready", "本批已中断并退出交换，派送机继续待命；请核对收货，只重提未收到的部分。", port));
                return;
            }
            if (message.StartsWith("SV batch stopped;", StringComparison.Ordinal))
            {
                failed = true;
                stop.Cancel();
                report(new("error", "本批因交换状态检查中断，已停止确认。请查看订单和日志，核对收货后再重连；这不一定是 USB 故障。", port));
                return;
            }
            if (message.Contains("Starting main PokeTradeBotSV loop", StringComparison.Ordinal))
                report(new("ready", "派送机已就绪 · USB 本地交换", port));
        }
    }

    public void OnFault(Exception error)
    {
        lock (sync)
        {
            if (failed) return;
            failed = true;
            // The transport guard has already latched. Even non-cancellable cleanup cannot send more commands.
            stop.Cancel();
            report(new("error", "USB 通信中断，已停止派送。请先核对是否收货，再重连；未开始的订单保留。", port));
        }
    }
}
