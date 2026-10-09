# 朱紫 USB 本地交换版

## 本版是什么

以官方 `kwsch/SysBot.NET` 提交 `844c1f31face95bc8e1cfec9c9b7cbd9f7a62895` 为基线，移植 FrancescoPacifico 的 [PR #216](https://github.com/kwsch/SysBot.NET/pull/216)（原提交 `1fb83c2d4bb541651251f9564b5d26ce9314e10d`）。保留原作者提交及 cherry-pick 来源，另外修正本地模式的联网回退与在线身份依赖。AGPL-3.0 许可不变。

- 仓库：<https://github.com/Moeblack/SysBot.NET>
- 功能分支：`feature/sv-local-trade`。`master` 保留官方基线，后续可继续同步。
- Windows 下载：<https://github.com/Moeblack/SysBot.NET/releases>
- 目标游戏：**朱／紫 4.0.0**，沿用当前官方版本检查与内存指针，不兼容的游戏版本会被拒绝。
- 当前验证：完整解决方案 Release 构建通过；74 项测试通过，其中 36 项为本地/在线策略回归测试；独立 Windows 发布程序已通过无设备 GUI 启动验证（窗口 `SysBot: Pokémon (SV)`）。**尚未完成真实 Switch 双机交换测试。** 测试不证明本地内存指针在实机上的行为。

## 推荐入口：本地网页

下载 `SysBot-SV-Local-Web-win-x64.zip`，解压后运行 `start-web.cmd`；直接派一只或一次贴队伍，复用上游生成和顺序派送。网页使用说明见 [SV-LOCAL-WEB.zh-CN.md](SV-LOCAL-WEB.zh-CN.md)。新增网页后的完整测试共 103 项通过，真实 Switch 验收仍未完成。

## 连接方式

```text
Windows 电脑 ──USB / usb-botbase── CFW 派送 Switch
                                      │
                                原生本地无线交换
                                      │
                                原厂接收 Switch
```

接收端不需要改机、安装插件或运行 homebrew。你在接收端手动进入本地连接交换、输入相同密码、选择交换材料和确认。

**USB 是电脑控制派送机的通道，不是两台 Switch 的交换线。** 游戏使用原生本地无线，不是两台设备经路由器进行 IP LAN 交换。

## 准备与配置

1. 派送端运行兼容的 Atmosphère/CFW 与 [usb-botbase](https://github.com/Koi-3088/usb-botbase)。电脑按[上游 USB 配置说明](https://github.com/kwsch/SysBot.NET/wiki/Configuring-a-new-USB-Connection)安装对应驱动，确认实际 USB 端口号。不要把 TCP 的 6000 当作 USB 端口。
2. 两台机器准备匹配的朱紫 4.0.0、解锁交换功能。先手动验证两机的本地无线交换能完成。
3. 解压发行包到独立目录，启动 `SysBot.exe`。发行 ZIP 自带 `config.json`，预选本地模式、SV、空设备列表，关闭自动分发；不要覆盖自己的旧配置。自行源码构建时可复制 `examples/config.sv-local.json` 为配置。程序 Mode 选择 **SV**（`config.json` 内数值为 `4`）。
4. 添加派送机，连接类型选 **USB**，端口填实际端口，routine 选 **LinkTrade** 或 **FlexTrade**。
5. 在 `Hub → Trade` 设置 **`PerformLocalTradeSV = true`**。源码中新建配置的默认仍为 `false`，保留官方在线行为；本分支发行包的示例配置显式设为 `true`。修改该项后必须停止并重新启动 bot；模式在每次启动时固定。
6. 派送机在游戏大地图、非对话/盒子界面启动 bot。预先留空**盒子 1 的第 1 格**，此格会被写入待派送个体，不能放收藏。先备份派送端存档。
7. 两台游戏都保持离线模式；本地无线必须可用，不要用飞行模式同时禁掉本地无线。程序若发现派送端游戏已经在线，会尝试退出在线状态；不能确认离线就失败，不会降级为在线交换。

配置位置示意（合并到程序生成的配置，不是完整配置文件）：

```json
{
  "Mode": 4,
  "Hub": {
    "Trade": {
      "PerformLocalTradeSV": true
    }
  }
}
```

**与原 PR 的重要区别：本地模式＋Wi-Fi/未知协议现在拒绝启动；原 PR 在这个组合下会退回联网流程。** 连接类型不能只靠上述 JSON 片段配置，仍需正确添加 USB bot。

## 不依赖 Discord 的最小运行方式

现已提供 [本地网页下单版](SV-LOCAL-WEB.zh-CN.md)：支持单只默认配置和整队 PS 一次入队，不需要 Discord，详见该说明。以下文件池路径仍适用于原桌面版，不是网页批量队列。上游生成、合法性和任务队列仍保留；现有 Discord 等集成是独立的互联网连接，不能称作“全部内网”。若要求整个流程不连外部服务，关闭这些集成。

可直接用已有的本地文件池功能验证交换路径：

1. 在 PKHeX 等工具中准备经过检查的朱紫 `.pk9` 文件，放入单独的派送目录；首次只放一只低价值测试个体。
2. 设置 `Hub → Folder → DistributeFolder` 为该目录。
3. 设置 `Hub → Distribution`：`DistributeWhileIdle = true`、`RandomCode = false`、`TradeCode` 为你选定的交换密码、`LedyQuitIfNoMatch = false`。
4. 启动 LinkTrade/FlexTrade bot，原厂接收机以相同密码进行**本地连接交换**。
5. 文件池会持续循环分发，**不是一次性消费队列**；收完需要的数量后停止 bot。增加/替换文件后重新加载文件池或重启程序。

本地模式的随机分发会派送已经由文件池选定的个体，不执行按对方昵称/NID 查询的 Ledy 替换。`LedyQuitIfNoMatch = true` 会明确拒绝这类本地分发，不会默默忽略后继续发。指定个体的 Specific 队列保留。

## 本地模式适配与限制

- 正常开始、连续交换、搜索前、等待伙伴和异常恢复都按同一个固定模式处理；`ConnectToOnline` 另有本地模式拒绝保护。
- 本地模式不把 NID=0 当作一个真实玩家，不读取/写入在线 NID 指针，也不调用在线信誉、冷却、封禁缓存或成功身份登记。
- 本地配对需从已确认的离线 Poké Portal 发起搜索，再进入交易盒子、读到非空且不是派送端自己的训练家信息，并解析出有效的对方个体指针。后续仍保留上一轮个体防重复、PK9 校验和与交换结果检查。
- **以上状态与训练家/个体指针仍沿用官方在线实现，尚待实机验证其本地有效性。** 找不到合法状态会超时，不猜新地址，也不回退在线。
- 两个存档的 OT、TID、SID 完全相同时，当前保守身份检查可能无法区分双方，不作为首轮验证环境。
- 交换完成判断沿用上游，实机前不能承诺每种取消/断线场景均可恢复。USB 物理断线后可能需要手动停止、重连和重启 bot。
- 应用的本地交换策略不是系统级防火墙，不能保证主机系统或其他软件没有外部连接；需要严格隔离时另行配置网络访问控制，同时保留本地无线。
- PKHeX/ALM 合法性、HOME tracker 及原有个体写入校验未为了离线模式而放宽。工具检查通过、本地交换成功，不等于官方对 HOME/联网/赛事使用的认可。

## 构建与测试

需要 **.NET 10 SDK**（仅安装 Runtime 不够）：

```powershell
dotnet restore SysBot.NET.slnx
dotnet build SysBot.NET.slnx -c Release --no-restore
dotnet test SysBot.Tests/SysBot.Tests.csproj -c Release --no-restore

dotnet publish SysBot.Pokemon.WinForms/SysBot.Pokemon.WinForms.csproj `
  -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```

WinForms 项目是 Windows 程序。发行 ZIP 使用独立运行时；源码保留官方依赖 DLL 与 NuGet 版本，不降级框架，不重写游戏指针。

CI 在 Windows 上执行完整构建和测试，并提供 Windows 发布文件。测试覆盖 USB 限制、默认配置兼容、离线恢复、禁止在线回退、零 NID 的本地配对策略以及在线模式不变，不会连接 Switch 或任天堂服务。

## 待实机验收（未完成，不打勾）

- [ ] 派送端 USB 通信、游戏版本和存档身份正确。
- [ ] 本地开关开启但设置 Wi-Fi 时明确拒绝，不开始游戏联网。
- [ ] 两台游戏离线完成一次指定个体交换；B1S1 的写入与收货内容正确。
- [ ] 连续完成第二次交换，未出现旧训练家/旧个体误匹配。
- [ ] 接收端不搜索、慢确认、取消时，程序正确超时或恢复，仍保持离线。
- [ ] NID 缺失时，本地训练家与个体指针仍有效；无效则收集日志与版本，不强写地址。
- [ ] 派送端最初在线时能正确退出在线后再本地搜索（若严格禁止预先在线，可不进行此项，保持两机离线验证）。

如实机失败，应提供游戏/CFW/usb-botbase版本、模式与端口设置及失败日志；不要提交账号令牌、完整个人存档或收藏个体到公开 issue。
