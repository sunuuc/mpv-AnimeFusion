# mpv-AnimeFusion

简体中文 | [English](README.en.md)

Windows 动漫播放器，支持 AI 超分、RIFE 补帧、在线／本地弹幕和 Bangumi 观看进度同步。

[下载](https://github.com/sunuuc/mpv-AnimeFusion/releases/latest) · [更新日志](CHANGELOG.md) · [使用说明](docs/standalone.md) · [问题反馈](https://github.com/sunuuc/mpv-AnimeFusion/issues)

## 功能

- **AI 超分与补帧**：多模型处理链、RIFE 补帧、可调倍率，以及按分辨率和帧率启用的自定义方案。
- **按需下载**：管理器检测显卡并推荐组件，也可自行选择模型和显卡组件。播放器包不内置模型。
- **弹幕**：多线路并行搜索、自动匹配；支持本地 XML、速度、字号、不透明度、显示区域、类型屏蔽和屏蔽词。
- **Bangumi 同步**：浏览器授权登录，查看封面、评分和剧集观看状态；自动收藏和标记看过可分别设置播放阈值，也可手动标记“看过”和“看到”。
- **播放与字幕**：本地文件、网络视频、播放列表、续播、章节、音轨切换及双字幕。

## 安装

需要 Windows 64 位系统。

1. 在 [Releases](https://github.com/sunuuc/mpv-AnimeFusion/releases/latest) 下载 `mpv-AnimeFusion-*-win-x64.7z` 并解压。
2. 运行 `mpv-AnimeFusion.exe`，打开链接或拖入视频。
3. 需要超分／补帧时，打开 `mpv-AnimeFusionManager.exe`，在“组件”页下载相应模型和显卡组件，再到“配置方案”中启用。

## 使用

### 超分与补帧

1. 打开管理器“组件”页，选择推荐项或手动勾选模型和显卡组件，点击“应用”下载。
2. 在“配置方案”中选择模型、补帧倍率和启用条件，设为默认方案。
3. 播放时按 Ctrl+1–9 切换方案，Ctrl+0 关闭 AI 处理。

### 弹幕

1. 在“设置 → 弹幕设置 → 弹幕线路”中添加线路并排序。
2. 打开视频自动加载弹幕；需要手动选择时，点击底栏弹幕按钮 →“搜索弹幕”，搜索作品并选集。
3. 在“弹幕设置”中调整字号、速度、不透明度、显示区域和屏蔽选项。
4. 本地 XML 使用弹幕菜单中的“导入本地弹幕”。

### Bangumi

1. 点击底栏账号图标，在浏览器登录并确认授权。
2. 点击头像查看作品和剧集；点击“收藏”设置状态，点击剧集标记“看过”或“看到”。
3. 在“设置 → 同步设置”中分别开启自动收藏和自动看过，并设置播放阈值。
4. 匹配失败时点击重试；需要更换作品时点击“选择条目”。

### 常用快捷键

| 按键 | 功能 |
| --- | --- |
| 空格 | 播放／暂停 |
| 左／右 | 后退／前进 5 秒 |
| 上／下 | 调整音量 |
| 双击 | 切换全屏 |
| Esc | 返回菜单或退出全屏 |
| Tab | 显示／隐藏 mpv 完整统计 |
| Ctrl+E | 打开配置管理器 |
| Ctrl+Tab | AI 状态、实际／目标 FPS 与 GPU 占用 |
| Ctrl+1–9 | 切换自定义方案 |
| Ctrl+0 | 关闭 AI 处理 |

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

TensorRT、CUDA 和 DirectML 按各厂商许可证使用，相关声明随包保留。AnimeJaNai 模型及项目采用 CC BY-NC-SA 4.0，各组件分别遵循其原许可证。

## 文档

[配置说明](docs/standalone.md) · [构建](docs/build.md) · [弹幕渲染](docs/danmaku-renderer.md) · [Bangumi 来源](docs/bangumi-sources.md) · [第三方许可](docs/open-source-notices.md)
