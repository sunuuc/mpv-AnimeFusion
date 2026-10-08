# mpv-AnimeFusion

[简体中文](README.md) | English

A Windows anime player with AI upscaling, RIFE frame interpolation, online/local danmaku and Bangumi watch-progress sync.

[Download](https://github.com/sunuuc/mpv-AnimeFusion/releases/latest) · [Changelog](CHANGELOG.md) · [User guide](docs/standalone.md) · [Report an issue](https://github.com/sunuuc/mpv-AnimeFusion/issues)

## Features

- **AI upscaling and interpolation**: multiple-model processing chains, RIFE interpolation, adjustable multipliers and custom profiles activated by resolution and frame rate.
- **Downloads on demand**: the manager detects your GPU and recommends components; models and GPU components can also be selected manually. The player package contains no models.
- **Danmaku**: parallel search across routes; remembers the working API, platform and episode mapping per show and season, reuses that source for subsequent episodes and searches again on loading failure. Supports local XML, speed, font size, opacity, display area, type filters and blocked words.
- **Bangumi sync**: sign in through browser authorization without creating your own application. The bottom-bar avatar panel shows cover art, rating, seasonal episodes and watch status. Automatic collection and watched-episode updates have separate playback thresholds; manual single-episode and cumulative progress updates are available.
- **Playback and subtitles**: local files, network video, playlists, resume, chapters, audio-track selection and dual subtitles.

## Installation

Requires 64-bit Windows.

1. Download `mpv-AnimeFusion-*-win-x64.7z` from [Releases](https://github.com/sunuuc/mpv-AnimeFusion/releases/latest) and extract it.
2. Run `mpv-AnimeFusion.exe`, then open a URL or drag in a video.
3. For AI processing, open `mpv-AnimeFusionManager.exe`, download the required models and GPU components under **Components**, then enable them under **Profiles**.

## Usage

### Upscaling and interpolation

Choose recommended components or select them manually under **Components**, then click **Apply** to download. NVIDIA GPUs can use TensorRT; AMD and Intel GPUs can use DirectML. Select the backend, models, interpolation multiplier and activation conditions in the manager. TensorRT builds a local engine cache on first use.

The initial profiles offer 2× interpolation and 2× interpolation with 2K upscaling. Other slots are available for your own profiles.

### Danmaku

Add and sort routes under **Settings → Danmaku settings → Danmaku routes**. Matching runs automatically; the bottom-bar danmaku menu also provides manual search, episode selection and local XML import. Matching uses titles, aliases, seasons and episodes, tolerating limited title differences while requiring the correct season and episode.

Sources are remembered separately for each show and season. Low comment counts, including a successful empty response, do not trigger source switching. Loading failure or a missing episode triggers another search; the next successful source becomes the remembered source. Personal routes and source history are excluded from releases.

Configure speed, size, opacity, display area and filters under **Danmaku settings**. The loaded entry displays its actual route, platform and comment count. Loading and completion notices show preparation status and the comment count. Renren member comments retain their source color and scroll normally.

### Bangumi

Click the account icon in the bottom bar and authorize in your browser. A circular avatar appears after login. Its panel shows the cover, title, rating, current episode and episode states. Failed matching can be retried, and subjects can also be selected manually.

Set separate thresholds for automatic collection and watched-episode updates under **Settings → Sync settings**. Existing collections are preserved; the latest collection state is checked before progress is submitted. For a collected subject, mark one episode watched or mark progress cumulatively through the selected regular episode. The application secret remains on the authorization service and is not bundled in the player.

### Keyboard shortcuts

| Key | Action |
| --- | --- |
| Space | Play / pause |
| Left / Right | Seek backward / forward 5 seconds |
| Up / Down | Adjust volume |
| Double-click | Toggle fullscreen |
| Esc | Back in menus / leave fullscreen |
| Tab | Toggle full mpv statistics |
| Ctrl+E | Open the configuration manager |
| Ctrl+Tab | AI status, actual / target FPS and GPU usage |
| Ctrl+1–9 | Select a custom profile |
| Ctrl+0 | Disable AI processing |

## Open-source credits

Thanks to these projects and their contributors. Their roles are listed below; pinned versions, modifications and individual licenses are documented in [Sources and third-party licenses](docs/open-source-notices.md).

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

TensorRT, CUDA and DirectML use their respective vendor licenses; notices are preserved in the package. AnimeJaNai models and the project use CC BY-NC-SA 4.0. Each component remains subject to its own license.

## Documentation

[Configuration](docs/standalone.md) · [Build guide](docs/build.md) · [Danmaku rendering](docs/danmaku-renderer.md) · [Bangumi sources](docs/bangumi-sources.md) · [Third-party licenses](docs/open-source-notices.md)
