# 贡献指南

## 环境要求

- Windows 10 1809+ / Windows 11
- .NET SDK 10.0.100 或更高（`dotnet --version`）
- 可选：Visual Studio 2022 17.14+ 或 VS Code（C# Dev Kit）

不需要安装 Windows App SDK 运行时、也不需要开启开发者模式：应用以**免打包（unpackaged）**
方式构建，`WindowsAppSDKSelfContained` 会把运行时随程序一起复制。

## 构建与测试

```powershell
dotnet build AirSend.slnx -c Release      # 编译
dotnet test tests/AirSend.Core.Tests/AirSend.Core.Tests.csproj   # 59 项单元测试
dotnet run --project src/AirSend.App/AirSend.App.csproj          # 直接运行
```

生成 zip 分发包（自包含、解压即用）：

```powershell
.\build-release.ps1                       # 默认 Release / win-x64
.\build-release.ps1 -RuntimeIdentifier win-arm64
```

## 代码结构

| 目录 | 内容 |
|---|---|
| `src/AirSend.App` | WinUI 3 界面（三个视图 + ViewModel + 服务：设置、托盘、单实例、本地化、语音测试） |
| `src/AirSend.Core` | 协议与音频核心：mDNS 发现、RTSP/plist、SRP/Curve25519/Ed25519/ChaCha20、ALAC、RTP、WASAPI 采集 |
| `tests/AirSend.Core.Tests` | 单元测试：RFC 向量、与独立参考实现逐字节比对、脚本化接收器完整握手 |

改动协议层时请跑测试；涉及真机的改动（配对、SETUP、音频）建议在真实 AirPlay 2 接收器上验证，
并在 [PORTING.md](PORTING.md) 里记录结论。

## 提交约定

- 提交信息用现在时、说明「为什么」而不只是「改了什么」；
- 一个提交只做一件事；
- XAML 里避免字符 emoji，统一用 Segoe Fluent 图标；
- 新增用户可见文案时，三种语言（zh / en / es）都要补齐，见 `src/AirSend.App/Services/Localization.cs`。

## 许可证

GPL-3.0-or-later。提交代码即表示同意以该许可证发布。来源与致谢见 [CREDITS.md](CREDITS.md)。

---

## Contributing (English)

- Requirements: Windows 10 1809+, .NET SDK 10+. No Windows App SDK runtime install and no
  Developer Mode needed (the app builds unpackaged and ships self-contained).
- Build with `dotnet build AirSend.slnx -c Release`, test with `dotnet test`, package with
  `.\build-release.ps1`.
- Please keep `PORTING.md` up to date when you change protocol behaviour, add user visible
  strings in all three languages, and prefer Segoe Fluent icons over text emoji.
