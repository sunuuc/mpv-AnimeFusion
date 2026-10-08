# mpv-AnimeFusion

简体中文 | [English](README.en.md)

Windows 动漫播放器，支持 AI 超分、RIFE 补帧、在线／本地弹幕和 Bangumi 观看进度同步。

[下载](https://github.com/sunuuc/mpv-AnimeFusion/releases/latest) · [更新日志](CHANGELOG.md) · [问题反馈](https://github.com/sunuuc/mpv-AnimeFusion/issues)

## 功能

- **AI 超分与补帧**：提升画质与播放流畅度。
- **按需下载**：下载模型和显卡组件。
- **弹幕**：支持在线和本地弹幕。
- **Bangumi 同步**：同步收藏与观看进度。
- **播放与字幕**：支持本地和网络视频、播放列表及字幕。

## 开源来源与致谢

感谢以下项目及其贡献者。这里说明各项目在本项目中的用途；固定版本、修改范围和逐项许可证见[来源与第三方许可](docs/open-source-notices.md)。

| 项目 | 用途 |
| --- | --- |
| [mpv-AnimeJaNai](https://github.com/the-database/mpv-AnimeJaNai) / the-database | AI 推理、模型处理链、RIFE 加载和原始播放配置 |
| [AnimeJaNaiManager](https://github.com/the-database/AnimeJaNaiManager) / the-database | 配置与组件管理器的基础源码 |
| [mpv](https://github.com/mpv-player/mpv) | 播放、音视频轨道、脚本 API 与字幕呈现 |
| [mpv.net](https://github.com/mpvnet-player/mpv.net) | Windows 播放器前端、菜单和设置窗口 |
| [DanmakuFactory](https://github.com/hihkm/DanmakuFactory) | 弹幕排布、防碰撞及 ASS 转换 |
| [libass](https://github.com/libass/libass) | ASS 字幕与弹幕渲染 |
| [thumbfast](https://github.com/po5/thumbfast) | 进度条缩略图 |
| [BangumiNet](https://github.com/ajtn123/BangumiNet) | Bangumi API 客户端 |
| [mpv_bangumi_sync](https://github.com/x-Armin/mpv_bangumi_sync) | 标题、季度、集数识别与进度同步逻辑的来源 |
| [czy0729/Bangumi](https://github.com/czy0729/Bangumi) | 收藏和剧集状态分离，以及“看过／看到”的交互参考 |
| [OAuth apps for Windows](https://github.com/googlesamples/oauth-apps-for-windows) | 浏览器授权与回环回调流程参考 |
| [.NET / ASP.NET Core](https://github.com/dotnet/aspnetcore) | Windows 运行库和授权服务 |
| [RIFE](https://github.com/hzwer/ECCV2022-RIFE)、[Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN)、[ONNX Runtime](https://github.com/microsoft/onnxruntime) | 补帧、超分模型及推理组件 |
| [FFmpeg](https://ffmpeg.org/)、[libplacebo](https://github.com/haasn/libplacebo)、[MSYS2](https://www.msys2.org/)、[PCRE2](https://github.com/PCRE2Project/pcre2) | 解码、视频输出、原生依赖与弹幕转换依赖 |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia)、[FluentAvalonia](https://github.com/amwx/FluentAvalonia)、[ReactiveUI](https://github.com/reactiveui/ReactiveUI) | 管理器界面及状态绑定 |
| [SkiaSharp / HarfBuzzSharp](https://github.com/mono/SkiaSharp)、[Inter](https://github.com/rsms/inter) | 管理器绘制、文字排版和字体 |
| [Nginx](https://nginx.org/)、[Certbot](https://github.com/certbot/certbot) | 授权服务的 HTTPS 入口及证书续期 |
| [Inno Setup](https://github.com/jrsoftware/issrc) | EXE 安装包与卸载程序 |

TensorRT、CUDA 和 DirectML 按各厂商许可证使用，相关声明随包保留。AnimeJaNai 模型及项目采用 CC BY-NC-SA 4.0，各组件分别遵循其原许可证。
