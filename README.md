# CloudGI Host

Windows 原生游戏主机程序，使用 C#、.NET 10 和 WinUI 3。

## 当前功能

- 主页提供“启动游戏”按钮，显示已保存的游戏路径与启动参数。
- 设置页面支持选择文件、选择目录或手动粘贴路径。
- 支持 `YuanShen.exe` 与 `GenshinImpact.exe`；选择目录时自动定位其中的游戏文件。
- 自定义启动参数可留空，保存后原样传给游戏。默认不附加参数。
- 同一 Host 中的启动请求串行处理；发现当前 Windows 会话已有原神进程时不重复启动。

修改设置后先点击“保存设置”，再返回主页启动。游戏已经运行时，新的参数在下次启动时生效。
若 Windows 弹出文件安全确认或启动授权窗口，需要在主机上处理，程序会保留这些系统检查。
界面显示“已发送启动请求”表示 Windows 已创建进程，不表示游戏已登录或已准备好串流。
程序只负责启动，不会自动登录、点击“进入游戏”或执行游戏内操作。

配置保存在当前 Windows 用户的 `%LOCALAPPDATA%\CloudGI\Host\settings.json`。
配置文件不随源码提交；损坏或不支持的配置会显示错误，由用户明确重新保存。
保存使用同目录临时文件替换，避免直接覆盖写入时留下半份配置。

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
5. 仅观察游戏登录界面，不点击“进入游戏”；再次发起启动应提示游戏已在运行。
6. 测试完成后关闭游戏，确认没有遗留游戏进程。

## 本次验证

- 配置与启动核心的 16 项测试在 macOS、Windows 中通过。
- Windows ARM64、x64 Debug 构建与锁定依赖还原通过，零警告、零错误。
- 指定游戏目录的路径解析、配置保存和重新读取已验证。
- 设置页已在 Parallels 中人工确认正常显示，页面保存的路径已从配置文件核对。
- 初始窗口按屏幕 DPI 缩放，并限制在当前显示器工作区域内。
- Parallels 中通过 Host 主页实际启动了指定游戏，参数保存为 `-screen-width 1280 -screen-height 720 -screen-fullscreen 0`。
- 游戏进程的父进程已核对为本次 Host；用户人工确认到达登录界面且未点击“进入游戏”。界面工具存在延迟，因此登录到达以人工确认记录。
- 测试游戏已关闭，并重新检查不存在 `YuanShen` / `GenshinImpact` 进程。
