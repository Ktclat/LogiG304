# LogiG304

Windows 上的独立 G304 / G305 本地控制工具。通过 Windows 自带 HID 接口直接访问 USB 接收器，**不使用 G HUB 核心，不要求预装 G HUB，不包含官方驱动或个人配置**。

## 下载与运行

在 [Releases](https://github.com/Ktclat/LogiG304/releases) 下载 `v0.1.0-preview.1`：

- `LogiG304-Setup.exe`：选择空文件夹安装，例如 `D:\Apps\LogiG304`。
- `LogiG304-0.1.0-preview.1-win-x64.zip`：解压后直接运行 `LogiG304.exe`。
- `SHA256SUMS.txt`：核对下载文件的 SHA-256。

要求 Windows 10/11 x64 和 .NET Framework 4.8。无 NuGet 或其他运行库下载步骤；系统缺少 .NET Framework 4.8 时需自行安装该系统组件。发行包未做代码签名。

安装、移动和卸载都只涉及本软件文件夹。没有服务、计划任务、自启动、C 盘兼容联接或目录权限修改；也没有登录、遥测、联网更新和防火墙配置。安装器不会启动 G HUB、停止进程或打开鼠标。

选择当前用户有写入权限的空文件夹即可。若旧安装器保护了某个父目录，本安装器不会修改其权限，应选择另一个可写位置。

## 功能与验证范围

| 功能 | 版本状态 |
| --- | --- |
| 识别 G304/G305 接收器及实际型号 | 已在一只 G304 上验证 |
| 读取 DPI 范围、当前 DPI、回报率、电量估值、控制模式 | 已实机验证；刷新不改设置 |
| 软件模式下调整当前 DPI / 回报率 | 已实现参数检查、回读及模拟测试；尚未实机试写 |
| 显式切换软件 / 板载模式 | 已实现回读及模拟测试；尚未实机试写 |
| 切换已有板载 DPI 档位 | 已实现模拟测试；仅识别支持的板载格式 |
| 编辑板载配置、持久化 DPI 档位、按键重映射、宏、游戏自动切换 | **未实现** |

**这是预览版，尚未完整替代 G HUB。** 为保留现有用户的安装和使用状态，实机检查只发送读取命令，未试写鼠标设置，未停止现有 G HUB。模拟测试不代表实机写入成功，也尚未验证所有固件和无 G HUB 的全新 Windows 系统。

软件模式设置不保证关机/拔插后保存。切换模式会改变使用的设备配置，可能立即影响 DPI 和按键行为。程序不写设备闪存或固件，不自动保存或恢复设置。每个写入操作都需要用户点击并确认。

## 与 G HUB 共存

不读取或修改 G HUB 的数据库、目录、服务、驱动、注册表及防火墙。检测到 G HUB、Logitech Gaming Software 或 Onboard Memory Manager 运行时，会阻止设置写入，保留只读查看。自行退出相应程序后刷新才能操作；本程序不会代为结束进程。

进程检查只是尽力保护，不是全局锁，请避免同时运行其他鼠标控制程序。G HUB 再次接管后可能重新应用自己的设置。

暂时只支持一个 `046D:C53F` 原装接收器，并进一步检查返回的 G304/G305 型号。鼠标休眠时请移动唤醒再刷新；连接多个接收器会拒绝操作。

## 构建

使用系统自带 .NET Framework 编译器，不需要 SDK、NuGet 或联网：

```powershell
./scripts/build-standalone.ps1 -Test -Package
```

输出在 `artifacts/standalone/`。测试使用模拟设备，不打开 HID。`LogiG304.exe --demo` 可查看不访问硬件的演示界面。完整实现和审核边界见 [独立版说明](docs/STANDALONE.md)。

`src/`、`frontend/` 和原 `scripts/build.ps1` 保留旧官方核心封装源码，单独编译，**不进入独立版 EXE 或下载包**。私人 G HUB 安装包没有公开发布。历史文档见 [旧方案说明](docs/LEGACY-README.md)。

旧方案的 `C:\Program Files\LGHUB` 是指向所选目录 `App` 子目录的目录联接，C 盘看到的是同一份文件。删除联接中的文件会影响真实安装。独立版不需要此入口，也不会清理它。

## 许可证

采用 [MIT](LICENSE)，允许使用、修改和再分发，须保留许可及版权声明。参考的 [libratbag](https://github.com/libratbag/libratbag) 协议实现也是 MIT，其声明见 [THIRD-PARTY.txt](standalone/THIRD-PARTY.txt)。报告结构参考 [Logitech HID++ 文档](https://github.com/Logitech/cpg-docs/tree/master/hidpp20)。

Logitech 软件、名称和商标仍属于其权利人。项目与其无隶属关系，也未获得其背书。MIT 不授予 G HUB 的再分发权。详见 [第三方范围](THIRD_PARTY.md)。
