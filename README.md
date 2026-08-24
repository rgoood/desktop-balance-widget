# DesktopWidget 桌面余额小组件

一个 Windows 桌面小组件（WPF, .NET 8），用于监控：

- **OpenRouter 账户余额**（剩余 / 总额 / 已用）
- **机场流量**（剩余 / 总量、用量进度条、到期时间）

数据默认每 **2 小时**自动刷新（间隔可配置），也支持手动刷新。

## 功能特性

- 悬浮窗模式：可拖动、置顶切换、最小化到系统托盘
- **桌面嵌入模式**：点击标题栏的显示器图标，小组件嵌入桌面壁纸层，像桌面小工具一样，不遮挡任何窗口
- 机场流量通过机场官方后台 API 获取（猫猫云等 V2Board/xboard 面板），无需订阅开关、无需浏览器，自动登录并实时拉取
- OpenRouter 余额通过官方 API `/api/v1/credits` 查询

## 使用方法

1. 下载 Release 中的 `DesktopWidget.exe`（或自行编译）
2. 启动后点击面板上的 **齿轮按钮**，填入：
   - OpenRouter API Key（在 [openrouter.ai/keys](https://openrouter.ai/keys) 创建）
   - 机场 API 地址（机场官网域名，如 `https://app.example.com`）
   - 机场账号 / 密码（用于自动登录后台 API 拉取流量）
3. 完成。配置保存在 `%APPDATA%\DesktopWidget\config.json`，**不会**随程序或仓库分发

## 从源码编译

需要 .NET 8 SDK：

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

产物位于 `bin/Release/net8.0-windows/win-x64/publish/DesktopWidget.exe`。

## 隐私说明

本项目不收集任何数据。API Key、机场账号密码仅保存在本机 `%APPDATA%\DesktopWidget\config.json`，仅用于向 OpenRouter 和你的机场官方接口发起请求，不会随代码或仓库上传。
