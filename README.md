# AI 代理启动器

适用于 Windows 10 和 Windows 11。从 [Releases](https://github.com/16bd/ai-proxy-launcher/releases/latest) 下载 `AIProxyLauncher-Desktop.exe`，双击即可使用。

## 首次使用

1. 在 v2rayN、Clash 等代理客户端中选择可用节点，保持客户端运行，并关闭 TUN。
2. 打开启动器。自动识别会优先验证 Windows 曾经使用的本地代理，再检查常见的 HTTP 或混合端口。启动器也会依次检查 `10808`、`7890`、`7897` 和 `10809`。
3. 未识别到代理，或需要换一个地址时，在「共享代理」填写客户端的 HTTP 或混合端口，例如 `127.0.0.1:7890` 或 `http://127.0.0.1:7890`。清空地址后，下次测试或启动会重新自动识别。
4. 点击「测试连接」检查三个服务的 HTTPS 探测地址，然后启动所需的桌面应用。服务探测通过后，可以在应用内继续完成登录。

## 桌面应用

- **Antigravity** 会在常见安装位置查找桌面程序。
- **ChatGPT** 会读取 Windows 的软件包登记并查找实际的 `ChatGPT.exe`，也会检查常见的独立安装路径。微软商店版由 Windows 激活，保留软件包身份。
- **Claude** 会查找 Windows 软件包和常见安装路径。安装桌面版后，可点击「测试连接」刷新检测。

## Antigravity 中文

先退出 Antigravity，再点击「安装中文」。启动器通过当前共享代理查询[上游最新正式版](https://github.com/Ace2025001/antigravity-desktop-zhcn/releases/latest)。新版本从 GitHub 下载并按发布信息的 SHA-256 校验，已经缓存的同版本也会重新校验。汉化使用离线词库，关闭联网补译、对话标题翻译和后台更新。安装通过校验后，启动器通过同一代理打开 Antigravity。

GitHub API 拒绝查询时，启动器会通过官方发布页确认已知版本仍是最新，并使用内置的 SHA-256 校验。下载被拒时，错误提示会显示失败的域名。

退出 Antigravity 后可点击「恢复英文」。恢复会用安装时的汉化组件核对当前应用资源和原版备份。新版组件的结构不兼容时，启动器会停止，并保留已安装的汉化。

汉化组件来自 [Antigravity Desktop ZHCN](https://github.com/Ace2025001/antigravity-desktop-zhcn)，采用 MIT 许可，版权和许可声明随原始汉化包保留。

切换代理时，请从托盘彻底退出已经打开的桌面应用，再从启动器打开。桌面应用若把登录交给默认浏览器，该浏览器也需要能访问登录站点。

启动器使用现有客户端的 HTTP 或混合端口。Windows 软件包接收代理启动参数，普通桌面进程同时接收代理环境变量。

## 从源码构建

在源码目录中运行以下 PowerShell 命令。编译使用 Windows 自带的 .NET Framework，输出同目录的 EXE。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1
```
