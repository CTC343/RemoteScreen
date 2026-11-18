# Windows 远程屏幕

双击本机 Windows 版本对应的 EXE。控制端接收画面并发送鼠标键盘操作，被控电脑运行接收端并决定是否共享。Win7、Win10、Win11 可以互连，两边各自按本机系统选择。

| 系统 | 你运行的控制端 | 发给对方的接收端 |
| --- | --- | --- |
| Windows 7 SP1 | `Win7-Controller.exe` | `Win7-Receiver.exe` |
| Windows 10 | `Win10-Controller.exe` | `Win10-Receiver.exe` |
| Windows 11 | `Win11-Controller.exe` | `Win11-Receiver.exe` |

六个文件都在 `EXE` 文件夹。每个 EXE 可单独复制到任意目录运行。接收端填写控制端发来的 `IP:端口` 或 `tls://主机名:端口`，选择「仅查看」或「允许鼠标键盘操作」，再点「连接并同意共享」。控制端的默认端口为 `8765`，接收前可修改。程序不包含固定服务器 IP、域名、账户、密码或项目路径。



## 运行条件

- 需要 .NET Framework 4.8 或更新的 4.x 版本。Windows 11 和 Windows 10 22H2 随系统提供。Windows 7 需要 SP1，并先安装 [.NET Framework 4.8 运行环境](https://support.microsoft.com/en-us/servicing/dotnetframework/2019/10/microsoft-net-framework-4-8-offline-installer-for-windows)。公网 TLS 连接还依赖系统的 TLS 1.2 和可信根证书。
- 跨网连接需要一个实际可达的入口。已有 Tailscale Funnel 的 Windows 10/11 控制电脑可继续使用它，控制端保留默认本机监听。接收端使用 Funnel 地址，无需安装 Tailscale。
- Win7 控制端可用于可信网络直连，也可使用另设的入口转发给它。当前 [Tailscale Windows 客户端](https://tailscale.com/docs/install/windows) 从 Windows 10 起支持。
- 只共享主屏幕，鼠标键盘操作以被控端当前登录桌面的权限执行。接收端可从窗口随时停止共享。

## 第一次使用部署步骤

1. 在新电脑装 [Tailscale](https://tailscale.com/download/windows)，登录你的账号，确认这台电脑显示 **Connected**。
<img width="2181" height="256" alt="ab1519d8c2d0af8286d053d61e784fba" src="https://github.com/user-attachments/assets/5974b38d-1227-4437-98e6-c9a63f0095d1" />
2. 打开对应系统的 `Controller.exe`，端口保留 `8765`，点「开始接收」，保持窗口打开。
3. **以管理员身份打开 PowerShell**，运行这两行。 [Tailscale Funnel 命令说明](https://tailscale.com/docs/reference/tailscale-cli/funnel)

   ```powershell
   $programDir = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion').ProgramFilesDir
   & (Join-Path $programDir 'Tailscale\tailscale.exe') funnel --bg --tls-terminated-tcp=8443 tcp://127.0.0.1:8765
   ```

5. 如果命令提示需要启用 Funnel，就打开**它这次打印的链接**，点 **Enable Funnel**。如果直接显示 `Available on the internet`，公网转发已经开启。
6. 复制输出里的 `*.ts.net:8443`，在前面加上 `tls://`，发给对方填进 `Receiver.exe`。

如果**只是对方换电脑**，发给他对应系统的 `Receiver.exe`，让他填你原来的地址就行。对方无需安装 Tailscale。

## 修改和重新编译

`Win7`、`Win10`、`Win11` 是三个独立的源码目录。任选一个目录打开即可修改。它们使用相同的通信格式和主要功能，各自的构建脚本会保留系统对应的文件名。

| 源码 | 用途 |
| --- | --- |
| `View.cs` | 控制端窗口、接收画面、发送操作 |
| `Share.cs` | 接收端窗口、同意共享、连接和重试 |
| `Desktop.cs` | 截屏、光标、鼠标键盘执行 |
| `Wire.cs` | 地址解析、TLS、通信格式 |

在装有 [.NET Framework 4.8 Developer Pack](https://dotnet.microsoft.com/download/dotnet-framework/net48) 的 Windows 电脑上，在要编译的源码目录运行 `.\Build.ps1`。两个 EXE 会生成在该目录的 `dist`。如果只编一个角色，用 `.\Build.ps1 -Target Receiver` 或 `.\Build.ps1 -Target Controller`。
