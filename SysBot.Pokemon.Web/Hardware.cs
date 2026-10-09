using System.Runtime.InteropServices;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using PKHeX.Core;
using SysBot.Base;

namespace SysBot.Pokemon.Web;

public static class UsbDevices
{
    public static UsbView Scan()
    {
        if (!NativeUsb.Available)
            return new([], "缺少 USB 原生库，请重新解压完整运行包；你仍可以先排队。");
        try
        {
            using var context = new UsbContext();
            using var devices = context.FindAll(new UsbDeviceFinder { Vid = 0x057E, Pid = 0x3000 });
            var ports = devices.Where(d => d.LocationId.PortNumbers.Count > 0)
                .Select(d => (int)d.LocationId.PortNumbers[^1]).Distinct().Order().ToArray();
            return new(ports, ports.Length switch
            {
                0 => "未发现 USB 派送机。接好数据线，启动 usb-botbase 后再识别。",
                1 => "已识别派送机，下单后会自动连接。",
                _ => "发现多台设备，请选择本次派送机的 USB 端口。",
            });
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("USB enumeration: " + e.Message);
            return new([], "USB 识别尚不可用。请检查 usb-botbase 和驱动，或填写已知端口号。");
        }
    }
}

public sealed class UsbTradeDevice : IWebTradeDevice, ILogForwarder
{
    private Action<DeviceView>? report;
    private int port;

    public UsbTradeDevice()
    {
        // Registered once before any hardware task; no mutation of this list during logging.
        LogUtil.Forwarders.Add(this);
    }

    public async Task RunAsync(PokeTradeHub<PK9> hub, int usbPort, Action<DeviceView> update, CancellationToken token)
    {
        if (!NativeUsb.Available)
            throw new OrderException("缺少 USB 原生库，请重新解压完整运行包。");
        port = usbPort;
        report = update;
        var state = new PokeBotState { Connection = new SwitchConnectionConfig { Protocol = SwitchProtocol.USB, Port = usbPort } };
        state.Initialize(PokeRoutineType.LinkTrade);
        state.Initialize();
        var bot = new PokeTradeBotSV(hub, state);
        try
        {
            // No PokeBotRunner integrations or idle distribution: same upstream queue and SV routine only.
            await bot.RunAsync(token).ConfigureAwait(false);
        }
        finally
        {
            // Upstream RunAsync doesn't disconnect if InitialStartup throws.
            try { bot.Connection.Disconnect(); }
            catch (Exception e) { Console.Error.WriteLine(e.Message); }
            report = null;
        }
    }

    public void Forward(string message, string identity)
    {
        Console.WriteLine($"[{identity}] {message}");
        if (identity != $"USB-{port}") return;
        if (message.Contains("Starting main PokeTradeBotSV loop", StringComparison.Ordinal))
            report?.Invoke(new("ready", "派送机已就绪 · USB 本地交换", port));
    }
}

internal static class NativeUsb
{
    // LibUsbDotNet's partially constructed UsbContext can throw on its finalizer thread
    // if the native library is missing. Check before constructing any context/USB bot.
    private static readonly Lazy<bool> Loaded = new(() =>
        NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, "libusb-1.0.dll"), out _));
    public static bool Available => Loaded.Value;
}
