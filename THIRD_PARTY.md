# 第三方范围

当前项目自有代码与文档采用 AGPL-3.0-only，不为下列项目授予许可：

- Logitech G HUB 的 EXE、DLL、驱动、ASAR、图标、设备资源及配置模板。
- Microsoft Windows、.NET Framework 和 Visual C++ 运行库。
- Logitech 或其他厂商的名称与商标。

这些二进制和资源没有提交到本仓库。不得因本仓库使用 AGPL 就认为整个私人安装包或罗技核心也使用 AGPL。AGPL 是自有代码的开源许可，不是罗技的重新授权，也不证明本地改装、重新打包或公开分发已经符合罗技条款。

若将自有代码注入、链接或合并到其他程序并分发，还需核实组合方式是否满足各方许可要求；本次没有给官方组件授予许可，也没有添加专有代码链接例外。源代码仓库的许可证变更不改变未重新打包的旧私人安装包。

可选的本地补丁工具使用 npm `asar` 3.2.0（MIT）及其依赖；依赖由 npm 单独安装，未随仓库复制。以各包自身的许可证为准。

外部依据（核对日期：2026-10-25）：

- [Logitech Software License Agreement](https://www.logi.com/en-ie/tos/software-license-agreement)，第 2、3、12 条分别涉及使用限制、转让和第三方开源组件。
- [GNU AGPL v3](https://www.gnu.org/licenses/agpl-3.0.html)，尤其第 5、6、13 条。
- [SPDX AGPL-3.0-only](https://spdx.org/licenses/AGPL-3.0-only.html)。根目录 LICENSE 来自 SPDX 发布的标准全文，未添加项目专用限制。
- [OSI MIT License](https://opensource.org/license/mit)，用于历史版本及采用 MIT 的第三方依赖。

本项目无法授予第三方软件的再分发权。具体使用、修改及发布应以适用许可、书面授权和当地法律为准。
