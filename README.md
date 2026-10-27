# CloudGI Host

CloudGI 的 Windows 原生 Host 起始项目，使用 C#、.NET 10 和 WinUI 3。
当前页面展示名称、版本、运行环境和界面框架。

## 开发环境

- Windows 11，x64 或 ARM64。
- .NET 10 SDK。首次构建需要联网还原 NuGet 依赖。
- 可使用 Visual Studio 的 WinUI 开发环境，也可以在 Windows PowerShell 中构建。
- macOS 用于编辑源码；WinUI 界面的构建、运行和调试在 Windows 中进行。

项目固定使用 `Microsoft.WindowsAppSDK.WinUI 2.6.4` 和
`Microsoft.Windows.SDK.BuildTools 10.0.26100.9169`。
Windows App SDK 运行时随构建输出提供；运行程序仍需要同架构的 .NET 10 运行时。

## 编译与运行

在仓库根目录打开 Windows PowerShell。

若使用本次在 Parallels 中准备的用户目录 SDK，先在当前 PowerShell 会话中设置：

```powershell
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\CloudGI\Tools\dotnet-10.0.402-arm64"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
```

Parallels Windows 11 ARM64：

```powershell
dotnet build CloudGI.Host.slnx -p:Platform=ARM64
dotnet run --project src/CloudGI.Host -p:Platform=ARM64 --no-build
```

Windows 11 x64 台式机：

```powershell
dotnet build CloudGI.Host.slnx -p:Platform=x64
dotnet run --project src/CloudGI.Host -p:Platform=x64 --no-build
```

也可在 Visual Studio 中打开 `CloudGI.Host.slnx`，选择对应架构后运行。
建议在 Windows 本地工作目录中编译，构建输出与缓存留在 Windows。

## 目录与当前范围

- `App.xaml` / `App.xaml.cs`：WinUI 应用资源和生命周期；入口由 XAML 编译器生成。
- `Views/MainWindow.xaml`：起始窗口，使用 WinUI 控件、主题资源和 `x:Bind`。
- `ViewModels/MainViewModel.cs`：独立于 UI 框架的只读环境信息。
- `app.manifest`：Windows 兼容性、普通用户权限和 DPI 声明。

这是直接启动可执行程序的 unpackaged 项目，当前未加入安装包或 MSIX 发布流程。
尚未接入游戏、Sunshine、发现、配对、控制接口或串流。
仓库只在本地初始化，未配置远程仓库。

## 当前验证记录

2026-10-28，在 Parallels Windows 11 ARM64、.NET SDK 10.0.402 中：

- Debug / ARM64 与 Debug / x64 构建均通过，零警告、零错误。
- ARM64 程序启动后创建 CloudGI Host 窗口并正常响应。
- x64 版本尚未在台式机中运行验证；窗口截图工具异常，视觉布局仍需人工核对。
