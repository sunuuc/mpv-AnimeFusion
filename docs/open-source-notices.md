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
| thumbfast / po5 | <https://github.com/po5/thumbfast> | `docs/THIRD_PARTY_LICENSES/thumbfast-LICENSE.txt` |
| PCRE2 / Philip Hazel 及贡献者 | <https://github.com/PCRE2Project/pcre2> | BSD，`docs/THIRD_PARTY_LICENSES/PCRE2-BSD.txt` |

组件管理源码来源及版本见 `app/build-info/standalone/updater-upstream.json`。

弹幕使用 DanmakuFactory 排布与转换，mpv/libass 第二字幕轨负责呈现。恢复版本的渲染与设置见 [弹幕说明](danmaku-renderer.md)。

## Bangumi 同步

BangumiNet.Api（MIT）用于 API 请求；mpv_bangumi_sync（MIT）提供标题、集数匹配和进度同步逻辑；Google OAuth Desktop 示例代码（Apache-2.0）用于浏览器授权。固定版本、移植文件和修改范围见 [Bangumi 源码来源](bangumi-sources.md)。许可证原文及依赖记录随包保留在 `docs/THIRD_PARTY_LICENSES`。

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

播放器、管理器、弹幕转换与渲染的修改源码位于 `src` 和 `third_party`，构建记录位于 `app/build-info`。

## 再分发

保留版权声明、许可证原文和所分发二进制对应的修改源码。各组件适用各自的许可证。

Bangumi 收藏和剧集观看状态交互参考 [czy0729/Bangumi](https://github.com/czy0729/Bangumi)，MIT；许可证见 `THIRD_PARTY_LICENSES/czy0729-Bangumi-MIT.txt`。

历史方案的 aidly 与 hooks-plugin 许可证保留在许可目录；当前弹幕渲染采用 DanmakuFactory 与 mpv/libass，不加载这些历史弹幕组件。
