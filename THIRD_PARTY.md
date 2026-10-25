# 第三方范围

独立版 `standalone/` 参考 MIT 许可的 libratbag HID++ 协议实现，版权和许可全文见 [standalone/THIRD-PARTY.txt](standalone/THIRD-PARTY.txt)，也嵌入并附带在独立版下载包中。下列官方核心范围仅与旧封装方案有关；独立版不包含或下载这些资源。

MIT 仅覆盖本仓库由项目作者编写的代码与文档，不为下列项目授予许可：

- Logitech G HUB 的 EXE、DLL、驱动、ASAR、图标、设备资源及配置模板。
- Microsoft Windows、.NET Framework 和 Visual C++ 运行库。
- Logitech 或其他厂商的名称与商标。

这些二进制和资源没有提交到本仓库。不得因本仓库使用 MIT 就认为整个私人安装包也使用 MIT。

可选的本地补丁工具使用 npm `asar` 3.2.0（MIT）及其依赖；依赖由 npm 单独安装，未随仓库复制。以各包自身的许可证为准。

外部依据（核对日期：2026-10-25）：

- [Logitech Software License Agreement](https://www.logi.com/en-ie/tos/software-license-agreement)，第 2、3、12 条分别涉及使用限制、转让和第三方开源组件。
- [OSI MIT License](https://opensource.org/license/mit)。

本项目无法授予第三方软件的再分发权。具体使用、修改及发布应以适用许可、书面授权和当地法律为准。
