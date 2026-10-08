# mpv-AnimeFusion

[简体中文](README.md) | English

A Windows anime player with AI upscaling, RIFE frame interpolation, online/local danmaku and Bangumi watch-progress sync.

[Download](https://github.com/sunuuc/mpv-AnimeFusion/releases/latest) · [Changelog](CHANGELOG.md) · [User guide](docs/standalone.md) · [Report an issue](https://github.com/sunuuc/mpv-AnimeFusion/issues)

## Features

- **AI upscaling and interpolation**: improve image quality and playback smoothness.
- **Downloads on demand**: download models and GPU components.
- **Danmaku**: online and local comments.
- **Bangumi sync**: sync collections and watch progress.
- **Playback and subtitles**: local and network videos, playlists and subtitles.

## Installation

Requires 64-bit Windows.

1. Download `mpv-AnimeFusion-*-win-x64.7z` from [Releases](https://github.com/sunuuc/mpv-AnimeFusion/releases/latest) and extract it.
2. Run `mpv-AnimeFusion.exe`, then open a URL or drag in a video.
3. For AI processing, open `mpv-AnimeFusionManager.exe`, download the required models and GPU components under **Components**, then enable them under **Profiles**.

## Usage

### Upscaling and interpolation

1. Open **Components** in the manager, choose recommended items or select models and GPU components manually, then click **Apply** to download.
2. Under **Profiles**, choose models, the interpolation multiplier and activation conditions, then set a default profile.
3. During playback, press Ctrl+1–9 to select a profile or Ctrl+0 to disable AI processing.

### Danmaku

1. Add and sort routes under **Settings → Danmaku settings → Danmaku routes**.
2. Open a video to load danmaku automatically. For manual selection, open the bottom-bar danmaku menu → **Search danmaku**, search for a show and choose an episode.
3. Adjust font size, speed, opacity, display area and filters under **Danmaku settings**.
4. Use **Import local danmaku** in the danmaku menu to load a local XML file.

### Bangumi

1. Click the account icon in the bottom bar, sign in through your browser and authorize the player.
2. Click your avatar to view the show and episodes. Choose a collection status, then click an episode to mark it watched or mark progress through it.
3. Under **Settings → Sync settings**, enable automatic collection and watched-episode updates separately and set their playback thresholds.
4. Retry failed matching, or use **Select subject** to choose another show.

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
