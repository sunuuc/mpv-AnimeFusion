# Bangumi 同步源码来源

| 来源 | 固定版本 | 用途与修改 |
| --- | --- | --- |
| [BangumiNet](https://github.com/ajtn123/BangumiNet) | `BangumiNet.Api 1.2.1`，提交 `b3fd617e8361a65791b6ca9dd3eeb46d06bfaea4` | 使用现成的异步 API 客户端与 Bearer 认证，访问账号、条目、集数和收藏进度。MIT。 |
| [mpv_bangumi_sync](https://github.com/x-Armin/mpv_bangumi_sync) | `54d0ba89510ab0d254e1a117560820200a5f76b8` | 移植 `title_guess.lua` 的系列／季度解析与键、`episode_matcher.lua` 的集数匹配顺序，以及默认同步阈值和已看检查。改用原生播放器进度事件与异步 API，不加载上游计时器。MIT。 |
| [czy0729/Bangumi](https://github.com/czy0729/Bangumi) | `706ae6f82e5dfa14a507c796c3b90ad50ab380ec` | 参考 `subject/store/action/collection.ts` 与 `progress.ts`：收藏与剧集状态分离，未收藏时不能编辑剧集；“看过”修改单集，“看到”按本季正篇顺序批量标记。MIT。 |
| [OAuth apps for Windows](https://github.com/googlesamples/oauth-apps-for-windows) | `ab9373d1a470e1aecb90fb263f9daf6ebc4ff643` | 适配浏览器、回环回调与 state 验证流程。播放器通过一次性授权码和 SHA-256 校验领取令牌，不包含 App Secret。Apache-2.0。 |

授权后端位于 `src/auth`，使用 [ASP.NET Core OAuth](https://github.com/dotnet/aspnetcore/tree/v10.0.0/src/Security/Authentication/OAuth) 的现有授权处理器完成 state、浏览器 Cookie 校验与令牌交换。应用密钥通过 Docker secrets 注入。HTTPS 使用 Nginx 和 Certbot，并自动续期。

协议依据：[Bangumi 官方授权文档](https://github.com/bangumi/api/blob/master/docs-raw/How-to-Auth.md)。

修改源码位于 `src/player/src/MpvNet.Windows/Bangumi` 和 `WPF/BangumiMatchWindow.*`。许可证原文保留在 `THIRD_PARTY_LICENSES`，NuGet 依赖记录见 `THIRD_PARTY_LICENSES/NuGet/Bangumi-dependencies.json`。
