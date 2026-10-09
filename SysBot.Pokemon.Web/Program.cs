using System.Diagnostics;
using SysBot.Pokemon.Web;

var noBrowser = args.Contains("--no-browser");
var portText = Environment.GetEnvironmentVariable("SYSBOT_WEB_PORT") ?? "5217";
if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
    throw new ArgumentException("SYSBOT_WEB_PORT must be 1-65535.");
var url = $"http://127.0.0.1:{port}";
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args.Where(a => a != "--no-browser").ToArray(),
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.WebHost.UseUrls(url);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 131072);
builder.Services.AddSingleton<IPokemonGenerator, PokemonGenerator>();
builder.Services.AddSingleton<IWebTradeDevice, UsbTradeDevice>();
builder.Services.AddSingleton(sp => new WebOrders(
    sp.GetRequiredService<IPokemonGenerator>(), sp.GetRequiredService<IWebTradeDevice>(),
    Environment.GetEnvironmentVariable("SYSBOT_WEB_DATA") ?? Path.Combine(AppContext.BaseDirectory, "data")));
var app = builder.Build();

app.Use(async (context, next) =>
{
    // Loopback-only, no login dialog. Reject cross-site writes and DNS-rebinding Host headers.
    if (context.Request.Host.Host is not ("127.0.0.1" or "localhost" or "::1" or "[::1]"))
    {
        context.Response.StatusCode = 403;
        return;
    }
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.Request.Method != "GET")
        {
            var origin = context.Request.Headers.Origin.ToString();
            var sameOrigin = string.IsNullOrEmpty(origin) || origin == $"{context.Request.Scheme}://{context.Request.Host}";
            if (!sameOrigin || context.Request.Headers["X-SysBot-Request"] != "local-web")
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { error = "请从本地派送网页操作。" });
                return;
            }
        }
    }
    try { await next(context); }
    catch (OrderException e)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { error = e.Message, details = e.Details });
    }
    catch (Exception e) when (!context.RequestAborted.IsCancellationRequested)
    {
        app.Logger.LogError(e, "Local web request failed");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "这次操作没能完成，配置已保留。请重试；仍失败时查看程序窗口日志。" });
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/catalog", () => new { pokemon = PokemonCatalog.Choices, defaultSpecies = "Metagross" });
app.MapGet("/api/state", (WebOrders orders) => orders.Snapshot());
app.MapGet("/api/usb", () => UsbDevices.Scan());
app.MapPost("/api/connect", (ConnectRequest request, WebOrders orders) => orders.Connect(request.Port));
app.MapPost("/api/orders", async (OrderRequest request, WebOrders orders) => await orders.SubmitAsync(request));
app.MapDelete("/api/orders/{id}", (string id, WebOrders orders) => orders.Cancel(id));
app.MapGet("/favicon.ico", () => Results.NoContent());
app.MapGet("/health", () => new { status = "ok", hardwareTested = false });
app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine($"本地派送台：{url} — 关闭此窗口会停止派送；不要同时运行桌面版控制同一台 Switch。");
    if (!noBrowser)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { Console.WriteLine("请手动打开上面的地址。"); }
    }
});
await app.RunAsync();

public partial class Program;
