# 嵌入式调试台

面向 Windows 的串口、TCP、UDP、BLE 与常用嵌入式协议调试工作台。

## 构建

项目使用系统安装的 .NET 10 SDK（`C:\Program Files\dotnet`）：

```powershell
dotnet build .\DeviceDebugStudio.sln -c Debug
```

## 发布

```powershell
.\Publish-OfflinePackage.ps1
```

唯一用户启动目录：

```text
ReleasePackages\嵌入式调试台-win-x64\DeviceDebugStudio.exe
```

## GitHub 在线更新

发布前在 `src/DeviceDebugStudio.App/DeviceDebugStudio.App.csproj` 更新 `<Version>`、`<AssemblyVersion>` 和 `<FileVersion>`，再运行发布脚本。脚本会生成：

- `ReleasePackages\嵌入式调试台-win-x64.zip`
- `ReleasePackages\嵌入式调试台-win-x64.zip.sha256`

在 GitHub 创建稳定 Release，标签使用 `v主版本.次版本.修订版本`，上传上述 ZIP 和 `.sha256` 文件。程序设置页填写 `owner/repository`，并打开“启动时检查”；程序显示主窗口后会在后台检查 `releases/latest`，发现新版本后提示下载、校验并重启。Release 资产应保留 `win-x64` ZIP，更新服务会优先选择该资产。

运行设置保存到 `%LocalAppData%\DeviceDebugStudio`，会话捕获默认保存到 `%USERPROFILE%\Documents\DeviceDebugStudio`。
