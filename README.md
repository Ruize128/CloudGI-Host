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

CloudGI 手机连接流程：

1. 打开 Host，首次按正常流程确认 Windows 局域网访问授权，等待专属 Sunshine 可连接。
2. 手机 CloudGI Client 点击“启动游戏”，自动发现和配对，再请求 Host 启动原神。
3. Host 仅使用已保存的游戏路径和参数；已有游戏进程直接复用。手机确认实际进程存在后连接 Desktop 串流。
4. 手机正常断开串流后，Host 自动退出本次会话对应的游戏；客户端被杀或网络不可达时由心跳超时收尾。Windows 的文件安全或提权提示仍在电脑正常处理。

控制接口在 Sunshine 基础端口加 2 提供：

- `POST /cloudgi/game/start/{requestId}`：32 位十六进制请求标识，无请求体。返回当前操作标识、starting/running/failed 状态。
- `GET /cloudgi/game/status/{requestId}`：查询同一操作；实际游戏退出后返回 exited。
- `POST /cloudgi/game/stream/{requestId}`：将已确认的游戏进程绑定到一个串流会话。
- `POST /cloudgi/game/heartbeat/{requestId}`：客户端实际开始串流后每两秒续期。
- `POST /cloudgi/game/disconnect/{requestId}`：正常断开时立即结束对应会话并退出游戏。

串流已连接后，连续八秒没有心跳，Host 在下一次一秒轮询时开始退出游戏；初次媒体连接有 45 秒启动宽限。
退出先请求正常关闭，等待五秒后若仍未退出，只结束绑定的游戏进程，不结束进程树。
绑定同时记录 PID、进程创建时间和 Windows 会话，避免 PID 复用误关其他进程；旧会话通知不能关闭新会话。
没有建立 CloudGI 串流会话时，不会自动关闭电脑上独立运行的游戏。官方 Moonlight 不发送 CloudGI 会话心跳。
Host 正常关闭也会清理当前活动会话；Host 被强制终止时不能保证继续执行游戏清理。

接口复用已有 Moonlight 配对证书验签，每次查询核对设备仍获授权；启动请求不接受任意路径、参数或命令。
请求包含时间戳和一次性随机数；Host 和手机时钟需正常同步。最近 64 个请求结果保存在内存中，Host 重启后清空；
正在进行的启动不因 HTTP 断开而取消。相同请求重试不会再次启动，新的点击仍会先检查已有进程。
Windows 启动调用返回后最多等待 30 秒确认实际游戏进程；超时仅报错，不关闭游戏或自动重试。

也可使用官方 Moonlight 手动连接：

1. 等待主页显示“可连接”。在设置页点击“允许局域网连接”，按正常流程确认 Windows 授权。
   此操作只为当前随附 Sunshine 程序创建本地子网入站规则；移动程序目录后需要重新设置。
2. 在同一局域网的官方 Moonlight 中选择 CloudGI 的主机名称。自动发现不可达时可手动添加 `IP:基础端口` 排查。
3. 手机显示 PIN 后，在 Host 设置页选择等待配对的设备、输入四位 PIN 并点击“配对”。
4. 配对完成后在 Moonlight 选择 Desktop，验证电脑画面、声音和基础输入。
5. 关闭 Host 会停止所属 Sunshine，Windows Job Object 也会在 Host 异常退出时清理它。
   已有 Sunshine 继续运行；若存在 CloudGI 活动游戏会话，正常关闭 Host 会先退出该游戏。

保留现有游戏和显示器分辨率，当前仍是显示器级捕获；游戏原生多点触控、音频问题修复及重连完善属于后续工作。Windows 发现服务可能按计算机名生成 mDNS 实例名，
列表展示名称以 Sunshine 返回的主机信息为准；同机双实例发现需要实际手机验收。

## 游戏运行状态

`GameLaunchService` 同时提供独立的 `IGameStatusService.GetStatusAsync()` 查询入口，供界面和后续控制 API 复用。
查询不依赖游戏路径配置：即使尚未配置 Host，也能发现外部启动的游戏。
Windows 适配层按 `YuanShen` / `GenshinImpact` 进程名查找，筛选当前 Windows 会话，检查进程是否退出。
每次查询重新获取 PID，不持久化 PID，也不读取游戏内存或识别游戏画面。

检测失败时显示“检测失败”及原因，并暂时禁用启动按钮；下一次轮询成功后自动恢复。
“启动中”表示 Host 正在处理启动请求；“运行中”只表示存在游戏进程，不代表登录或加载完成。
状态轮询独立于配置读取和路径校验，查询与校验在后台执行；关闭 Host 时取消轮询；活动串流会话的游戏清理由会话管理负责。

## 编译与运行

编译需要 Windows 11（x64 或 ARM64）和 .NET 10 SDK。首次构建需要联网还原 NuGet 依赖及 Sunshine。
可在 Visual Studio 中打开 `CloudGI.Host.slnx`，也可以使用 Windows PowerShell。
Host 的 manifest 请求管理员权限，启动时按 Windows 正常 UAC 流程确认，以便断开后退出高权限游戏。
此方式已获用户明确批准；不关闭 UAC 或更改系统安全策略。使用 Visual Studio 调试时需以管理员权限运行 IDE。
WinUI 的构建与运行在 Windows 中完成；macOS 可编辑源码并运行核心逻辑测试。

若使用已经为 Parallels 准备的用户目录 SDK，先在当前 PowerShell 会话设置：

```powershell
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\CloudGI\Tools\dotnet-10.0.402-arm64"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
```

Parallels Windows ARM64：

```powershell
dotnet build CloudGI.Host.slnx -p:Platform=ARM64
Start-Process .\src\CloudGI.Host\bin\ARM64\Debug\net10.0-windows10.0.26100.0\win-arm64\CloudGI.Host.exe -Verb RunAs
```

Windows x64 台式机：

```powershell
dotnet build CloudGI.Host.slnx -p:Platform=x64
Start-Process .\src\CloudGI.Host\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\CloudGI.Host.exe -Verb RunAs
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

主页和 Host 控制 API 共用 `IGameLaunchService.LaunchAsync()` 启动游戏，
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

### 手机请求启动游戏（2026-11-01）

- 新增签名启动和状态接口，和 Host 界面共用游戏启动与状态服务。
- 核心测试共 41 项通过，新增启动去重、失败恢复、实际游戏 PID、已有进程与配置不可访问、请求签名和撤销授权覆盖。
- 使用生产 HTTPS 控制接口与临时游戏替身完成集成检查：未配对拒绝、外部参数拒绝、签名启动、重复请求去重、状态查询、退出状态和撤销授权；未运行真实游戏。
- Windows ARM64 / x64 构建与 ARM64 Release 发布通过，零警告、零错误。新版 Host 已在 Parallels 启动，Client 已安装到测试手机。

### 手机真实游戏启动验收（2026-11-01，以下为旧保留游戏策略记录）

- 用户确认在手机点击“启动游戏”后自动进入串流并看到游戏登录界面，未点击“进入游戏”。
- Android 日志确认签名启动请求成功、Host 确认游戏进程后打开原生 Desktop 串流，硬件解码器实际渲染视频帧。
- Windows 确认原神 PID 为 25704，父进程为本次 Host（1592）。
- 结束测试客户端进程后，原神 PID 25704 保持运行，验证连接中断不会关闭游戏。
- 已关闭本轮测试游戏，并确认没有遗留 YuanShen / GenshinImpact 进程。
- 用户另行确认可从串流正常返回 Client 主页，电脑游戏仍运行，这是预期行为。旧策略已由断开退出取代；已有游戏复用、重复请求与失败恢复仍有核心及 HTTPS 替身集成覆盖。

### 断开自动退出游戏（2026-11-01）

- 已实现串流页断开通知、真实连接后的心跳及 Host 超时退出，替代之前“断开保留游戏”的行为。
- 用户明确批准 Host 启动请求管理员权限，manifest 使用 requireAdministrator，uiAccess 保持 false。
- 46 项核心测试通过；生产 HTTPS 接口替身集成验证已覆盖会话绑定、心跳、正常断开、重复断开及旧会话隔离。
- Android 构建与共享桌面界面编译、Windows ARM64/x64 构建和 ARM64 发布通过。用户确认正常返回 Client 后游戏自动关闭、Host 恢复未运行；日志确认结束通知和清理成功，PID 18584 的退出已由只读观察程序记录。客户端强退实机验证也已通过：强退 Client 后未直接操作游戏，Host 自动退出游戏 PID 19696，观察到从强退到进程消失约 12 秒（包含心跳检测和关闭等待）。

- 两次实机测试均停留在游戏登录界面。正常返回关闭 PID 18584；第二次强退客户端后由 Host 自动关闭 PID 19696。原有 Sunshine 服务和 CloudGI 专属实例继续运行，测试后未遗留游戏进程。
