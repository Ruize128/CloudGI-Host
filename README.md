# CloudGI Host

Windows 原生游戏主机程序，使用 C#、.NET 10 和 WinUI 3。

## 当前功能

- 主页提供“启动游戏”按钮，显示已保存的游戏路径与启动参数。
- 设置页面支持选择文件、选择目录或手动粘贴路径。
- 支持 `YuanShen.exe` 与 `GenshinImpact.exe`；选择目录时自动定位其中的游戏文件。
- 自定义启动参数可留空，保存后原样传给游戏。默认不附加参数。
- 同一 Host 中的启动请求串行处理；发现当前 Windows 会话已有原神进程时不重复启动。
- 主页每秒检查游戏进程，显示“未运行 / 启动中 / 运行中”，运行中显示当前 PID 并禁用启动按钮。
- 从官方启动器或其他入口启动、关闭游戏后，主页自动跟随更新；重启游戏后重新发现 PID。

修改设置后先点击“保存设置”，再返回主页启动。游戏已经运行时，新的参数在下次启动时生效。
若 Windows 弹出文件安全确认或启动授权窗口，需要在主机上处理，程序会保留这些系统检查。
界面显示“已发送启动请求”表示 Windows 已创建进程，不表示游戏已登录或已准备好串流。
程序只负责启动，不会自动登录、点击“进入游戏”或执行游戏内操作。

配置保存在当前 Windows 用户的 `%LOCALAPPDATA%\CloudGI\Host\settings.json`。
配置文件不随源码提交；损坏或不支持的配置会显示错误，由用户明确重新保存。
保存使用同目录临时文件替换，避免直接覆盖写入时留下半份配置。

## 游戏运行状态

`GameLaunchService` 同时提供独立的 `IGameStatusService.GetStatusAsync()` 查询入口，供界面和后续控制 API 复用。
查询不依赖游戏路径配置：即使尚未配置 Host，也能发现外部启动的游戏。
Windows 适配层按 `YuanShen` / `GenshinImpact` 进程名查找，筛选当前 Windows 会话，检查进程是否退出。
每次查询重新获取 PID，不持久化 PID，也不读取游戏内存或识别游戏画面。

检测失败时显示“检测失败”及原因，并暂时禁用启动按钮；下一次轮询成功后自动恢复。
“启动中”表示 Host 正在处理启动请求；“运行中”只表示存在游戏进程，不代表登录或加载完成。
状态轮询独立于配置读取和路径校验，查询与校验在后台执行；关闭 Host 时取消轮询，不会关闭游戏。

## 编译与运行

需要 Windows 11（x64 或 ARM64）和 .NET 10 SDK。首次构建需要联网还原 NuGet 依赖。
可在 Visual Studio 中打开 `CloudGI.Host.slnx`，也可以使用 Windows PowerShell。
WinUI 的构建与运行在 Windows 中完成；macOS 可编辑源码并运行核心逻辑测试。

若使用已经为 Parallels 准备的用户目录 SDK，先在当前 PowerShell 会话设置：

```powershell
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\CloudGI\Tools\dotnet-10.0.402-arm64"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
```

Parallels Windows ARM64：

```powershell
dotnet build CloudGI.Host.slnx -p:Platform=ARM64
dotnet run --project src/CloudGI.Host -p:Platform=ARM64 --no-build
```

Windows x64 台式机：

```powershell
dotnet build CloudGI.Host.slnx -p:Platform=x64
dotnet run --project src/CloudGI.Host -p:Platform=x64 --no-build
```

Windows App SDK 运行时随构建输出提供；运行仍需要同架构的 .NET 10 运行时。
目前为直接启动可执行程序的 unpackaged 应用。

## 核心服务与后续 Client 接入

- `src/CloudGI.Host`：WinUI 导航、主页、设置页、ViewModel 和 Windows 进程适配。
- `src/CloudGI.Host.Core`：配置存储、路径校验、启动请求及重复启动保护，不依赖 WinUI。
- `tests/CloudGI.Host.Core.Tests`：配置异常、参数保留、路径解析、并发启动和取消等测试。

主页通过 `IGameLaunchService.LaunchAsync()` 启动游戏。未来 Host 控制 API 可复用同一个服务实例，
无需模拟按钮点击。服务只读取 Host 已保存的配置，调用者不传入可执行文件、命令行或脚本。
当前没有新增 Client 协议、网络接口、配对或串流功能。

运行核心测试（Windows / macOS 均可）：

```sh
dotnet test tests/CloudGI.Host.Core.Tests/CloudGI.Host.Core.Tests.csproj
```

单元测试使用进程启动替身，不会打开实际游戏。

## 设计参考

参考以下项目将游戏路径与启动参数分离保存、由主页发起启动的交互设计：

- [FufuLauncher 游戏路径选择与校验](https://github.com/FufuLauncher/FufuLauncher/blob/ec36b66a7de8eedee87891a7405bceffbea6a9b5/FufuLauncher/Views/Pages/GameSettingsPage.GamePath.cs)
- [BetterGI 启动配置](https://github.com/babalae/better-genshin-impact/blob/0cdfbcd62691bbc2f77e745823c184e0a8f6da45/BetterGenshinImpact/Core/Config/GenshinStartConfig.cs)
- [BetterGI 游戏进程启动](https://github.com/babalae/better-genshin-impact/blob/0cdfbcd62691bbc2f77e745823c184e0a8f6da45/BetterGenshinImpact/GameTask/SystemControl.cs)

## 手动验收

1. 首次运行时，主页应提示设置路径，“启动游戏”保持禁用。
2. 打开设置，使用“选择文件”或“选择目录”选择游戏；无效路径保存时应显示错误。
3. 填写可选参数并保存，返回主页核对路径和参数，再重启 Host 检查设置是否保留。
4. 点击“启动游戏”，按 Windows 的正常安全确认流程处理可能出现的提示。
5. 仅观察游戏登录界面，不点击“进入游戏”；主页应显示“运行中”、当前 PID，启动按钮变为禁用的“游戏已运行”。
6. 关闭游戏后，主页应自动恢复“未运行”；路径配置有效时重新启用启动按钮。
7. 从官方启动器等外部入口打开游戏，检查主页能自动发现；重启游戏后显示新的 PID。
8. 测试完成后关闭游戏，确认没有遗留游戏进程。

## 验证记录

### 游戏配置与启动

- 最初配置与启动核心的 16 项测试在 macOS、Windows 中通过。
- Windows ARM64、x64 Debug 构建与锁定依赖还原通过，零警告、零错误。
- 指定游戏目录的路径解析、配置保存和重新读取已验证。
- 设置页已在 Parallels 中人工确认正常显示，页面保存的路径已从配置文件核对。
- 初始窗口按屏幕 DPI 缩放，并限制在当前显示器工作区域内。
- Parallels 中通过 Host 主页实际启动了指定游戏，参数保存为 `-screen-width 1280 -screen-height 720 -screen-fullscreen 0`。
- 游戏进程的父进程已核对为本次 Host；用户人工确认到达登录界面且未点击“进入游戏”。界面工具存在延迟，因此登录到达以人工确认记录。
- 测试游戏已关闭，并重新检查不存在 `YuanShen` / `GenshinImpact` 进程。

### 运行状态检测（2026-10-29）

- 核心测试增加到 23 项，覆盖外部进程发现、退出和 PID 更新、启动等待、检测失败与恢复、取消等行为；macOS 和 Windows 均通过。
- Windows ARM64、x64 Debug 构建及锁定依赖还原通过，零警告、零错误。
- 使用临时自编写进程验证实际 Windows 进程枚举，以及 ViewModel 的定时刷新、按钮禁用和恢复、启动失败恢复、取消后停止轮询。
- 临时集成程序直接链接生产 ViewModel 和 Windows 进程适配代码，只用枚举替身隔离 WinUI 的提示类型；此项不代替视觉检查。
- 本轮未启动真实游戏；临时进程不包含游戏逻辑，测试后全部关闭。游戏配置保留原样。
- 已核对首页“未运行”及路径不可访问提示。游戏位于家中 NAS，2026-10-29 验证环境无法访问，因此当时暂缓真实游戏验收。
- 真实游戏启动、外部发现、退出恢复和 PID 更新已于 2026-10-30 补测，见下节。系统确认窗口停留期间“启动中”的视觉显示未单独确认；启动等待状态已有自动化覆盖。

进程轮询与状态变化更新的设计参考：
[FufuLauncher 主页监测](https://github.com/FufuLauncher/FufuLauncher/blob/71bd2f35c0dc8fc54a4876821a862fa21f67ccd4/FufuLauncher/ViewModels/Home/MainViewModel.Game.cs)。

### 真实游戏状态验收（2026-10-30）

NAS 路径已恢复可访问，Windows 验证副本的状态服务、主页 ViewModel 和 XAML 已与当前源码核对一致。
因界面工具无法连接，界面和登录画面由用户人工确认；只读观察程序复用生产状态查询和 Windows 进程适配代码，记录每秒查询的状态变化。

| 操作 | 人工界面确认 | 进程查询记录 |
| --- | --- | --- |
| 初始打开 Host | 未运行，启动按钮可用 | NotRunning |
| 从 Host 启动游戏 | 到达登录界面；运行中、显示 PID、按钮禁用 | Running，PID 23200；父进程为本次 Host |
| 关闭游戏 | 未运行，启动按钮恢复 | NotRunning |
| 从官方启动器重新启动 | 自动发现、PID 更新、按钮禁用 | Running，PID 22012；父进程为 HYP.exe |
| 再次关闭游戏 | 未运行，启动按钮恢复 | NotRunning |

- 两次游戏启动均按约定只停留在登录界面，未点击“进入游戏”。
- 状态记录未出现检测错误；游戏实际退出后，复查不存在 YuanShen / GenshinImpact 进程。
- Host 验证进程及只读观察程序已结束，游戏路径和启动参数保留原样。
- 本次补测未发现需要修改功能代码的问题；状态检测改动仍未提交。
