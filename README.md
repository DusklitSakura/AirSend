# AirSend

**把 Windows 的声音送到 HomePod。**

系统声音由 WASAPI loopback 采集，编码成 ALAC，通过加密的 RTP 送给局域网里的 AirPlay 2
接收器。原版 [Pabldi08/AirSend](https://github.com/Pabldi08/AirSend) 是 Rust + Tauri 写的，
这里是它的 WinUI 3 / C# / .NET 10 移植版。

[![build](https://github.com/DusklitSakura/AirSend/actions/workflows/build.yml/badge.svg)](https://github.com/DusklitSakura/AirSend/actions/workflows/build.yml)
[![license: GPL-3.0-or-later](https://img.shields.io/badge/license-GPL--3.0--or--later-blue)](LICENSE)
![platform](https://img.shields.io/badge/platform-Windows%2010%201809%2B%20%C2%B7%20x64%20%2F%20x86%20%2F%20ARM64-lightgrey)

---

## 先说清楚：这是移植版

功能范围、界面流程和协议行为都照原项目来，没有另做设计 —— RTSP 的报文顺序、瞬态配对、
RTP 与 ChaCha20-Poly1305 的加密格式、ALAC 帧与 magic cookie、NTP 授时与同步包、
延迟档位、音量策略，全部以 [Pabldi08/AirSend](https://github.com/Pabldi08/AirSend)
和它用的协议栈 [airplay2-rs](https://github.com/Pabldi08/airplay2-rs) 为准
（两者均为 GPL-3.0-or-later）。

这个仓库做的只有两件事：把这套东西用 C#/.NET 重写一遍；以及把三个上游依赖换成等价的
自己实现 —— mDNS 从 `mdns-sd` crate 换成直接解析 DNS 报文，音频采集从 `wasapi` crate
换成自己写的 COM 互操作，界面从 Tauri/WebView 换成 WinUI 3。

逐模块对照见 [PORTING.md](PORTING.md)，来源声明见 [CREDITS.md](CREDITS.md)。

## 功能

原版就有的：

- **设备发现** —— 浏览 `_airplay._tcp` / `_raop._tcp`，同一台设备的两条记录会自动合并；
  路由器不转发 mDNS 时可以手动填 `IP[:端口]` 添加。
- **音频链路** —— WASAPI loopback 采集系统声音 → ALAC 编码 → ChaCha20-Poly1305 加密的
  RTP，带 NTP 授时与同步包。
- **采集来源** —— 默认跟随系统的播放设备；在 Windows 里换输出设备（比如从扬声器切到耳机）
  会自己跟着换，也可以固定成某一个设备。
- **配对** —— 瞬态 pair-setup（SRP-6a，3072 位群 / SHA-512）加 pair-verify
  （Curve25519 + Ed25519），之后的 RTSP 全程加密。
- **托盘** —— 关掉窗口不退出，缩在托盘里继续放。
- **界面语言** —— 英语和西班牙语。

这个版本新增或改动的：

- **音量以接收端为准** —— 连上以后读 HomePod 上实际的音量，不拿程序里记着的旧值去覆盖它
  （原版只写不读）。
- **听力保护** —— 音量推过 35% 先确认一次，可以勾「本次启动不再提醒」；降到 5% 以下给一个
  5 秒自行消失的悬浮提示，不挡操作。
- **连接测试语音** —— 让电脑用 Windows 语音合成念一句当前界面语言的话发给接收器，
  用来确认声音真的过去了；没有对应语音包时退回 440 Hz 提示音。
- **单实例** —— 重复启动只是把已有窗口叫到前面。
- **开机自启动** —— 可选，配合「启动时自动连接」，登录后就能直接开始推流。
- **自动更新** —— 可选启动时 / 每天 / 每周检查 GitHub 上的新版本（也可以设成完全不查）。
  发现新版本会先给你看更新内容，确认后才下载：按本机实际装的架构和「带不带运行时」挑包，
  下完再核对一遍包里的版本信息，然后退出程序、替换文件、自动重开。
- **中文界面** —— 中英西三种语言，首次启动跟随系统语言。
- **界面重写** —— 竖排导航加 WinUI 官方的 SettingsCard（原版是单个 WebView 页面）。
- **zip 分发** —— 解压后跑 `AirSend.exe` 就行，不装 MSIX、不装安装器、不用开发者模式
  （原版是 NSIS 安装包）。

## 截图

| 设备 | 播放 | 设置 |
|---|---|---|
| ![设备页](docs/screenshots/devices.png) | ![播放页](docs/screenshots/playback.png) | ![设置页](docs/screenshots/settings.png) |

## 下载

到 [Releases](../../releases/latest) 拿对应架构的 zip，解压直接运行 `AirSend.exe`。

| 包 | 要预先装什么 | 大小（参考） |
|---|---|---|
| `AirSend-<版本>-win-<架构>-selfcontained.zip` | 什么都不用装，.NET 运行时和 Windows App SDK 都在压缩包里 | x64 约 92 MB · arm64 约 89 MB · x86 约 65 MB |
| `AirSend-<版本>-win-<架构>-frameworkdependent.zip` | .NET 10 Desktop Runtime 和 Windows App SDK 2.5 运行时 | x64 约 28 MB · arm64 约 28 MB · x86 约 12 MB |

架构怎么选：普通电脑挑 **x64**，骁龙之类的 ARM 电脑挑 **arm64**，32 位系统挑 **x86**。
拿不准就用 `selfcontained` 的 `win-x64`。

## 怎么用

1. 打开程序，在**设备**页点「搜索设备」，局域网里的 AirPlay 2 接收器会列出来。
2. 点设备的「连接」——弹窗会问要不要马上开始推送。选「开始发送」就立刻把电脑声音送过去，
   选「稍后」则只保持连接。
3. **播放**页：播放/停止、音量、连接测试语音、AirPlay 缓冲区上限。
4. **设置**页：界面语言、启动时自动连接（可以指定设备）、开机自启动、检查更新、
   采集来源、日志与诊断、关于。

几个容易踩的点：

- 缓冲区上限**最低 100 ms**。再低接收器会接受会话但不出声；默认 3000 ms 稳一些。
- 音量以接收端为准。想临时确认通路，用「连接测试语音」。
- 只想抓某个播放设备（比如耳机）的声音，去**设置 → 音频 → 采集的音频来源**里选它。

## 常见问题

**搜不到 HomePod**

先看接收器和电脑在不在同一个网段（2.4G / 5G 分开的路由器尤其容易踩），然后点「重新搜索」。
路由器不转发 mDNS 的话，用**设备 → 手动添加设备**填 `IP[:端口]`。
杀毒软件也会占住 mDNS 的 5353 端口（卡巴斯基就是），这里改成用临时端口发问询来绕开。

**连上了没声音**

把缓冲区上限调到 100 ms 以上；确认 HomePod 音量不是 0；用「连接测试语音」验一下通路。
还是没声音，就去**设置 → 日志与诊断 → 显示日志**看 RTSP / SETUP 的记录。

**能让 AirSend 出现在 Windows 的扬声器列表里吗**

不能。Windows 的播放设备列表只认内核音频驱动建出来的端点，普通应用加不进去
（USB/IP 也不行 —— `usbipd-win` 只转发物理设备，`usbip-win2` 要求对端设备自己实现客户端）。
现实做法是装个签过名的虚拟声卡（VB-CABLE、Scream、Voicemeeter 都行），
在**设置 → 音频 → 采集的音频来源**里选中它，再把系统输出切过去。

**日志和数据存在哪**

默认在 `%APPDATA%\AirSend\`：`settings.json`，以及 `logs\app.YYYY-MM-DD.log`（每天一个文件）。
目录写不进去时会退回 exe 同目录的 `logs\`。设环境变量 `AIRSEND_DATA_DIR` 可以整体挪到别处。

## 从源码构建

需要 Windows 10 1809 或更新，以及 .NET SDK 10.0.100 以上（Visual Studio 2022 / VS Code 可选）。
不用装 Windows App SDK 运行时，也不用开开发者模式。

```powershell
dotnet build AirSend.slnx -c Release                                   # 编译
dotnet test tests/AirSend.Core.Tests/AirSend.Core.Tests.csproj         # 单元测试
dotnet run --project src/AirSend.App/AirSend.App.csproj                # 直接运行
.\build-release.ps1                                                    # 打 zip
.\build-release.ps1 -RuntimeIdentifier win-arm64 -FrameworkDependent   # 换架构 / 不带运行时
```

### 自动构建

[`.github/workflows/build.yml`](.github/workflows/build.yml) 在 push 和 PR 时跑测试，
然后用矩阵编出 6 个 zip（带不带运行时 × x86 / x64 / arm64）作为 Actions 工件；
打 `v*` 标签还会自动建 Release 并把这 6 个包挂上去。

## 目录结构

```
src/AirSend.App           WinUI 3 界面：设备 / 播放 / 设置三个页面、ViewModel、设置存储、
                          托盘、单实例、本地化、语音测试
src/AirSend.Core          协议与音频核心：mDNS 发现、RTSP 与二进制 plist、SRP-6a、
                          X25519 / Ed25519、ALAC 编码、ChaCha20-Poly1305、RTP、
                          NTP 授时、WASAPI 采集
tests/AirSend.Core.Tests  135 项单元测试
docs/screenshots          界面截图
PORTING.md                与上游 Rust 版本的逐模块对照、验证记录、已知限制
build-release.ps1         打 zip 的脚本
```

## 验证到什么程度

`dotnet test` 的 135 项测试覆盖：X25519（RFC 7748）、Ed25519（RFC 8032）、SRP-6a
（与一份独立的 Python 实现逐字节比对）、HKDF-SHA512、TLV8、ChaCha20-Poly1305 通道、
二进制 plist（用 Python `plistlib` 交叉验证互通性）、RTSP 客户端（含加密响应）、
mDNS 报文解析（PTR / SRV / TXT / A）、设备归并、延迟映射、ALAC 帧往返（编码器对比
一个按 ffmpeg `libavcodec/alac.c` 的解码流程重写、只用于测试的解码器），以及对着一个
脚本化的模拟接收器跑通的完整握手、系统输出设备切换的检测，以及更新检查的版本比较、
检查周期、按架构 / 运行时 / 版本挑选安装包、下错包时的校验拒绝、下载中断后续传。

真机这边（HomePod gen 2）验过：设备发现、瞬态配对、加密 RTSP、SETUP / RECORD、
音量读回、NTP 授时、能听见的声音、连接测试语音。细节和排查过程都记在
[PORTING.md](PORTING.md)。

## 许可与致谢

这是 [Pabldi08/AirSend](https://github.com/Pabldi08/AirSend) 的 WinUI 3 / C# 移植，
协议行为按它用的 [airplay2-rs](https://github.com/Pabldi08/airplay2-rs) 复刻。
原项目和协议栈都是 **GPL-3.0-or-later**，所以这个移植同样以
[GNU GPL-3.0-or-later](LICENSE) 发布，`LICENSE` 就是原项目的许可证原文。

来源声明在 [CREDITS.md](CREDITS.md)。

---

## English

A WinUI 3 / C# / .NET 10 port of [Pabldi08/AirSend](https://github.com/Pabldi08/AirSend).
It captures Windows system audio through WASAPI loopback and streams it to an AirPlay 2
receiver (HomePod) as ALAC over encrypted RTP, with mDNS discovery, transient SRP pairing,
NTP timing and sync packets, receiver volume read-back, a spoken connection test in the UI
language, tray plus close-to-tray, single instance and start-with-Windows.

- **Download** a zip from [Releases](../../releases/latest) and run `AirSend.exe`. The
  `selfcontained` package needs nothing preinstalled; `frameworkdependent` needs the .NET 10
  Desktop Runtime and the Windows App SDK 2.5 runtime. No MSIX, no installer, no Developer Mode.
- **Build** with `dotnet build AirSend.slnx -c Release`, test with `dotnet test`, package with
  `.\build-release.ps1`. CI produces six zips (with/without runtime × x86/x64/arm64) and attaches
  them to a release for any `v*` tag.
- **Verified** on a real HomePod gen 2: discovery, pairing, SETUP/RECORD, audible audio, volume
  read-back and the spoken connection test. Details in [PORTING.md](PORTING.md).
- **Updates**: AirSend can check GitHub Releases at startup, daily or weekly (or never), shows the
  release notes first, then downloads the package matching this installation and restarts to
  install it.
- **License**: GPL-3.0-or-later, same as the upstream project. See [CREDITS.md](CREDITS.md).
