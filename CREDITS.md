# 来源声明 / Credits

AirSend 是 **[Pabldi08/AirSend](https://github.com/Pabldi08/AirSend)** 的 WinUI 3 / C# 移植版。

## 原始项目

| 项目 | 许可证 | 说明 |
|---|---|---|
| [Pabldi08/AirSend](https://github.com/Pabldi08/AirSend) | GPL-3.0-or-later | **被移植的原项目**（Rust + Tauri）。本项目的界面流程、功能范围、设备发现/探测/推流编排、延迟与音量策略都移植自它；`LICENSE` 用的就是它的许可证原文 |
| [airplay2-rs](https://github.com/Pabldi08/airplay2-rs)（上游为 [lmcgartland/airplay2-rs](https://github.com/lmcgartland/airplay2-rs)） | GPL-3.0-or-later | 原项目使用的 AirPlay 2 协议栈。本项目的 RTSP 流程、瞬态配对、RTP/ChaCha20-Poly1305 报文布局、ALAC 帧与 magic cookie、SYNC 授时等协议行为均以它为准复刻（C# 重新实现） |

原项目是 GPL-3.0-or-later，因此本移植同样以 **GPL-3.0-or-later** 发布，详见 [LICENSE](LICENSE)。

---

## English

AirSend is a WinUI 3 / C# port of **[Pabldi08/AirSend](https://github.com/Pabldi08/AirSend)**
(GPL-3.0-or-later). Its AirPlay 2 protocol behaviour follows
[airplay2-rs](https://github.com/Pabldi08/airplay2-rs) (upstream:
[lmcgartland/airplay2-rs](https://github.com/lmcgartland/airplay2-rs)), also
GPL-3.0-or-later. This port is therefore released under the same license.
