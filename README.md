# CloudGI Host

Windows 游戏串流主机，使用 C#、.NET 10 和 WinUI 3。配合 CloudGI Client，在手机上一键启动原神并串流游玩。

## 当前功能

- 设置页保存 `YuanShen.exe` / `GenshinImpact.exe` 路径、自定义参数及全屏选项。
- 首页显示进程状态、PID、实际启动参数，并提供默认开启的“以手机 UI 启动”开关。开关自动保存，本机与手机启动使用同一配置。
- 默认使用 `-screen-fullscreen 1 -window-mode borderless`，保留自定义宽高和其他参数；旧手机/云平台参数会被过滤。
- 发现当前 Windows 会话已有游戏时直接复用；修改启动模式需要退出游戏后重新启动。
- 自带专属 Sunshine，与单独安装的 Sunshine 使用独立配置、端口、身份和进程生命周期。
- CloudGI Client 自动发现、无需手动 PIN 配对、签名请求启动游戏，再连接整个显示器的 Desktop 串流。
- 手指默认使用原生多点触控；正常断开或心跳超时后关闭会话对应的游戏。

Host 启动时通过正常 UAC 流程请求管理员权限，用于管理高权限游戏。程序不执行自动登录或游戏内点击。
配置保存在 `%LOCALAPPDATA%\CloudGI\Host\settings.json`，不随源码提交。

## 手机 UI 组件

`third-party/FufuLauncher.Touchscreen` 是基于 FufuLauncher.UnlockerIsland 固定源码的触屏专用改版，使用 AGPL-3.0。
只保留触屏切换和必要的加载、扫描、Hook 逻辑，来源、修改和构建方法见该目录的 `NOTICE.md`。

ARM64 和 x64 Host 都调用随附的 x64 `CloudGI.MobileUI.exe`，向本次新建的 x64 游戏加载 `CloudGI.Touchscreen.dll`。
函数匹配与 Hook 就绪后恢复游戏并返回真实游戏 PID，后续由游戏线程回调切换手机 UI。
不会等待这个回调才开始串流，以免登录阶段尚未触发回调时卡住启动。

组件不匹配时明确失败并清理本次新建的进程；可以关闭首页开关后普通启动。已有独立游戏不会被重新注入或重启。
游戏更新后需重新验证兼容性；模块准备成功与实际 HUD 生效是不同状态。

常规 `dotnet build/publish` 使用随库的运行文件，不要求安装 C++ 工具链。
修改原生源码后须重建两个运行文件及 `SHA256SUMS`；发布目录同时携带对应源码、构建输入和许可证。
最近一次原生启动阶段记录在 `%LOCALAPPDATA%\CloudGI\Host\mobile-ui-status.json`，仅含阶段、错误码、PID 和耗时。

## 内置 Sunshine 与会话

使用官方 Sunshine `2026.914.233613` lite。首次构建下载并校验 SHA256，缓存位于 `src/CloudGI.Host/obj/sunshine`；
版本和校验值见 `build/Sunshine.targets`。运行时使用输出目录副本，不另装 Sunshine 服务。

默认优先使用基础端口 `48989`，冲突时选择其他完整端口组；CloudGI HTTPS 控制端口为基础端口加 2。
设置页可修改对外名称、允许局域网连接，也保留官方 Moonlight 的手动 PIN 配对入口。
改名重启专属实例会断开当前连接，但保留配对身份。
配置、证书、UUID、配对记录和日志独立保存在 `%LOCALAPPDATA%\CloudGI\Host\Sunshine`；管理凭据由 Host 管理。

当前自动配对适用于可信家庭局域网。后续游戏控制请求使用已配对设备证书签名、时间戳和一次性随机数；
不接受 Client 提供任意可执行文件、参数或命令。两端时钟需同步。

| 接口 | 用途 |
| --- | --- |
| `GET /cloudgi/info`、`POST /cloudgi/pair` | 发现信息及自动配对 |
| `POST /cloudgi/game/start/{requestId}` | 串行、去重的启动请求 |
| `GET /cloudgi/game/status/{requestId}` | 查询启动与实际进程状态 |
| `POST /cloudgi/game/stream/{requestId}` | 将实际游戏进程绑定到串流会话 |
| `POST /cloudgi/game/heartbeat/{requestId}` | 串流期间每两秒续期 |
| `POST /cloudgi/game/disconnect/{requestId}` | 结束会话并关闭绑定游戏 |

初次媒体连接宽限 45 秒；串流开始后连续 8 秒无心跳便开始清理。
先请求正常关闭，等待 5 秒仍未退出才结束绑定进程。身份同时核对 PID、创建时间和 Windows 会话，避免误关其他进程。
正常关闭 Host 会清理活动会话并停止专属 Sunshine；强制终止 Host 无法保证游戏清理。
官方 Moonlight 不发送 CloudGI 会话心跳，手动连接时不具备这套游戏会话生命周期。

## 构建与运行

需要 Windows 11 x64/ARM64、.NET 10 SDK。首次构建需联网还原 NuGet 依赖和 Sunshine。
可在 Visual Studio 中打开 `CloudGI.Host.slnx`；调试需确认管理员权限。macOS 可编辑源码并运行核心测试，WinUI 构建在 Windows 完成。

```powershell
# Parallels Windows ARM64；台式机将 ARM64 换为 x64。
dotnet build CloudGI.Host.slnx -p:Platform=ARM64 -p:RestoreLockedMode=true
Start-Process .\src\CloudGI.Host\bin\ARM64\Debug\net10.0-windows10.0.26100.0\win-arm64\CloudGI.Host.exe

# 自包含发布
dotnet publish src/CloudGI.Host/CloudGI.Host.csproj -c Release -p:Platform=ARM64 -p:RestoreLockedMode=true
```

运行需保留完整输出目录，其中包含 .NET、Windows App SDK、PRI/XAML、Sunshine 和手机 UI 组件。
目标电脑无需另装 .NET 或 Sunshine。

## 代码结构与测试

- `src/CloudGI.Host`：WinUI 页面、ViewModel、Windows 进程及串流适配。
- `src/CloudGI.Host.Core`：配置、参数合并、进程状态、启动与会话、配对及签名校验。
- `tests/CloudGI.Host.Core.Tests`：核心回归测试。
- `third-party`：固定第三方依赖、许可证及原生组件源码。
- `build`：Sunshine 获取及手机 UI 运行文件打包规则。

```sh
dotnet test tests/CloudGI.Host.Core.Tests/CloudGI.Host.Core.Tests.csproj -p:RestoreLockedMode=true
```

原生测试仅使用自建进程，保留正常回调、无回调就绪和不支持特征的失败清理三种场景；按 `NOTICE.md` 使用 `--tests` 构建。

## 阶段验收（2026-11-02）

用户已确认：手机可以正常启动游戏、进入串流，游戏显示手机 UI，能够正常触控游玩。
此前已验证自动发现与配对、默认全屏、原生多点触控、正常断开退出及客户端强退后的超时清理。

当前捕获整个显示器；音频、性能与连接恢复需分别验证。Parallels 旧测试曾出现音频采集失败，本阶段未单独重测音频。
游戏版本升级和 Windows 台式机环境仍需独立复测。
