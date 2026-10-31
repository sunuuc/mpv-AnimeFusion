# mpv-AnimeFusion

[简体中文](README.md) | English

An mpv-based player built for anime, bringing together features from many projects. See the sources below. Supports AI upscaling, frame interpolation, danmaku and Bangumi/Trakt/MyAnimeList watch-progress sync.

[Download](https://github.com/sunuuc/mpv-AnimeFusion/releases/latest) · [Changelog](CHANGELOG.md) · [Report an issue](https://github.com/sunuuc/mpv-AnimeFusion/issues)


## Features

- **AI upscaling and interpolation**: improve image quality and playback smoothness.
  <img width="1898" height="1061" alt="image" src="https://github.com/user-attachments/assets/25d92e1d-db68-4327-bff5-1acc6fe29b2e" />

- **Downloads on demand**: download the models and GPU components you need.
  <img width="1898" height="1061" alt="image" src="https://github.com/user-attachments/assets/759796a2-a195-48f7-a791-ae2179e339d3" />

- **Danmaku**: online comments and local XML, JSON and ASS danmaku files. Online danmaku supports the Dandanplay API v2 interface. No danmaku routes are bundled or provided; users need to add compatible third-party danmaku APIs themselves.
<img width="2560" height="1494" alt="image" src="https://github.com/user-attachments/assets/6bdd6d74-6248-450c-b015-12f309820c14" />

- **Online subtitles**: search, download and load subtitles from ASSRT.

- **Watch sync**: sync watch progress with Bangumi, Trakt and MyAnimeList.
<img width="1213" height="1121" alt="image" src="https://github.com/user-attachments/assets/be033a4f-dfde-47be-8f05-7758bec43b91" />

## Community

This project recognizes and thanks the [LINUX DO](https://linux.do) community.

## Open-source credits

Thanks to these projects and their contributors. Modifications and individual licenses are documented in [Sources and third-party licenses](docs/open-source-notices.md).

| Project | Contribution |
| --- | --- |
| [mpv-AnimeJaNai](https://github.com/the-database/mpv-AnimeJaNai) / the-database | AI inference, model processing chains, RIFE loading and original playback configuration |
| [AnimeJaNaiManager](https://github.com/the-database/AnimeJaNaiManager) / the-database | Configuration and component-manager source |
| [mpv](https://github.com/mpv-player/mpv) | Playback, audio/video tracks, scripting and subtitle presentation |
| [mpv.net](https://github.com/mpvnet-player/mpv.net) | Windows frontend, menus and settings windows |
| [DanmakuFactory](https://github.com/hihkm/DanmakuFactory) | Danmaku layout, collision avoidance and ASS conversion |
| [libass](https://github.com/libass/libass) | ASS subtitle and danmaku rendering |
| [BangumiNet](https://github.com/ajtn123/BangumiNet) | Bangumi API client |
| [BangumiExtLinker](https://github.com/Rhilip/BangumiExtLinker) | Cross-site anime IDs and title data |
| [AnitomySharp](https://github.com/chu-shen/AnitomySharp) / [Anitomy](https://github.com/erengy/anitomy) | Anime filename parsing |
| [mpv_bangumi_sync](https://github.com/x-Armin/mpv_bangumi_sync) | Source of title, season, episode matching and progress-sync logic |
| [RIFE](https://github.com/hzwer/ECCV2022-RIFE), [Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN), [ONNX Runtime](https://github.com/microsoft/onnxruntime) | Interpolation, upscaling models and inference components |

TensorRT, CUDA and DirectML use their respective vendor licenses; notices are preserved in the package. AnimeJaNai models and the project use CC BY-NC-SA 4.0. Each component remains subject to its own license.
