# 贡献指南

## 需要准备什么

- Windows 10 1809 或更新（Windows 11 一样跑）
- .NET SDK 10.0.100 以上，用 `dotnet --version` 确认
- 编辑器随意：Visual Studio 2022（17.14 以上）或者 VS Code + C# Dev Kit 都行

不用装 Windows App SDK 运行时，也不用打开开发者模式。项目按「免打包」方式构建，
发布时由 `WindowsAppSDKSelfContained` 把运行时一起复制到输出目录。

## 编译、测试、打包

```powershell
dotnet build AirSend.slnx -c Release                             # 编译
dotnet test tests/AirSend.Core.Tests/AirSend.Core.Tests.csproj   # 单元测试
dotnet run --project src/AirSend.App/AirSend.App.csproj          # 直接运行
```

打一个解压即用的 zip（默认 Release / win-x64，自带运行时）：

```powershell
.\build-release.ps1
.\build-release.ps1 -RuntimeIdentifier win-arm64
```

## 代码在哪

| 目录 | 里面是什么 |
|---|---|
| `src/AirSend.App` | WinUI 3 界面：设备 / 播放 / 设置三个页面、ViewModel，以及设置存储、托盘、单实例、本地化、语音测试这些服务 |
| `src/AirSend.Core` | 协议与音频核心：mDNS 发现、RTSP 与二进制 plist、SRP-6a、Curve25519 / Ed25519、ChaCha20-Poly1305、ALAC、RTP、NTP 授时、WASAPI 采集 |
| `tests/AirSend.Core.Tests` | 单元测试：RFC 官方测试向量、与独立参考实现逐字节比对、对着脚本化的模拟接收器跑完整握手 |

## 改的时候注意几点

- 动了协议层的代码，先把测试跑一遍。
- 涉及真机的部分（配对、SETUP、音频输出）最好在真实的 AirPlay 2 接收器上过一遍，
  结论记到 [PORTING.md](PORTING.md) 里 —— 那里同时记着已经被排除的猜测和踩过的坑。
- 提交信息写清**为什么改**，不要只写「改了什么」；一个提交只做一件事。
- XAML 里不要用字符 emoji，图标统一用 Segoe Fluent。
- 界面上的新文案三种语言（zh / en / es）都要补齐，位置在
  `src/AirSend.App/Services/Localization.cs`。

## 许可

GPL-3.0-or-later。提交代码即表示同意按这个许可证发布。来源与致谢见 [CREDITS.md](CREDITS.md)。

---

## Contributing (English)

- Requirements: Windows 10 1809+, .NET SDK 10.0.100+. No Windows App SDK runtime install and no
  Developer Mode needed — the app builds unpackaged and ships self-contained.
- Build with `dotnet build AirSend.slnx -c Release`, test with `dotnet test`, package with
  `.\build-release.ps1`.
- Run the tests when you change the protocol layer, and verify anything involving real hardware
  (pairing, SETUP, audio output) against an actual AirPlay 2 receiver — then write the result
  down in [PORTING.md](PORTING.md).
- Add user-visible strings in all three languages (see `src/AirSend.App/Services/Localization.cs`),
  and prefer Segoe Fluent icons over text emoji.
