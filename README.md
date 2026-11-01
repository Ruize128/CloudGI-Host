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

## 内置 Sunshine

Host 随附官方 Sunshine `2026.914.233613` lite 运行文件，按 ARM64 / x64 构建自动选择架构。
首次编译从官方 GitHub 下载并校验 SHA256，缓存在 `src/CloudGI.Host/obj/sunshine`；之后可复用缓存。
程序运行时只使用输出目录的 `Sunshine` 副本，不下载文件，不安装或接管 Sunshine 系统服务。
版本、下载地址和校验值位于 `build/Sunshine.targets`；上游许可及源码获取说明位于 `third-party/Sunshine`。

启动 Host 会自动启动专属实例。主页显示名称、端口、版本和启动状态；首次编码器检测可能需要数十秒。
设置页可修改主机名称；保存后会重启专属串流实例并断开当前连接，配对身份保持不变。
配置、证书、UUID、配对记录及日志独立保存在 `%LOCALAPPDATA%\CloudGI\Host\Sunshine`。
本机管理密码随机生成并通过 Windows DPAPI 保存，管理请求仅连接本机并核验本实例的证书。

默认优先使用基础端口 `48989`，检测 HTTP / HTTPS / Web UI / RTSP 及视频、控制、音频 UDP 的完整端口组；
冲突时尝试其他端口组并保存结果。端口预检之后仍会检查服务响应及专属 UUID，失败时报告原因。
独立安装的 Sunshine 的配置、身份、端口和服务保持原样。

手机连接流程：

1. 等待主页显示“可连接”。在设置页点击“允许局域网连接”，按正常流程确认 Windows 授权。
   此操作只为当前随附 Sunshine 程序创建本地子网入站规则；移动程序目录后需要重新设置。
2. 在同一局域网的官方 Moonlight 中选择 CloudGI 的主机名称。自动发现不可达时可手动添加 `IP:基础端口` 排查。
3. 手机显示 PIN 后，在 Host 设置页选择等待配对的设备、输入四位 PIN 并点击“配对”。
4. 配对完成后在 Moonlight 选择 Desktop，验证电脑画面、声音和基础输入。
5. 关闭 Host 会停止所属 Sunshine，Windows Job Object 也会在 Host 异常退出时清理它。
   已有 Sunshine 继续运行；退出 Host 不关闭游戏。

本轮保留现有游戏和显示器分辨率。当前仍是显示器级捕获；游戏自动启动串流入口、原生多点触控和
CloudGI Client 的 Moonlight 集成属于后续工作。Windows 发现服务可能按计算机名生成 mDNS 实例名，
列表展示名称以 Sunshine 返回的主机信息为准；同机双实例发现需要实际手机验收。

## 游戏运行状态

`GameLaunchService` 同时提供独立的 `IGameStatusService.GetStatusAsync()` 查询入口，供界面和后续控制 API 复用。
查询不依赖游戏路径配置：即使尚未配置 Host，也能发现外部启动的游戏。
Windows 适配层按 `YuanShen` / `GenshinImpact` 进程名查找，筛选当前 Windows 会话，检查进程是否退出。
每次查询重新获取 PID，不持久化 PID，也不读取游戏内存或识别游戏画面。

检测失败时显示“检测失败”及原因，并暂时禁用启动按钮；下一次轮询成功后自动恢复。
“启动中”表示 Host 正在处理启动请求；“运行中”只表示存在游戏进程，不代表登录或加载完成。
状态轮询独立于配置读取和路径校验，查询与校验在后台执行；关闭 Host 时取消轮询，不会关闭游戏。

## 编译与运行

编译需要 Windows 11（x64 或 ARM64）和 .NET 10 SDK。首次构建需要联网还原 NuGet 依赖及 Sunshine。
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

输出随附 .NET 10、Windows App SDK 运行时及 Sunshine，目标电脑无需单独安装 .NET 或 Sunshine。
目前为直接启动可执行程序的 unpackaged 应用；使用完整输出目录运行。

生成自包含发布目录：

```powershell
dotnet publish src/CloudGI.Host/CloudGI.Host.csproj -c Release -p:Platform=ARM64 -p:RestoreLockedMode=true
# Windows 台式机将 ARM64 换为 x64。
```

发布目录位于 `src/CloudGI.Host/bin/<平台>/Release/<目标框架>/<运行标识>/publish`。
发布会一并携带应用的 PRI / 编译 XAML 资源；不能只复制 exe。

## 核心服务与后续 Client 接入

- `src/CloudGI.Host`：WinUI 导航、主页、设置页、ViewModel 和 Windows 进程适配。
- `src/CloudGI.Host.Core`：配置存储、路径校验、启动请求及重复启动保护，以及专属 Sunshine 配置和端口选择，不依赖 WinUI。
- `tests/CloudGI.Host.Core.Tests`：配置异常、参数保留、路径解析、并发启动和取消，以及 Sunshine 身份保留、配置隔离、端口冲突等测试。

主页通过 `IGameLaunchService.LaunchAsync()` 启动游戏。未来 Host 控制 API 可复用同一个服务实例，
无需模拟按钮点击。服务只读取 Host 已保存的配置，调用者不传入可执行文件、命令行或脚本。
CloudGI Client 现在通过专属 HTTPS 控制入口自动配对。控制端口为 Sunshine 基础端口加 2，
提供 `GET /cloudgi/info` 和 `POST /cloudgi/pair`；配对请求仅匹配同一客户端标识和来源地址。
这是可信家庭局域网自动授权模式，局域网可达接口的 CloudGI Client 可以自动取得串流授权；不用于公网暴露。
Sunshine 管理凭据始终留在 Host。自动配对仅处理授权，不接收程序路径、命令行或游戏启动请求。
Windows 放行规则分别限定到随附 Sunshine 和本 Host 程序、本地子网；首次准备会请求正常 Windows 授权。

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

### 内置 Sunshine 集成验证（2026-11-01）

- macOS 核心测试共 32 项通过，新增专属配置、名称输入、身份初始化和 TCP / UDP 派生端口冲突覆盖。
- Windows ARM64 Debug 构建及 x64 / ARM64 Release 自包含发布通过。
- 临时 Windows 集成程序直接使用生产配置和进程管理代码，验证实际 Sunshine 启动、端口组回退、重复实例保护、
  本机管理认证与证书核验、改名、UUID 保留、重新打开，以及强制终止 Host 后的 Job Object 清理；全部通过。
- 原有 Sunshine 的服务响应与 UUID 在验证前后保持一致。未启动游戏。
- 用户已确认官方 Moonlight 可以发现、配对并投屏；声音和基础输入未单独确认。

### CloudGI Client 自动配对（2026-11-01）

- 新增可信局域网 HTTPS 自动配对入口，与 Sunshine 复用同一主机证书。
- 控制端口纳入完整端口组冲突检查；启动时执行实际 HTTPS 握手和身份自检。
- Windows TLS 使用当前用户密钥容器导入证书，避免 Schannel 无法使用临时私钥的问题。
- 34 项核心测试通过，覆盖客户端标识、PIN 格式及来源地址和待配对会话的匹配。
- Android 真机日志已确认发现 CloudGI 主机、无需手动 PIN 完成首次配对、查询 Desktop 并接收和渲染 H.264 视频帧。
- 当前虚拟机日志显示音频采集初始化失败，声音尚不可用；输入和重连仍需分别验收。
