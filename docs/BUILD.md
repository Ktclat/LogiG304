# 构建边界与步骤

## 无第三方资源的源码构建

在 Windows x64 上安装 .NET Framework 4.8 后，在仓库根目录执行：

```powershell
./scripts/build.ps1 -Test
node ./frontend/test-offline.cjs
python -m unittest discover -s tests -p "test_*.py"
python ./scripts/audit_public.py
```

不会下载软件、安装服务、安装驱动或改动现有 G HUB。Windows 回归测试会在 `artifacts/` 中暂时创建测试目录、目录联接和 ACL 测试数据，然后清理自己的测试目录。

编译器的临时文件也放在 `artifacts/compiler-temp/`，脚本完成后恢复原来的 TEMP/TMP 环境变量。

输出包含：

- `Setup.Development.exe`：无嵌入资源的安装器测试入口。
- `G HUB Offline.exe`：需要已正确部署资源和系统登记的日常入口。
- `网络设置.exe`：网络模式切换入口。
- `卸载 G HUB 本地版.exe`：解除本工具安装的入口。

不要直接用这些开发产物替换正在使用的安装目录文件。

## 可选的授权资源构建

只有另行持有适用授权、已经完成隐私审查的资源 ZIP 才能作为输入：

```powershell
./scripts/build.ps1 -PayloadZip /path/to/authorized-resource.zip
```

这是开发者接口，不是取得罗技软件或再分发许可的方式。仓库不提供该 ZIP，也没有隐藏下载入口。

ZIP 必须包含 `payload-manifest.json`，字段 `version` 为 `2025.9.814156`，`files` 是从 ZIP 相对路径到 SHA-256 的映射。
安装器会将资源清单摘要写进构建副本，并逐个检查嵌入资源。打包工具应使用本次编译的三个辅助 EXE 与配置文件，不应混入旧版工具。

必要布局：

```text
App/                             授权的官方核心、依赖与离线界面
app.offline.asar                  与版本固定哈希一致的离线界面
backup/app.original.asar          与版本固定哈希一致的原版界面
Seed/System/depots/               授权的设备资源与官方驱动安装组件
Seed/System/current.template.json
Seed/System/installation.template.json
G HUB Offline.exe
G HUB Offline.exe.config
网络设置.exe
网络设置.exe.config
卸载 G HUB 本地版.exe
卸载 G HUB 本地版.exe.config
```

配置模板中的 `${ROOT}`、`${PROGRAMDATA}`、`${LOCALAPPDATA}` 在安装时按目标电脑环境展开。
此接口拒绝本机数据库、个人配置、历史日志、机器状态文件和模板中的个人路径/SID。
现有私人验证包含有自己的配置快照，因此**不能直接作为这个公开构建接口的输入**。

资源校验通过只证明结构与哈希一致，不证明再分发授权、无敏感内容、第三方安全性或新电脑上安装成功。
去除个人配置后的首次运行和全新 Windows 安装仍须单独验证；不得用私人电脑的成功记录代替这一步。

## 可选的界面构建复现

`frontend/offline-mode.cjs` 是完整的自有策略源码。对另行授权的本地原始 ASAR，可用以下开发工具复现固定版本补丁：

```powershell
npm ci --ignore-scripts
node scripts/patch-asar.cjs /path/to/original.asar ./artifacts/app.offline.asar
```

首次准备 npm 依赖可能联网；准备好依赖后，补丁构建本身不联网。输入与输出均校验固定 SHA-256，原文件不修改，输出不能覆盖已有文件。
工具比对每个未修改的原始资源，且不会把生成的第三方资源提交进 Git。此命令不授予修改或再分发第三方软件的权利。
