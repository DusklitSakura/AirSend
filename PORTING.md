# Rust → C# 移植说明

本文档逐模块说明 `Pabldi08/AirSend`（Rust + Tauri）如何对应到本仓库的
WinUI 3 / C# 实现，并明确哪些部分已经用自动化测试验证、哪些部分还需要真机确认。

> 说明：**功能范围、界面流程与协议行为都来自上游**（AirSend + `airplay2-rs`，GPL-3.0-or-later），
> 本仓库新写的只是 C#/.NET 的实现代码，以及把上游所用的库换成等价的自写实现
> （`mdns-sd` crate → 自写 DNS 报文解析、`wasapi` crate → 自写 WASAPI COM 互操作、
> Tauri/WebView → WinUI 3）。下表中「本仓库（C#）」列指的是**实现位置**，不是原创归属。

## 1. 模块映射

| 上游（Rust） | 本仓库（C#） | 说明 |
|---|---|---|
| `ui/`（Tauri WebView：index.html + main.ts + i18n.ts + styles.css） | `MainPage.xaml`（NavigationView 分类）、`Views/DevicesView`、`Views/PlaybackView`、`Views/SettingsView`、`ViewModels/`、`Services/Localization.cs` | 同一套界面流程与文案；`i18n.ts` 的 ES/EN 字符串逐条搬到 C# 字典，并新增中文。三个视图共享同一个 ViewModel，顶部导航按功能分类 |
| `src-tauri/src/lib.rs`（`#[tauri::command]`、托盘、持久化、日志） | `Services/AirPlayCoordinator.cs`、`Services/SettingsStore.cs`、`Services/TrayIcon.cs`、`Core/Logging/AppLog.cs` | 命令 → 方法一一对应；`tauri-plugin-store` → `%APPDATA%\AirSend\settings.json`；托盘改用 `Shell_NotifyIcon` |
| `crates/airplay-core/src/discovery.rs`（`mdns-sd` crate） | `Core/Discovery/MdnsBrowser.cs`、`AirPlayDiscovery.cs`、`DeviceGrouping.cs` | 手写 DNS-SD（不依赖第三方 mDNS 库）：UDP 组播、名称压缩、PTR/SRV/TXT/A/AAAA 解析、TTL 缓存与重扫回放 |
| `crates/airplay-core/src/probe.rs` | `Core/Probe/ManualEndpoint.cs`、`AirPlayProbe.cs` | 逐条对应，包含 IPv6 方括号端点与错误分类 |
| `crates/airplay-core/src/pairing.rs` | `Core/Pairing/AirPlayPairing.cs`、`PairingIdentity.cs` | 瞬态 pair-setup（HKP=4，PIN 3939）+ pair-verify |
| `crates/airplay-core/src/streaming.rs`（延迟校验、音泵、心跳） | `Core/Streaming/LatencyProfile.cs`、`AirPlayStreamSession.cs` | 0–3000ms/100ms 步进、10s 冷却、2s RTSP feedback 心跳 |
| `crates/audio-capture/src/windows.rs`（`wasapi` crate） | `Core/Capture/WasapiLoopbackCapture.cs` | 手写 COM 互操作 + `AUTOCONVERTPCM`，目标格式 44.1kHz/16bit/立体声 |
| `crates/audio-encode/`（ALAC） | `Core/Audio/AlacEncoder.cs`、`AlacMagicCookie.cs` | 见下文「ALAC 现状」 |
| `airplay2-rs` 的 `airplay-rtsp` | `Core/Rtsp/` | 二进制 plist 编解码、RTSP 请求/响应、HomeKit 加密通道 |
| `airplay2-rs` 的 `airplay-crypto` | `Core/Crypto/` | TLV8、HKDF-SHA512、SRP-6a、X25519、Ed25519、ChaCha20-Poly1305、AES-CBC |
| `airplay2-rs` 的 `airplay-audio`（RTP/加密） | `Core/Audio/RtpAudioSender.cs`、`AirPlayCiphers.cs` | RTP 头 + ChaCha20-Poly1305 负载 + 16B tag + 8B nonce 尾随；重传请求应答 |
| `airplay2-rs` 的 `airplay-timing`（NTP 授时） | `Core/Audio/NtpTimingServer.cs` | 应答 payload type 82 → 83 |
| `src-tauri/tauri.conf.json`（窗口 420×540） | `MainWindow.xaml.cs`（440×760 DIP） | 宽度贴近原版，因为增加了播放器卡片与日志区而加高 |

## 2. 关键协议细节（按上游实现复刻）

- **配对**：`POST /pair-setup`，`X-Apple-HKP: 4`，TLV8 M1→M4；SRP-6a 使用
  RFC 5054 3072 位群、g=5、SHA-512，且 **M1 里 H(g) 用的是未填充的生成元字节**
  （上游注释指出：用填充版本会被接收端以 `0x02` 拒绝）。
- **会话密钥**：SRP/ECDH 共享密钥经 HKDF-SHA512（salt `Control-Salt`，
  info `Control-Write-Encryption-Key` / `Control-Read-Encryption-Key`）派生出
  RTSP 双向密钥。
- **RTSP 加密通道**：配对完成后每个报文按 HomeKit 帧格式封装
  `[u16_le 长度][密文][16B tag]`，AAD 为长度字段，nonce 为 `4 字节 0 || u64_le 计数器`。
- **音频加密**：AirPlay 2 用 ChaCha20-Poly1305，nonce 为
  `[0,0,0,0, seq_le(2), 0×6]`，AAD 为 `RTP timestamp(BE) || SSRC(BE)`，
  发送尾随 `16B tag + 8B nonce`。
- **SETUP 分两阶段**：第一阶段传 `timingPort`/`timingProtocol`/`deviceID`，
  第二阶段传 `streams[0]`（`audioFormat`=ALAC magic cookie、`controlPort`、
  `ct`/`spf`/`sr`/`ssrc`/`type`=96、`shk`），随后 `RECORD`、`SET_PARAMETER /volume`
  （dB 值）与每 2s 一次的 `GET_PARAMETER` 心跳。

## 3. 验证状态

### 已用自动化测试覆盖（`tests/AirSend.Core.Tests`，53 项）

| 主题 | 依据 |
|---|---|
| X25519 | RFC 7748 §5.2 密钥协商与 §6.1 标量乘向量 |
| Ed25519 | RFC 8032 §7.1 TEST 1/2/3（签名与验签） |
| SRP-6a | 与独立 Python 实现（`work/tools/gen_srp_vector.py`）逐字节比对 K/M1/M2 |
| HKDF-SHA512 | 与 Python `hmac`/`hashlib` 参考值比对 |
| TLV8 | 分片重组、截断拒绝、M1 标志位 |
| ChaCha20-Poly1305 通道 | 帧格式、计数器推进、>1024B 分块、音频 nonce/AAD 往返 |
| AES-CBC | 尾块直通语义（与上游 `encrypt_raop` 一致） |
| bplist | 往返 + **Python `plistlib` 交叉解析**（证明与 Apple `CFPropertyList` 互通） |
| RTSP 客户端 | 明文响应、加密响应（同一套密钥的服务端桩） |
| RTSP 探测 | AirTunes 头识别、非 AirPlay 拒绝 |
| 设备归并 / 延迟 / 端点解析 | 复刻上游 Rust 单测用例 |
| ALAC | magic cookie 布局、帧往返（verbatim 模式） |

### 运行期诊断

- 日志与设置默认写到 `%APPDATA%\AirSend\`；该目录不可写时依次回退到 exe 同目录的
  `logs\`、`%TEMP%\AirSend\logs`。
- 设置环境变量 `AIRSEND_DATA_DIR=<目录>` 可以把两者整体重定向（便携安装、远程支持、
  自动化测试都靠它）。
- 应用启动时会记录界面语言与系统语言、托盘图标是否可用；托盘不可用（受限会话、
  没有资源管理器）时，关闭窗口会直接退出进程，而不是把窗口藏到一个不存在的托盘里。
- 启动阶段的异常会写到 exe 同目录的 `startup-error.log`（含内部异常链），
  因为那时日志目录可能还不可用。

### 真机验证结果（HomePod gen 2，192.168.1.17）

已经在一台真实 HomePod 上跑通了除 WASAPI 取流以外的完整链路：

```
discovered _airplay._tcp.local: 卧室 [HomePod] 192.168.1.17:7000
POST /pair-setup → 200 (M2) → 200 (M4)   pair-setup transient completado (M1-M4)
OPTIONS * (加密) → 200  (ANNOUNCE, SETUP, RECORD, PAUSE, FLUSH, TEARDOWN, ...)
SETUP (阶段 1) → 200，事件连接建立在 49173
SETUP (阶段 2) → 200，音频发往 192.168.1.17:57474
RECORD → 200
SET_PARAMETER /volume → 200
POST /feedback → 200（心跳）
发送 3 秒 440 Hz 测试音：375 个 RTP 包 × 352 帧 = 3.00 秒，TEARDOWN → 200
```

**最终结果：已出声**（人耳确认）。也就是说 SRP 配对、TLV8、RTSP 加密通道、
SETUP/RECORD、音量、心跳、SYNC 授时、ALAC 编码 + RTP 打包 + ChaCha20-Poly1305
加密，整条链路都在真实 HomePod 上跑通了。

### 静音问题的排查过程（2026-09-29）

「HomePod 点亮但没声音」最终定位到**四个叠加的缺陷**，每一个单独存在都会导致静音：

1. **SETUP 的压缩类型写成 PCM**：应该是 `ct = 2`（ALAC）+ `audioFormat = 0x40000`，
   并且 ALAC 的 magic cookie 要放在独立的 `asc` 字段；原来把 cookie 塞进了
   `audioFormat`、`ct` 写成 1，接收端于是按 PCM 解 ALAC → 静音。
2. **ALAC 帧缺 3 位元素标签**（最致命）：完整帧结构是
   `3 位元素类型(0=SCE/1=CPE) + 4 位实例标签 + 12 位保留 + 1 位 has_size +
   2 位 extra_bits + 1 位「未压缩」+ [32 位样本数] + 交错样本 + 3 位 END 标签`。
   原实现漏掉了开头的 3 位和结尾的 3 位，解码器从第 1 个 bit 就错位。
   比对依据：`alac-encoder` crate 的 `encode_stereo_escape`（上游实际使用的编码器）
   与 ffmpeg `libavcodec/alac.c` 的 `alac_decode_frame`/`decode_element`。
3. **缺 FLUSH + RTP-Info，且起始 RTP 时间戳不是 0**：上游在开播前发
   `FLUSH` 并带 `RTP-Info: seq=..;rtptime=0`，RTP 时钟从 0 开始、SSRC 用 0。
4. **缺 SYNC 包（payload type 84）**：发送端必须周期性告诉接收端
   「这个 RTP 时间戳对应这个 NTP 时间」；没有它接收端会接受会话、对完时钟，
   但永远不开始渲染。

顺带修掉的两个隐患：Windows 定时器精度只有 ~15.6 ms，按包 sleep 会让发送速率
只有实时的一半（改成按绝对时钟排程）；控制端口的 RTCP 重传请求现在会应答。

其他仍待注意的点：`GET /info` 的 `srcvers` 在 HomePod 上返回空；WASAPI loopback
在开发机上一次只能确认「已启动并持续产出帧」，是否有声音由用户人耳确认。

### mDNS 解析修正（2026-09-29）

首次真机联调时发现两个真实缺陷，均已修复并有回归测试：

1. **SRV 记录偏移错误**：SRV 的 rdata 是 `priority(2) weight(2) port(2) target-name`，
   之前从 rdata 起点开始解析目标主机名，把数字字段当成了标签长度 → 主机名和 A 记录
   匹配全部失效，界面上设备地址显示为空（点连接报 “IP 无效”）。
2. **5353 端口竞争**：Kaspersky（`avp.exe`）也在监听 UDP 5353。Windows 上同一端口的
   单播报文只会投递给其中一个套接字，导致 HomePod 发给我们的单播应答被安全软件
   抢走。现在额外用一个**临时端口套接字**发送带 QU 位的查询，应答直接回到我们的
   端口，不再受第三方 mDNS 监听器影响。

## 4. 与上游的有意差异

| 差异 | 原因 |
|---|---|
| 分发改 zip（无 MSIX、无安装包） | 用户明确要求；顺带免去开发者模式与包签名流程 |
| 缓冲区下限从 0 ms 改为 100 ms | 实测 0 ms 时 HomePod 会接受会话但不出声；下限 100 ms 后才稳定出声 |
| 连接成功后弹窗询问是否立即推流 | 连接（配对）和播放是两件事，避免连上就自动出声 |
| 音量 35% 以上二次确认 + 5% 以下悬浮提示 | 上游没有音量保护；高音量长时间播放有听力风险，因此加了确认（可勾选「本次启动不再提醒」）与低音量提醒 |
| 竖向 NavigationView + 官方 SettingsCard | 上游是单页 WebView 布局；移植版按功能分成「设备 / 播放 / 设置」，设置项用 Windows Community Toolkit 的 SettingsCard，贴近 Windows 11 设置应用 |
| 新增「开机自启动」 | 写 HKCU Run 键并带 `--minimized`，与「启动时自动连接」搭配可做到登录即静默推流 |
| 连接时读取接收端音量 | 上游只写音量；移植版在建立会话后先发 `GET_PARAMETER /`（`text/parameters`，body `volume`）读回接收端当前音量（返回 dB，换算成 0..1），读得到就沿用、不覆盖，读不到才写入本机保存的偏好 |
| 未连接时播放页只留空状态卡片 | 音量/测试音/缓冲区三张卡片只在已连接设备时出现 |
| 测试音改为「连接测试语音」 | 用 Windows SAPI（`System.Speech`）按界面语言合成一句话（无对应语音时回退英语），经 `PlayPcmAsync` 重采样到 44.1k/立体声后走同一条 ALAC+RTP 通路；合成结果按语言缓存。SAPI 不可用时退回 440 Hz 音 |
| 按钮不再用字符 emoji | 播放/停止按钮改为 Segoe Fluent 图标（E768 / E71A），文案里去掉 ▶ / ⏸ |
| 多设备播放各自开一路采集 | 上游共享一次 WASAPI 采集；本移植版每个接收器各建一路会话，功能可用但更耗 CPU、且没有做接收器时钟同步（上游同样标注为实验性） |
| 不做 FairPlay / AirPlay 1 旧路径 / PTP / 屏幕镜像 | 上游 AirSend 本身也不涉及这些；AirPlay 2 + NTP 是 HomePod 需要的组合 |
| 新增「活动日志」面板 | 应用内就能看到日志，便于无控制台时的支持排查；文件日志与上游同格式 |
| 日志/设置目录不可写时自动回落 | 受限账户与沙箱环境下依然可用 |

## 5. 已知限制

- 只支持 AirPlay 2 接收器（HomePod / Apple TV / AirPort Express 的 AirPlay 2 模式）。
- 单次会话的缓冲上限由延迟滑块决定（0–3000ms）；0ms 与上游一样标为实验性。
- 延迟修改需要 10 秒冷却；播放中的修改会重建 SETUP，与上游行为一致。
- 未实现屏幕镜像、AirPlay 1（RAOP）加密、PTP 授时、多设备时钟同步。

## 6. 集成到系统扬声器列表（可行性）

**结论：只靠这个应用做不到，必须有一个虚拟声卡驱动。**

Windows 的播放设备列表（音量弹出面板里的“选择播放设备”）只列出**音频端点
(audio endpoint)**，而端点只能由**内核模式音频驱动**注册（传统 PortCls/AVStream，或
较新的 ACX 框架）。纯用户态进程没有办法把自己的名字插进那个列表——这也是
VB-CABLE、Scream、Voicemeeter 这类工具都要装驱动的根本原因。

### 路线 A：自己写虚拟声卡驱动（可做，但成本高）

- 做法：以微软 `Sysvad`（WDK 示例，PortCls）或 ACX 模板为基础，导出一个名为
  “AirSend”的渲染端点；应用改成采集这个端点的 loopback，再转发到 AirPlay。
- 代价：需要安装 **WDK** 才能构建；驱动必须**签名**（EV 证书，或开启测试签名 +
  管理员权限，重启后才生效）；驱动程序如果写错会波及整个系统音频栈。
- 本机状态：这台机器没有安装 WDK，也没有代码签名证书，沙箱里没有管理员权限，
  所以**我无法在这里构建或验证驱动**。如果需要，我可以先把工程骨架（driver 项目、
  INF、构建脚本、自签名/测试签名步骤说明）搭出来，由你在有 WDK 的机器上完成
  构建与安装验证。

### 路线 B：用户态折中（已经实现）

1. 装一个第三方虚拟声卡（VB-CABLE / Scream / Voicemeeter）。
2. 在 Windows 里把**系统输出**切到这个虚拟声卡 —— 此时扬声器列表里出现的是那个
   虚拟声卡（这是它的驱动提供的）。
3. 在 AirSend 的「采集的音频来源」里选中这个虚拟声卡。

结果就是：任何应用的声音都会流经虚拟声卡 → AirSend 采集 → 推给 HomePod。
这条路径完全在用户态，已经在本版本里实现并可用（`WasapiDevices` 枚举播放端点，
`WasapiLoopbackCapture` 按设备 ID 打开 loopback），不需要驱动、不需要管理员权限。


### 路线 C：让 AirSend 自己“感知音频”（可选增强）

监测所选端点的电平，一旦有声音就自动连接并开始推流，静音一段时间后自动停止
（类似 TuneBlade 的 auto-connect）。同样是纯用户态实现，如果需要我可以加上。

## 6. 复现构建

```powershell
dotnet test tests/AirSend.Core.Tests/AirSend.Core.Tests.csproj
dotnet build src/AirSend.App/AirSend.App.csproj -c Release -p:Platform=x64
.\build-release.ps1 -RestoreSources <本地 NuGet 源目录>   # 仅离线时需要
```
