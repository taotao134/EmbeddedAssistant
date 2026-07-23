# 嵌入式调试台协作约定

- 项目为 WPF 上位机，核心界面位于 `src/DeviceDebugStudio.App`。
- 完成界面改动后执行 `dotnet test .\DeviceDebugStudio.sln -c Release --no-restore`。
- 用户交付只使用 `ReleasePackages\嵌入式调试台-win-x64\DeviceDebugStudio.exe`。
- 使用 `Publish-OfflinePackage.ps1` 发布；发布前如有运行中的 `DeviceDebugStudio.exe`，先关闭再替换稳定包。
- 修改布局时检查窄窗口及展开/收起状态，确保边框、滚动条和相邻控件不重叠或被裁切。
