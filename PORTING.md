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

### 已用自动化测试覆盖（`tests/AirSend.Core.Tests`，140 项）

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
| 系统输出设备切换 | 默认端点变化只上报一次、设备消失上报为 `null`、读失败不算切换、轮询能自行发现变化；另有一项对着真实 MMDevice API 读默认端点，确认手写 COM 声明可用 |
| 更新检查 | 版本号比较（含预发布版）、检查周期（启动时 / 每天 / 每周 / 不查）、按架构 + 运行时 + 版本挑包、拒绝别的版本或别的架构的包、老包用 PE 头与运行时文件推断、下载被掐断后按 Range 续传、服务端忽略 Range 时从头再来 |
| 播放队列排空 | 队列空时立即返回；队列始终不空时按超时返回（测试音不会再把界面锁死） |
| 语言目录 | 每条消息三种语言都非空、按 `AppText.Language` 取对应文案、未知键原样显示；同一异常切换语言后文案跟着变；另有一项扫描 `src` 下所有 `log.` / `error.` / `name.` 字面量，确保键一定在目录里（防拼错） |
| 更新替换 | 复制包内全部文件并新建子目录、保留安装目录里多余的旧文件、目标被占用时重试后如实报告失败、等待旧进程退出、超时兜底、缺包时报错、`update-report.txt` 内容正确 |

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

### 切换系统输出设备后不出声（2026-09-29）

现象：播放过程中在 Windows 里把输出设备从 A 换到 B，HomePod 还在放，但内容变成
静音；在「采集的音频来源」里手动选 B 也没有任何反应。

两个独立的原因：

1. **WASAPI loopback 客户端绑定在打开时的端点上**。`IAudioClient::Initialize` 之后，
   采集流一直读的是那一个设备；系统默认设备换成 B 之后，A 上没有音频在渲染，
   loopback 只会持续交付静音帧（`AUDCLNT_BUFFERFLAGS_SILENT`），我们照旧把它们
   （零填充）编码发送 —— 所以接收端仍然是「正在播放」，只是没有声音。
   代码里也没有任何 `IMMNotificationClient` 注册，谁都不知道默认设备变了。
2. **设置里的选择只写进了 settings.json**。`OnSelectedCaptureIndexChanged` 把新的
   端点 ID 存下来就结束了，正在跑的 `AirPlayStreamSession` 早就用旧 ID 建好了采集
   实例，要等下一次「播放」才会重新读取，所以当场看起来「没反应」。

修复：

- 新增 `Core/Capture/AudioEndpointWatcher.cs`，每秒读一次
  `IMMDeviceEnumerator::GetDefaultAudioEndpoint(eRender, eConsole)`，
  端点 ID 变化时抛事件（含「变成没有输出设备」的情况）。
- `AirPlayStreamSession` 增加 `RestartCapture(deviceId)`：只重建 WASAPI 采集，
  不动 RTSP 会话，接收端靠自己的缓冲把这几毫秒盖过去；旧实例先停再开新实例，
  期间不会两路声音混在一起。重建在后台线程完成（停止采集最多会 join 2 秒，
  不能卡住调用方），连续多次切换只保留最后一次。
- `AirPlayCoordinator` 订阅该事件：**跟随系统默认**的会话重新指向新端点；
  用户手动改「采集的音频来源」时，正在播放的会话立即重建，不必等到下次播放。

为什么用轮询而不是 `IMMNotificationClient`：通知接口需要把托管类以 COM 可调用对象
的形式暴露出去，回调的 vtable 由音频服务的线程直接调用——签名写错或抛异常会直接
带走整个进程，而且这种回调没法在测试里覆盖。一次 `GetDefaultAudioEndpoint` 调用
每秒一次，代价可以忽略，切换被发现的延迟最多 1 秒，人手切换设备感知不到差别。

### 自动更新（2026-09-29）

上游没有更新机制（Rust 版只发布一个 NSIS 安装包）。移植版加了一条「检查 → 提示 → 下载 →
自替换」的链路：

- **检查**：`GET https://api.github.com/repos/DusklitSakura/AirSend/releases/latest`
  （带 `User-Agent`；未认证请求每小时 60 次，远高于每天或每周一次的需要）。版本比较只认
  数字段，以及「正式版高于自己的预发布版」；`/latest` 本身也不会返回草稿和预发布版。
  周期由设置 `update_check` 决定：启动时（每次启动一次）/ 每天 / 每周 / 不查，
  上次成功检查的时间记在 `update_last_check`。自动检查失败不打扰用户，只有手动点
  「立即检查」才会提示成功/失败。
- **提示**：发现新版本才弹对话框，内容是 release notes（去掉 Markdown 记号）、当前版本、
  本机安装标识和发布页链接；按钮为「下载并更新」与「以后再说」，不按主按钮就什么都不做。
- **选包（重点）**：每个包在构建时把身份写进 `build-info.txt`
  （`version=` / `runtime=` / `flavor=`，见 `AirSend.App.csproj` 的 `WriteBuildInfo` 目标），
  更新器据此挑资产，要求文件名同时满足 `-{版本}-` 与 `-{rid}-{flavor}.zip`。
  找不到完全匹配的包就**不下载任何东西**，提示用户去发布页自己取。
  0.2.0 及更早的包没有这个文件，此时退回可观测特征：进程架构 + 是否存在 `coreclr.dll`。
  解包之后还会再校验一次：包里有 `build-info.txt` 就逐项比对（架构 / 口味 / 版本），
  没有就读 `AirSend.exe` 的 PE 头确认架构、按有没有 `coreclr.dll` 确认带不带运行时、
  并比对文件版本。任一不符就报错退出，不会去覆盖。
- **替换**：解压到 `%TEMP%\AirSend-update-<版本>-<随机>`，然后启动**刚解压出来的那份
  `AirSend.exe`**：`AirSend.exe --apply-update --source <暂存目录> --target <安装目录>
  --wait-pid <旧进程>`（见 `Program.cs` 与 `Core/Updates/UpdateApplier.cs`）。
  它等旧进程退出 → 递归覆盖安装目录 → 把结果写进 `update-report.txt` → 重新启动应用。
  Windows 不允许覆盖正在运行的 exe 和已加载的 DLL，所以必须换一个进程来做；
  用自己而不是脚本，是为了避开 shell：参数走 `ArgumentList`（由运行时转义），
  复制走 .NET API 并对被短暂占用的文件重试 4 次，失败原因能原样带出来。
  覆盖是增量的，因此只允许换成**同一种口味**的包（带运行时的不会换成不带运行时的，
  反之亦然，否则会留下多余或缺失的文件）。临时目录里超过一天的残留会在下次启动时清掉
  （正在使用的那份刚创建，不会被误删）。
- **结果回执**：`update-report.txt` 写在安装目录里，下次启动时应用读取并提示
  「已更新到 {版本}」或「上次更新没能替换文件（原因）」，读完即删。否则更新失败是
  完全静默的——第一次的真实故障就是这么被用户发现的：窗口一闪，什么都没发生。
- **下载**：用资产列表里的 `browser_download_url` 流式写盘并上报进度；连接被掐断时按
  `Range: bytes=N-` 续传，最多尝试 4 次，服务端不支持 Range（回 200）时从头再来。
  这条不是理论：实测国内网络下载 90 MB 的包常在半路被重置。

### 自动更新的第一次真实故障（2026-09-30）

现象：在 0.2.1 里点「下载并更新」，包完整下载并解压（`%TEMP%\AirSend-update-0.2.2-…\files`
里 65 个文件都在），但更新没生效；临时目录里那个 `apply-update.cmd` 双击只闪一下就没了。

原因：生成的批处理把安装目录写成 `set "AIRSEND_DST=D:\Software\AirSend\"`。
`AppContext.BaseDirectory` 永远以反斜杠结尾，而 cmd 在带引号的参数里把结尾的 `\"` 当成
「被转义的引号」，robocopy 于是拿到一个坏路径，以 8 以上的退出码失败；脚本 `exit /b 1`
退出，窗口一闪而过，没有任何提示。

修复（0.2.3）：不再生成批处理，改由**刚解压出来的那份 AirSend 自己**完成替换——
`AirSend.exe --apply-update …`，入口在 `Program.cs`，先于单实例与 XAML 初始化处理这个开关。
同时：

- 参数用 `ProcessStartInfo.ArgumentList` 传，转义交给运行时，不再手写引号；
- 等旧进程最多 60 秒（原来无上限），单个文件被短暂占用时整轮重试 4 次；
- 结束后写 `update-report.txt`，下次启动读取并提示成功或失败原因，读完即删；
- 更新器自身也用当前界面语言写日志（复用同一套日志与文案目录）。

验证：单元测试覆盖复制全部文件、新建子目录、被占用文件失败、等待旧进程、超时兜底、缺包
报错与报告内容；另外用真实构建做了端到端：假安装目录 + 假旧进程 → 复制 537 个文件 →
报告 `result=ok` 并自动重启；再把目标 exe 锁住跑一次 → 报告 `result=failed` 并写明是哪个
文件被占用。

### 测试音之后所有按钮变灰（2026-09-30）

现象：连接 HomePod 放测试音之后，音频断断续续；随后测试音按钮、停止、断开全部变灰，
退不出来。日志里泵的统计中断，之后再没有任何输出。

三个问题叠在一起：

1. **RTP 的 UDP 发送会无限期阻塞**。`RtpAudioSender` 用阻塞式 `Socket.Send` 且没设
   `SendTimeout`：网络一卡（Wi-Fi 抖动、安全软件检查流量），发送缓冲区满后 `Send`
   就不返回 → 泵线程停住 → 队列再也排不空。
2. **测试音结尾有一个无上限的等待**。`PlayPcmAsync` 正常发完音频后会
   `while (_audioQueue.Count > 0) await Task.Delay(20)`，本意是等队列排空再返回。但采集
   线程在用户放音乐时会一直往里塞，只要泵停住，这个循环就永远不退出 → 调用永不返回。
3. **界面因此锁死**。`PlayTestToneAsync` 前半段（合成语音）没有 try/finally，`IsBusy`
   卡在 true；而测试音、播放/停止、每台设备的按钮（含断开）都由它控制，于是整窗变灰。

修复：

- RTP 套接字和流的控制套接字都设 `SendTimeout = 100 ms`（`ConfigureSocket`），超时即
  丢包并限频告警（RTP 允许丢包，接收端会用 PT=85 请求重传）；重传响应那次发送也补了
  try/catch，不再可能把控制循环带停。
- `PlayPcmAsync` 的排空等待加 5 秒上限（`DrainQueueAsync`），超时只记一条告警。
- `PlayTestToneAsync` 全程 try/finally，`IsBusy` 一定会释放；语音合成最多等 15 秒，
  超时就退回 440 Hz 音（SAPI 单独测过是正常的：构造 2 ms、合成 50 ms，有 zh-CN 语音）。
- 兜底：`IsBusy` 超过 3 分钟仍未释放时，界面刷新路径会自动解锁并提示 `busy_timeout`。

音频断断续续还有一半是设置问题：当时缓冲区上限是 100 ms（就是实测能出声的最小值），
没有给抖动留余量，建议 500–1500 ms。

### 日志与错误文案跟随界面语言（2026-09-30）

现象：界面切成中文，日志（应用内的「日志与诊断」面板和文件）和错误提示仍然是西语或英语。

原因很单纯：那些文案是照着上游 Rust 版逐条搬过来的，上游作者用西语；Core 也不认识界面语言，
日志、异常都只是拼字符串，没有任何语言概念。

做法（三层，都收在 `Core/Localization/`）：

- `MessageTable.cs`：一个键对应三语（es / en / zh）的查表与占位符替换，三种语言的文案写在
  同一条记录里，所以不可能只补一种；占位符按匿名对象属性名替换，与界面文案同一套写法。
- 三张表：`Logging/LogMessages.cs`（`log.*`，应用与协议的运行记录）、
  `Localization/ErrorMessages.cs`（`error.*`，用户会看到的失败原因）、
  `Localization/NameMessages.cs`（`name.*`，Core 自己起的名字，例如手动添加设备的默认名）。
- `AppText` 持有当前语言并提供 `Get(key, parameters)`；`AirSendException` 基类让异常只带
  「键 + 参数」，`LocalizedMessage` 在**显示时**解析，所以同一条异常在不同语言下读出来不同。
  界面各处的 `err = ex.Message` 改成 `AirSendError.Describe(ex)`：域内异常走目录，框架异常
  （Socket、IO）保持自己的技术描述。
- `AppLog` 的 `Language` / `Text` 都代理到 `AppText`；`Info/Warn/Error/Debug` 改为接收
  「键 + 参数」，异常重载里的文本也用 `AirSendError.Describe`。未知键原样输出，不会静默丢消息。
- 应用启动时（创建 `Localization` 之后、写第一行日志之前）设定 `AppText.Language`，并订阅
  `LanguageChanged`，所以切换语言后新写入的日志和新弹出的错误立刻是新语言。

实测：

- 设置里把语言设成 zh / en / es 各跑一次，日志分别输出中文、英文、西语原文（西语列与上游逐字一致）。
- 直接调用 Core 的两个真实失败路径（手动地址解析、连接一个不存在的设备），三种语言下分别得到：
  `「not-an-ip」不是 IP 或 IP:端口 格式` / `'not-an-ip' is not an IP or IP:port endpoint` /
  `'not-an-ip' no es una IP ni una IP:puerto`，以及带真实超时秒数的 `连接 192.168.1.99:7000 超时（2 秒）`。

仍保持技术原文的：框架异常（Socket/IO/加密库）的文字，以及协议报文里原样带出来的字段。

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
| 采集来源跟随系统输出设备 | 上游固定采集默认设备的 loopback，切设备后只能靠用户重开；移植版监听默认端点变化并重建采集，同时在设置里选择某个端点时立刻生效 |
| 新增自动更新 | 上游没有更新机制；移植版按「启动时 / 每天 / 每周 / 不查」检查 GitHub Releases，确认后下载本机对应的包（架构 + 带不带运行时 + 版本三者都匹配），退出后由新版本自己接管替换文件并重启，并把结果留成回执 |
| 日志与错误文案跟随界面语言 | 上游的 `tracing` 输出和异常文字只有西语；移植版把日志（`log.*`）、用户可见的失败原因（`error.*`）和 Core 自起的名字（`name.*`）收进 `Core/Localization/` 的三语目录，按 `AppText.Language`（启动时设为界面语言）渲染，西语列保留上游原文；异常改为携带「键 + 参数」，显示时才解析 |
| 设置页两条文案不再与上游 `i18n.ts` 逐字一致 | 「采集的音频来源」的说明文字补了「会自动跟随系统输出」、新增「{name}（默认）」标记，三种语言同步更新；其余文案仍与上游一致 |

## 5. 已知限制

- 只支持 AirPlay 2 接收器（HomePod / Apple TV / AirPort Express 的 AirPlay 2 模式）。
- 单次会话的缓冲上限由延迟滑块决定（100–3000 ms）；下限 100 ms 是实测得出的（见第 4 节）。
- 延迟修改需要 10 秒冷却；播放中的修改会重建 SETUP，与上游行为一致。
- 未实现屏幕镜像、AirPlay 1（RAOP）加密、PTP 授时、多设备时钟同步。
- 采集来源最多 1 秒发现系统输出设备的变化（轮询，不是事件通知）。
- 自动更新要从 GitHub 下载安装包，国内网络可能很慢或中途失败；失败时对话框里可以直接点「打开发布页」手动下载。

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
