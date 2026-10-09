# mpv-AnimeFusion

[简体中文](README.md) | English

An mpv-based player built for anime, bringing together features from many projects. See the sources below. Supports AI upscaling, frame interpolation, danmaku and Bangumi watch-progress sync.

[Download](https://github.com/sunuuc/mpv-AnimeFusion/releases/latest) · [Changelog](CHANGELOG.md) · [Report an issue](https://github.com/sunuuc/mpv-AnimeFusion/issues)

## Features

- **AI upscaling and interpolation**: improve image quality and playback smoothness.
  <img width="1898" height="1061" alt="image" src="https://github.com/user-attachments/assets/25d92e1d-db68-4327-bff5-1acc6fe29b2e" />

- **Downloads on demand**: download the models and GPU components you need.
  <img width="1898" height="1061" alt="image" src="https://github.com/user-attachments/assets/759796a2-a195-48f7-a791-ae2179e339d3" />

- **Danmaku**: online comments and local XML, JSON and ASS danmaku files. Online danmaku supports the Dandanplay API v2 interface. No danmaku routes are bundled or provided; users need to add compatible third-party danmaku APIs themselves.
<img width="2560" height="1494" alt="image" src="https://github.com/user-attachments/assets/6bdd6d74-6248-450c-b015-12f309820c14" />

- **Bangumi sync**: automatically sync collections and watch progress.
<img width="1213" height="1121" alt="image" src="https://github.com/user-attachments/assets/be033a4f-dfde-47be-8f05-7758bec43b91" />

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
| [thumbfast](https://github.com/po5/thumbfast) | Seek-bar thumbnails |
| [BangumiNet](https://github.com/ajtn123/BangumiNet) | Bangumi API client |
| [mpv_bangumi_sync](https://github.com/x-Armin/mpv_bangumi_sync) | Source of title, season, episode matching and progress-sync logic |
| [czy0729/Bangumi](https://github.com/czy0729/Bangumi) | Reference for separate collection/episode states and individual/cumulative progress actions |
| [OAuth apps for Windows](https://github.com/googlesamples/oauth-apps-for-windows) | Browser authorization and loopback callback reference |
| [.NET / ASP.NET Core](https://github.com/dotnet/aspnetcore) | Windows runtime and authorization service |
| [RIFE](https://github.com/hzwer/ECCV2022-RIFE), [Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN), [ONNX Runtime](https://github.com/microsoft/onnxruntime) | Interpolation, upscaling models and inference components |
| [FFmpeg](https://ffmpeg.org/), [libplacebo](https://github.com/haasn/libplacebo), [MSYS2](https://www.msys2.org/), [PCRE2](https://github.com/PCRE2Project/pcre2) | Decoding, video output, native runtime and converter dependencies |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia), [FluentAvalonia](https://github.com/amwx/FluentAvalonia), [ReactiveUI](https://github.com/reactiveui/ReactiveUI) | Manager UI and state binding |
| [SkiaSharp / HarfBuzzSharp](https://github.com/mono/SkiaSharp), [Inter](https://github.com/rsms/inter) | Manager drawing, text shaping and fonts |
| [Nginx](https://nginx.org/), [Certbot](https://github.com/certbot/certbot) | HTTPS endpoint and certificate renewal for authorization |
| [Inno Setup](https://github.com/jrsoftware/issrc) | EXE installer and uninstaller |
| [curl](https://github.com/curl/curl) | Online danmaku requests |

TensorRT, CUDA and DirectML use their respective vendor licenses; notices are preserved in the package. AnimeJaNai models and the project use CC BY-NC-SA 4.0. Each component remains subject to its own license.
