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

运行设置保存到 `%LocalAppData%\DeviceDebugStudio`，会话捕获默认保存到 `%USERPROFILE%\Documents\DeviceDebugStudio`。
