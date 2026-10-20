# CCF Deadlines Visual Calendar

[English](README.md) | 简体中文

一款简洁美观、支持自定义的 Windows 桌面日历，方便直观地查看 CCF 会议的投稿截止时间。

[![查看 CCF Deadlines Visual Calendar 演示](assets/ccf-calendar-preview.gif)](assets/ccf-calendar-demo.mp4)

## 功能

- 📅 **可滚动日历**，悬停即可查看详情信息。
- ⏳ **实时倒计时**，可选择固定显示一个目标投稿的会议。
- 🎨 **自定义外观**，支持调整颜色、字体、窗口大小和透明度等。
- 🌍 **自选显示时区**，统一显示各会议的在所选时区下的截止时间。
- 🔄 **每日自动更新**，主站镜像自动获取无需代理，离线也可查看已缓存的数据。

## 使用方法

**下载使用：** 在本仓库的 **Releases** 页面下载 `DeadlineCalendar-win-x64.zip`，完整解压后运行 `DeadlineCalendar.exe`。

右键点击 **Windows 系统托盘**中的日历图标，可显示或隐藏日历、退出程序，也可打开**设置**，选择关注的会议、调整外观等。

**运行源码：** 下载或克隆仓库，安装下方列出的 .NET SDK，然后在项目根目录执行：

```powershell
dotnet run --project src/DeadlineCalendar.App/DeadlineCalendar.App.csproj -c Release
```

## 环境要求

- Windows 11 x64。
- 获取和更新 [CCF Deadlines](https://github.com/ccfddl/ccf-deadlines) 的会议数据需要联网，无需代理；缓存数据可离线查看。
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) 10.0.402 或更高的 10.0.x 版本（仅运行源码时需要）。
