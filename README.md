# mpv-AnimeFusion

简体中文 | [English](README.en.md)

Windows 动漫播放器，支持 AI 超分、RIFE 补帧、在线／本地弹幕和 Bangumi 观看进度同步。

[下载](https://github.com/sunuuc/mpv-AnimeFusion/releases/latest) · [更新日志](CHANGELOG.md) · [使用说明](docs/standalone.md) · [问题反馈](https://github.com/sunuuc/mpv-AnimeFusion/issues)

## 功能

- **AI 超分与补帧**：多模型处理链、RIFE 补帧、可调倍率，以及按分辨率和帧率启用的自定义方案。
- **按需下载**：管理器检测显卡并推荐组件，也可自行选择模型和显卡组件。播放器包不内置模型。
- **弹幕**：多线路并行搜索；按作品和季度记住成功使用的 API、平台和剧集，下一集直接沿用，加载失败时重新匹配。支持本地 XML、速度、字号、不透明度、显示区域、类型屏蔽和屏蔽词。
- **Bangumi 同步**：浏览器授权登录，无需用户申请应用；底栏头像面板显示封面、评分、本季剧集与观看状态。自动收藏和标记看过分别设置播放阈值，支持手动“看过”和“看到”。
- **播放与字幕**：本地文件、网络视频、播放列表、续播、章节、音轨切换及双字幕。

## 安装

需要 Windows 64 位系统。

1. 在 [Releases](https://github.com/sunuuc/mpv-AnimeFusion/releases/latest) 下载 `mpv-AnimeFusion-*-win-x64.7z` 并解压。
2. 运行 `mpv-AnimeFusion.exe`，打开链接或拖入视频。
3. 需要超分／补帧时，打开 `mpv-AnimeFusionManager.exe`，在“组件”页下载相应模型和显卡组件，再到“配置方案”中启用。

## 使用

### 超分与补帧

在管理器“组件”页选择推荐项或手动勾选，点击“应用”下载。NVIDIA 显卡可选择 TensorRT，AMD / Intel 显卡可使用 DirectML；后端和处理方案在管理器中设置。首次使用 TensorRT 模型需要生成引擎缓存。

在“配置方案”中选择超分模型、补帧倍率和启用条件，然后设为默认方案。预设提供 2× 补帧及 2× 补帧＋2K 超分，其余槽位可自行配置。

### 弹幕

在“设置 → 弹幕设置 → 弹幕线路”中添加线路并排序。播放时自动匹配，底栏弹幕菜单支持搜索、选择剧集及导入本地 XML。首次匹配按标题、别名、季度和集数寻找来源；季数和集数严格校验，名称允许有限差异。

每部作品的每季分别记住来源，弹幕少或正常返回 0 条时不会换源。源加载失败或没有对应集时才重新搜索，成功后沿用新来源。个人线路和来源记录不随发行包分发。

字号、速度、不透明度、显示区域及屏蔽选项在“弹幕设置”中调整。已加载条目显示实际线路、平台和条数；加载时显示准备提示，完成时显示弹幕数量。人人视频会员弹幕保留原颜色并正常滚动。

### Bangumi

点击底栏账号图标，在浏览器登录并确认授权，成功后显示圆形头像。面板显示本季封面、名字、评分、当前集和剧集状态；匹配失败时可重试，也可手动选择条目。

在“设置 → 同步设置”中分别设置自动收藏和自动看过的播放阈值。已有收藏不会重复提交；提交进度前检查最新收藏状态。已收藏作品可手动标记单集“看过”，或按本季正篇顺序标记“看到”。应用密钥保存在授权服务，播放器不内置 App Secret。

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
