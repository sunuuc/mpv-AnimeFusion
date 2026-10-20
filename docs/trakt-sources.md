# Trakt 同步源码来源

## 官方契约与授权

- [trakt/trakt-api](https://github.com/trakt/trakt-api)，MIT，Copyright (c) 2024 Trakt Inc.：`search`、`shows`、`sync/history`、`users/settings` 的请求与响应结构。移植到 `TraktModels.cs` 和 `TraktClient.cs`；原许可见 `THIRD_PARTY_LICENSES/trakt-api-MIT.txt`。
- [Trakt PKCE](https://developer.trakt.tv/docs/pkce)、[OAuth](https://developer.trakt.tv/docs/authentication-oauth)、[Search](https://developer.trakt.tv/docs/search)、[Images](https://developer.trakt.tv/docs/images)、[Rate Limits](https://developer.trakt.tv/docs/rate-limiting)：接入时按 2026-10-11 的官方文档使用 PKCE、轮换刷新令牌、精确及别名搜索、图片缓存与速率限制。
- [ASP.NET Core OpenID Connect](https://github.com/dotnet/aspnetcore)，MIT：服务器沿用现有授权服务的框架处理状态、关联 Cookie、授权码交换和 PKCE。Trakt 实例与 Bangumi 实例分别运行，只有 Trakt 实例启用 OpenID Connect；没有播放器中的 Client Secret。
- [Google OAuth Windows 示例](https://github.com/googlesamples/oauth-apps-for-windows)，Apache-2.0：沿用现有浏览器与回环交接流程。回调只向固定本地监听地址传递短期、一次性、带证明的交接码。

## 匹配

- [AnitomySharp](https://github.com/chu-shen/AnitomySharp)，MPL-2.0，固定提交 `579d94335dff23e3f44f72fa169ecadf03d039dc`。原 C# 文件完整保留于 `third_party/anitomysharp`，本地修正末段分词和分隔符合并的边界检查，未添加作品解析规则；其来源为 [erengy/anitomy](https://github.com/erengy/anitomy) 和 AnitomyJ。通过公开 `Parse` API 提取字幕组、标题、年份、季度和集数，供 Bangumi 与 Trakt 共用。
- 已有 `mpv_bangumi_sync` 移植代码继续处理标题别名和候选置信度；Trakt 使用同一个标题评分函数。新增逻辑读取 mpv 的现有元数据 ID，按官方 ID lookup 接口匹配，检查所选季度与剧集是否真实存在。
- 研究参考：[iamkroot/trakt-scrobbler](https://github.com/iamkroot/trakt-scrobbler) 的文件名分析、ID 缓存和剧集定位；[dyphire/trakt-scrobble](https://github.com/dyphire/trakt-scrobble) 的 mpv 使用场景。未复制这两个项目的运行代码或增加 Python、Lua HTTP 实现。

## 本地状态与图像

原生 mpv 的文件、标题和播放进度事件触发匹配及阈值同步，不添加定时网络查询。客户端通过当前账号的观看状态避免重复新增历史；电影与剧集使用 Trakt 的历史接口，收藏不作为写入观看记录的前置条件。

手动确认的季度和集数映射分别记录：当文件编号与 Trakt 的分季编号不同，保存用户确认的编号差值，后续剧集使用该映射核对真实剧集；不会自动推测跨季度偏移。

账号使用 Windows DPAPI 加密；Trakt 刷新令牌串行交换并在后续请求前原子保存。同步开关、个人匹配与账户数据不进入发行包。未兼容或迁移历史 Trakt 格式，本次为第一版。

头像与封面复用管理器已使用的 SkiaSharp 3.119.4（MIT）进行 WebP 解码和本地缓存，仍交给既有 mpv 位图覆盖接口呈现。许可已保留于 `THIRD_PARTY_LICENSES/NuGet`。

## 服务配置

Trakt 回调为 `https://trakt.sunuuc.de5.net/oauth/callback`，公开 Client ID 配置在客户端和 `src/auth/deploy/compose.yml`，不需要 Client Secret。跨域 origins 留空；播放器通过原生 HTTP 客户端请求。

Docker 服务 `trakt-auth` 使用独立数据保护密钥目录 `trakt-data`，由现有 Nginx 提供 HTTPS。证书续期沿用已有 Certbot 定时任务，包含两个域名。Trakt 的本地交接监听为 `http://127.0.0.1:33166/`，Bangumi 保持原监听端口。
