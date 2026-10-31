# mpv-AnimeFusion

简体中文 | [English](README.en.md)

动漫专用mpv，支持 AI 超分、补帧、弹幕和 Bangumi／Trakt／MyAnimeList(MAL) 观看进度同步。

[下载](https://github.com/sunuuc/mpv-AnimeFusion/releases/latest) · [更新日志](CHANGELOG.md) · [问题反馈](https://github.com/sunuuc/mpv-AnimeFusion/issues)

## 功能

- **AI 超分与补帧**：提升画质与播放流畅度。
  <img width="1898" height="1061" alt="image" src="https://github.com/user-attachments/assets/25d92e1d-db68-4327-bff5-1acc6fe29b2e" />

- **按需下载**：按需下载需要的模型和显卡组件。
  <img width="1898" height="1061" alt="image" src="https://github.com/user-attachments/assets/759796a2-a195-48f7-a791-ae2179e339d3" />

- **弹幕**：支持在线弹幕及本地 XML、JSON、ASS 弹幕文件。在线弹幕兼容弹弹play API v2 接口，不内置或提供任何弹幕线路，用户需自行添加兼容的第三方弹幕 API。
<img width="2560" height="1494" alt="image" src="https://github.com/user-attachments/assets/6bdd6d74-6248-450c-b015-12f309820c14" />

- **在线字幕**：通过 ASSRT 搜索、下载字幕。
<img width="1609" height="999" alt="image" src="https://github.com/user-attachments/assets/53eadfc1-a568-4876-ab94-8baf9748acac" />


- **观看同步**：支持 Bangumi、Trakt 与 MyAnimeList 观看进度同步。
<img width="1213" height="1121" alt="image" src="https://github.com/user-attachments/assets/be033a4f-dfde-47be-8f05-7758bec43b91" />

## 社区

本项目认可并感谢 [LINUX DO](https://linux.do) 社区。

## 开源来源与致谢

感谢以下项目及其贡献者，修改范围和逐项许可证见[来源与第三方许可](docs/open-source-notices.md)。

| 项目 | 用途 |
| --- | --- |
| [mpv-AnimeJaNai](https://github.com/the-database/mpv-AnimeJaNai) / the-database | AI 推理、模型处理链、RIFE 加载和原始播放配置 |
| [AnimeJaNaiManager](https://github.com/the-database/AnimeJaNaiManager) / the-database | 配置与组件管理器的基础源码 |
| [mpv](https://github.com/mpv-player/mpv) | 播放、音视频轨道、脚本 API 与字幕呈现 |
| [mpv.net](https://github.com/mpvnet-player/mpv.net) | Windows 播放器前端、菜单和设置窗口 |
| [DanmakuFactory](https://github.com/hihkm/DanmakuFactory) | 弹幕排布、防碰撞及 ASS 转换 |
| [libass](https://github.com/libass/libass) | ASS 字幕与弹幕渲染 |
| [BangumiNet](https://github.com/ajtn123/BangumiNet) | Bangumi API 客户端 |
| [BangumiExtLinker](https://github.com/Rhilip/BangumiExtLinker) | 跨站作品 ID 和名称数据 |
| [AnitomySharp](https://github.com/chu-shen/AnitomySharp) / [Anitomy](https://github.com/erengy/anitomy) | 动漫文件名解析 |
| [mpv_bangumi_sync](https://github.com/x-Armin/mpv_bangumi_sync) | 标题、季度、集数识别与进度同步逻辑的来源 |
| [RIFE](https://github.com/hzwer/ECCV2022-RIFE)、[Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN)、[ONNX Runtime](https://github.com/microsoft/onnxruntime) | 补帧、超分模型及推理组件 |

TensorRT、CUDA 和 DirectML 按各厂商许可证使用，相关声明随包保留。AnimeJaNai 模型及项目采用 CC BY-NC-SA 4.0，各组件分别遵循其原许可证。
