
# TaskMonitor

**中文** | English (TODO)

从进程维度实时监测系统负载情况。

<p align="center">
  <img src="assets/header.png" alt="TaskMonitor">
</p>

## 安装 & 使用

推荐在 Windows 11 中使用，本项目打包为单文件 exe 进行分发，直接在发布页下载即可。

下载入口：[CNB(大陆友好)](https://cnb.cool/linesoft2/TaskMonitor/-/releases) | [GitHub](https://github.com/linesoft2/TaskMonitor/releases)

在下载并打开后，可以在**系统任务栏**的左侧 / 右侧（取决于任务栏设置）看到常驻的任务栏窗口，点击某个项目即可查看该项的详情和该项目的高占用进程。

## 制作背景

当有程序执行高负载任务或系统卡顿时，我们经常需要查看系统的负载情况，同时，我们也希望定位到具体哪个程序/进程在执行高负载任务， TaskMonitor 由此诞生，它不仅可以常驻任务栏实时展示共五个维度的实时负载，在点击某个项目后，还能查看每个项目的详情以及各进程的负载情况。

## 特色

- 单文件，接近 native 级渲染，极低资源占用。
- 仅依赖现代 Windows 内置的 .NET Framework，无需安装第三方 runtime。
- Fluent2 (WinUI) 设计风格，贴近系统原生UI，界面现代美观。
- 与 Windows 任务管理器一致的采样方式和计算逻辑，确保数值准确。
- 支持与 Clash / Mihomo 集成，可采集经代理后的进程网速数据。
- 纯 Win32 接口实现，无驱动、Ring0 等内核级采集方式，不影响系统稳定性。
- 完全免费，非盈利性，开放源代码。

## 功能

- CPU：支持CPU利用率，速度，进程数，线程数，句柄数，运行时间和每个进程利用率的采集展示。
- 内存：支持内存占用率，内存占用详情，已提交，已缓存，分页池，非分页池和每个进程占用内存数值的采集和展示。
- 磁盘：支持按物理磁盘分别采集，支持磁盘占用率，磁盘名称，磁盘类型，利用率，读写速度，响应时间和每个进程的读写速度的采集和展示。
- GPU：支持多个 GPU 分别采集，支持 GPU 占用率，GPU 名称，利用率，温度，负载类型，显存占用情况以及每个进程的负载类型和占用率的采集和展示。
- 网络：支持按网卡分别采集，支持网卡实时速率，网络类型，SSID，WiFi 标准，协商速率，本机 IPv4，公网 IPv4 / IPv6，本地延迟，公网延迟和每个进程的实时网速的采集和展示；支持与 Clash / Mihomo 集成，从而在进程列表展示经过代理后的实时速率。
- 其他：支持深色 / 浅色模式，支持触控板、触摸屏，支持设置采样间隔，支持在进程列表合并相同程序等

## 限制

- 由于部分接口需要 UAC 权限，本工具将始终申请 UAC ，后续可能会对此进行优化。
- 由于本项目使用了多个未文档化的 API，可能存在较多兼容性问题，当前在 Windows 11 25H2 运行良好，经初步测试，在 Windows 10 22H2 系统下存在兼容性问题，后续将逐步优化，建议在 Windows 11 上使用本项目。
- 公网IP的采集会向第三方网站发起请求，可在设置关闭此功能。

## TODO

- [ ] i18n
- [ ] 无障碍
- [ ] Windows 10 兼容
- [ ] 悬浮模式（适用于隐藏任务栏的情况）
- [ ] 副屏支持
- [ ] ARM64 支持

## 参考 / 致谢 / 开源许可

- [TrafficMonitor](https://github.com/zhongyang219/TrafficMonitor) ([License](https://github.com/zhongyang219/TrafficMonitor/blob/master/LICENSE))
- Windows 任务管理器
- [iNKORE.UI.WPF.Modern](https://github.com/iNKORE-NET/UI.WPF.Modern) ([License](https://github.com/iNKORE-NET/UI.WPF.Modern/blob/main/LICENSE.md))
- [FluentWpfCore](https://github.com/TwilightLemon/FluentWpfCore) ([License](https://github.com/TwilightLemon/FluentWpfCore/blob/master/LICENSE.txt))
- [YamlDotNet](https://github.com/aaubry/YamlDotNet) ([License](https://github.com/aaubry/YamlDotNet/blob/master/LICENSE.txt))

## 版权信息

Copyright © 2026 双霖 (LineSoft).

请在 [Apache License 2.0](LICENSE) 的许可下使用。
