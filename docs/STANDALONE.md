# 独立版实现与验收记录

版本：0.1.0-preview.1，2026-10-25。

## 通信与保护

`HidTransport.cs` 使用 SetupAPI 枚举 HID collection，仅打开 `046D:C53F` 接收器的 vendor-defined、20 字节输入/输出接口，不打开鼠标/键盘输入 collection，不更换驱动。

异步发送 HID++ 长报告，匹配设备索引、动态 feature index、函数号及 software ID。USB 有超时；传输失败关闭当前连接，刷新时重建。协议错误不能作为成功结果。

`G304Device.cs` 校验 ping challenge 和实际名称，再动态发现 feature index：

| Feature | 读取 | 显式设置 |
| --- | --- | --- |
| 0000 / 0005 | 握手、feature 查找、名称 | 无 |
| 1000 | 电量估值 | 无 |
| 2201 | 传感器数、DPI 能力和当前值 | 软件模式当前 DPI |
| 8060 | 回报率能力及当前间隔 | 软件模式受支持的间隔 |
| 8100 | 模式、现有板载配置头和档位 | 切换模式、选择已有档位 |

8100 的内存写入函数 6、7、8 和 DFU 功能没有实现。板载配置读取只用于展示既有 DPI 档位，不导出配置或宏。

设置前及最终写入命令前检查冲突进程。USB 请求不能与其他软件原子互斥，因此仍应避免同时控制。设置回读不符会报错，不自动重试或隐式恢复设备状态。

## 数据与安装

没有配置数据库、自动日志、网络客户端或后台组件。界面数值从鼠标读取，演示数值是合成数据。发行包不包含本机配置或诊断结果。

一个自有 EXE 嵌入 MIT、第三方声明和通用说明。安装包和便携 EXE 使用相同字节；`-Setup` 文件名打开安装页，复制为 `LogiG304.exe` 后进入控制界面。

安装器只写选定空目录的四个自有文件，不修改 ACL、注册表、服务、任务、驱动或防火墙，不提升权限。拒绝 UNC、根目录、数据流、非空目录、联接/符号链接路径、现有 G HUB 子目录。失败时只清理此次创建的文件，不递归删除目录树。

## 审核和验证

- 一只 G304 的真实只读通信通过：HID++ 4.2，200–12000 DPI、50 步进，设备状态可读取。
- 原 G HUB agent 保持运行，独立程序检测到冲突并限制写入。
- 原安装的 7 个关键文件、2 个配置数据库哈希保持一致，原服务保持运行。
- 模拟测试覆盖错误型号、握手错误、其他应用响应、无效 DPI/回报率/模式/档位、冲突保护、写入回读失败及禁止自动重试。
- 安装测试覆盖中文与空格路径、非空目录保护、已有 G HUB 保护、包路径越界拒绝、失败回退、普通目录属性。
- WinForms 演示界面已离屏渲染并检查，演示不打开 HID。
- 实际鼠标写入及无 G HUB 的全新系统尚未验收，故发布为预览版。

稳定版仍需在独立测试环境验证设置写入、拔插、休眠恢复、模式/已有档位切换、不同固件，以及无需 G HUB 驱动。这些步骤不应擅自改变用户正在使用的鼠标。

## 参考

- [Logitech HID++ 文档](https://github.com/Logitech/cpg-docs/tree/f9107a4e3190d798f9be8c5d596f255f5e5741d2/hidpp20)：报告结构、Root、software ID。
- [libratbag hidpp20.c](https://github.com/libratbag/libratbag/blob/8235b5bb6032dea15901ab58a8218956f4494d08/src/hidpp20.c)、[hidpp20.h](https://github.com/libratbag/libratbag/blob/8235b5bb6032dea15901ab58a8218956f4494d08/src/hidpp20.h)：命令、DPI 编码、回报率位图和板载格式，MIT 声明随包保留。
- [Microsoft HID 报告 API](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/sending-hid-reports)。

不调用或修改官方闭源核心，不打包其 UI、设备资源、数据库或驱动。
