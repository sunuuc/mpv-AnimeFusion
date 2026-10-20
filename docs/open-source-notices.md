# mpv-AnimeFusion：来源、修改与第三方许可

## 原始项目与核心组件

| 项目 / 作者 | 来源 | 许可证 / 声明位置 |
| --- | --- | --- |
| mpv-AnimeJaNai / the-database | <https://github.com/the-database/mpv-AnimeJaNai> | `docs/LICENSE`，CC BY-NC-SA 4.0 |
| AnimeJaNaiManager / the-database | <https://github.com/the-database/AnimeJaNaiManager> | `docs/THIRD_PARTY_LICENSES/AnimeJaNaiManager-GPL-3.0.txt` |
| mpv / mpv contributors | <https://github.com/mpv-player/mpv> | `docs/THIRD_PARTY_LICENSES/mpv-source/`；具体构建包含 GPL/LGPL 组件，保留原声明 |
| mpv.net / stax76 及贡献者 | <https://github.com/mpvnet-player/mpvnet> | `docs/THIRD_PARTY_LICENSES/mpv.net-LICENSE.txt` |
| DanmakuFactory / hihkm 及贡献者 | <https://github.com/hihkm/DanmakuFactory> | MIT，`docs/THIRD_PARTY_LICENSES/DanmakuFactory-MIT.txt`；转换源码在 `third_party/danmaku-factory` |
| libass / libass contributors | <https://github.com/libass/libass> | ISC，`docs/THIRD_PARTY_LICENSES/libass-ISC.txt`；修改源码在 `third_party/libass` |
| PCRE2 / Philip Hazel 及贡献者 | <https://github.com/PCRE2Project/pcre2> | BSD，`docs/THIRD_PARTY_LICENSES/PCRE2-BSD.txt` |
| Inno Setup / Jordan Russell、Martijn Laan 及贡献者 | <https://github.com/jrsoftware/issrc> | 安装与卸载程序，6.7.3，`docs/THIRD_PARTY_LICENSES/Inno-Setup.txt` |

组件管理源码来源及版本见 `app/build-info/standalone/updater-upstream.json`。

弹幕使用 DanmakuFactory 排布与转换，mpv/libass 第二字幕轨负责呈现。恢复版本的渲染与设置见 [弹幕说明](danmaku-renderer.md)。

在线弹幕通过 mpv 原生 `subprocess` 异步调用 Windows 自带的 [curl](https://github.com/curl/curl)；请求参数通过 mpv 的参数数组传递，不通过 PowerShell。curl 使用 curl license，由系统提供，本包不重新分发。视频识别仅读取开头 16 MiB，通过 Windows BCrypt API 计算 MD5；本地导入使用 mpv.net 的原生文件选择窗口。

## 在线字幕

字幕搜索和下载通过 [ASSRT 官方 API](https://assrt.net/api/doc) 提供，客户端直接请求服务。字幕服务、账号权限和请求配额受平台规则约束。实现与凭据配置见 [在线字幕](online-subtitles.md)。

字幕包解压使用未修改的 [SharpCompress](https://github.com/adamhathcock/sharpcompress) 0.50.5（MIT），支持 ZIP、RAR 与 7z；许可证原文保留于 `docs/THIRD_PARTY_LICENSES/NuGet/SharpCompress-MIT.txt`。

搜索列表沿用 mpv.net 内附的 [HandyControl](https://github.com/HandyOrg/HandyControl) 滚动控件（MIT），在原控件源码中通过 WPF `IScrollInfo` 直接处理像素输入，不逐次添加滚动动画；许可证见 `THIRD_PARTY_LICENSES/HandyControl-MIT.txt`。

## Bangumi 同步

BangumiNet.Api（MIT）用于 API 请求；mpv_bangumi_sync（MIT）提供标题、集数匹配和进度同步逻辑；Google OAuth Desktop 示例代码（Apache-2.0）用于浏览器授权。固定版本、移植文件和修改范围见 [Bangumi 源码来源](bangumi-sources.md)。许可证原文及依赖记录随包保留在 `docs/THIRD_PARTY_LICENSES`。

## Trakt 同步与文件名解析

Trakt 请求和响应契约来源于 [trakt/trakt-api](https://github.com/trakt/trakt-api)，MIT；服务器使用 ASP.NET Core OpenID Connect 的 PKCE 流程。Bangumi 与 Trakt 共用 [AnitomySharp](https://github.com/chu-shen/AnitomySharp) 文件名解析器，MPL-2.0，其来源为 erengy/anitomy 和 AnitomyJ；本地修正了末段分词和分隔符合并的边界检查。固定提交、移植范围与官方文档见 [Trakt 源码来源](trakt-sources.md)。头像和封面的 WebP 解码复用 SkiaSharp，MIT。新增许可原文随源码及包保留。

## MyAnimeList 同步

沿用 ASP.NET Core OAuth（MIT）扩展和 Google 桌面授权示例（Apache-2.0），使用 MAL 官方公开客户端 PKCE。AnitomySharp 用于文件名解析；[Rhilip/BangumiExtLinker](https://github.com/Rhilip/BangumiExtLinker) 的 CC BY 4.0 数据用于跨站作品候选，数据及上游署名保留在 `third_party/bangumi-ext-linker`，许可为 `THIRD_PARTY_LICENSES/BangumiExtLinker-CC-BY-4.0.txt`。详情见 [MAL 来源与研究](myanimelist-research.md)。Taiga 仅为研究参考，本次未复制其 GPL 源码。

## 模型、界面框架与运行库

- AnimeJaNai 超分模型与项目采用 **CC BY-NC-SA 4.0**。需要署名、限非商业使用、修改后按相同方式共享；完整条件以 `LICENSE` 为准。
- RIFE 来源：<https://github.com/hzwer/ECCV2022-RIFE>，原项目 MIT 原文为 `docs/THIRD_PARTY_LICENSES/RIFE-MIT.txt`；模型由固定版本的上游 AnimeJaNai 组件包提供。
- Real-ESRGAN 来源：<https://github.com/xinntao/Real-ESRGAN>；模型使用的架构名称和上游模型文件名保留不变，原项目 BSD 3-Clause 原文为 `docs/THIRD_PARTY_LICENSES/Real-ESRGAN-BSD.txt`。
- .NET 与 Windows Desktop 运行库：MIT，完整原文及 .NET 第三方声明在 `docs/THIRD_PARTY_LICENSES/Microsoft/`。
- SkiaSharp、HarfBuzzSharp：随包保留 NuGet 组件的 MIT 原文于 `docs/THIRD_PARTY_LICENSES/NuGet/`；Inter 字体：SIL OFL 1.1，原文为 `docs/THIRD_PARTY_LICENSES/Inter-OFL-1.1.txt`。
- Avalonia（MIT）：<https://github.com/AvaloniaUI/Avalonia>；FluentAvalonia（MIT）：<https://github.com/amwx/FluentAvalonia>；ReactiveUI（MIT）：<https://github.com/reactiveui/ReactiveUI>。各自的 MIT 原文保留在 `docs/THIRD_PARTY_LICENSES/`。
- TensorRT / CUDA：NVIDIA 厂商许可，**不是开源许可证**；固定运行库版本为 TensorRT 11.1 / CUDA 13.3。原许可与附带第三方声明在 `docs/THIRD_PARTY_LICENSES/NVIDIA/`。
- DirectML（Microsoft 软件许可）：<https://github.com/microsoft/DirectML>；ONNX Runtime（MIT）：<https://github.com/microsoft/onnxruntime>；原声明在 `animejanai/inference/THIRD_PARTY_NOTICES.txt`，许可原文为 `docs/THIRD_PARTY_LICENSES/DirectML.txt` 与 `docs/THIRD_PARTY_LICENSES/ONNX-Runtime-MIT.txt`。
- mpv 所使用的 FFmpeg、MSYS2 及其他依赖的逐项原许可保留在 `docs/THIRD_PARTY_LICENSES/MSYS2/` 与 `docs/licenses/`。新增许可文件的来源与 SHA-256 在 `docs/THIRD_PARTY_LICENSES/sources.json`。

## 修改源码

EXE 安装定义位于 `src/player/src/Setup/Inno`。简体中文安装界面采用 Inno Setup `is-6_7_3` 标签中的 `Files/Languages/Unofficial/ChineseSimplified.isl`，维护者 Zhenghan Yang，SHA-256 为 `7d544b9bb1d142cfa11f2e5d3cc8abe2e55f8e066c5124e3772675aa236e1278`，保留原文件署名。构建时用 [innoextract_win](https://github.com/UserUnknownFactor/innoextract_win) 670 解包编译器；它基于 [innoextract](https://github.com/dscharrer/innoextract)，仅为构建工具，不进入发行包。

播放器、管理器、弹幕转换与渲染的修改源码位于 `src` 和 `third_party`，构建记录位于 `app/build-info`。

## 再分发

镜像反转通过 mpv 的 `glsl-shaders` 接口使用 [hdr-toys](https://github.com/natural-harmonia-gropius/hdr-toys) 原版 `utils/transform.glsl`，固定版本 `41d1044e64e3ba06de966c09b72e23b76690cf4a`，源码不作修改。作者 natural-harmonia-gropius，MIT；许可证见 `THIRD_PARTY_LICENSES/hdr-toys-MIT.txt`。

保留版权声明、许可证原文和所分发二进制对应的修改源码。各组件适用各自的许可证。

Bangumi 收藏和剧集观看状态交互参考 [czy0729/Bangumi](https://github.com/czy0729/Bangumi)，MIT；许可证见 `THIRD_PARTY_LICENSES/czy0729-Bangumi-MIT.txt`。

历史方案的 aidly 与 hooks-plugin 许可证保留在许可目录；当前弹幕渲染采用 DanmakuFactory 与 mpv/libass，不加载这些历史弹幕组件。
