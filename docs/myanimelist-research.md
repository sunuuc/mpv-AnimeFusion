# MyAnimeList 同步源码来源

## 结论

MAL 官方 API 支持浏览器授权、查询作品、读取个人状态及更新已看集数。具备实现同步的条件，但不能把任意中文文件名直接搜索后取第一项作为可靠匹配。

MAL 的每个动画条目有自己的 ID 和集数；第二季、分割播出部分、OVA 等可能各自占一个条目。`start_season` 表示播出的年份与春夏秋冬，不表示系列的第几季。数据库关系和集数范围必须单独核对。

官方同步字段是条目的已看集数和观看状态，不能等同于 Bangumi 或 Trakt 的每集独立看过状态。乱序观看或中途跳过的集数，不能仅靠一个已看集数完整表达。

## 官方接口与授权

- [官方授权说明](https://myanimelist.net/apiconfig/references/authorization)：OAuth 授权码加 PKCE，目前只支持 `plain`。不能直接使用 Trakt 的 `S256` 配置。
- [官方 API v2](https://myanimelist.net/apiconfig/references/api/v2)：公开查询可使用 `X-MAL-CLIENT-ID`；个人列表查询与修改使用用户 Bearer token。
- 授权入口为 `https://myanimelist.net/v1/oauth2/authorize`，令牌入口为 `https://myanimelist.net/v1/oauth2/token`。令牌请求为表单编码，使用随机 state，并验证回调。
- `GET /v2/anime?q=...` 查询候选；详情字段可请求 `alternative_titles,start_date,media_type,num_episodes,related_anime,my_list_status`。
- `PATCH /v2/anime/{id}/my_list_status` 更新 `num_watched_episodes` 和 `status`，仅修改提交的字段；条目不存在时也能添加。
- 列表详情返回字段名为 `num_episodes_watched`，写入参数为 `num_watched_episodes`，两者不同。
- Other 类型适合本桌面应用的公开客户端方式。官方文档允许没有 Client Secret 的客户端；[Taiga 的当前授权源码](https://github.com/erengy/taiga/blob/develop/src/sync/myanimelist/myanimelist_auth.cpp) 交换及刷新令牌时只传 Client ID，不传 Secret。

## 可参考的开源实现

### Taiga

[erengy/taiga](https://github.com/erengy/taiga)，GPL-3.0。使用 Anitomy 提取文件标题、季、集等信息，索引原名、英语名、日语名、别名和用户确认的名字，验证候选后识别。

具体源码：

- [已有识别流程](https://github.com/erengy/taiga/blob/develop/src/v1/track/recognition.cpp)：文件名、目录标题、名字索引、候选评分、季度信息。
- [已有集数对应流程](https://github.com/erengy/taiga/blob/develop/src/v1/track/recognition_relations.cpp)：读取现成对应表，将连续集数换成目标条目及其集数。
- 新版 `src/track/recognition_validate.cpp` 的集数验证仍有调用对应表的 TODO；不能把正在重写的这一段视为已经接好全部流程。需要结合已有完整实现审查。

本播放器已经有 AnitomySharp；本次接入沿用同一个解析入口，不需要引入 Qt 或另一套常驻进程。

### Anime Relations

[erengy/anime-relations](https://github.com/erengy/anime-relations)，公有领域数据。明确记录来源 MAL ID、集数范围、目标 MAL ID 和目标集数范围，处理连续编号、拆分条目及特别篇。研究时数据的 `last_modified` 为 2026-08-03。

这是社区维护的通用数据，不应在播放器代码中增加某部作品的判断。未覆盖的情况需要确认，不能根据播放年份或前作集数自行猜偏移。

### BangumiExtLinker

[Rhilip/BangumiExtLinker](https://github.com/Rhilip/BangumiExtLinker)，项目声明数据 CC BY 4.0，并列出上游来源。提供 Bangumi ID 与 MAL ID 的对应数据，可用于中文名称与跨站识别。

项目明确说明：一个 Bangumi 条目被其他站点拆成多个条目时，可能只记录其中一个。因此对应表只能提供候选 ID，仍须校验目标条目的类型、日期、关系和集数范围。

## 匹配与同步

1. 优先使用视频元数据中明确的 MAL ID 或用户已经确认的条目对应关系。
2. 视频元数据提供 Bangumi ID 时，读取公开跨站 ID 数据取得 MAL 候选；无 ID 时对其中的原名与中文名评分，核对数据的季度、目标名称、首播年月、作品类型和集数范围；不要求用户额外登录 Bangumi。
3. 没有 ID 对应时，从公开作品资料取得原名与别名，搜索 MAL 并核对候选。年份仅辅助区分重制等情况，不代表季度编号。
4. 使用现有 AnitomySharp 提取季集；当前对连续编号与拆分条目要求手动确认当前条目和集数，保存明确的编号差值，不猜测跨季偏移。Anime Relations 为后续可参考的数据，当前未使用其代码或分发其数据。
5. 确认后按作品与季度保存 MAL ID、集数对应关系，后续集数直接核对该条目。
6. 达到观看阈值时读取最新个人列表状态，再更新必要字段；普通重看不能倒退进度，也不覆盖评分、标签等无关字段。若总集数未知，不根据最后一个本地文件推断作品已完结。

## 授权与数据来源

授权服务通过 ASP.NET Core OAuth 的 `AddOAuth<TOptions,THandler>`、`BuildChallengeUrl` 和 `ExchangeCodeAsync` 扩展公开客户端验证与 plain PKCE；框架负责 state、回调与关联 Cookie。实现位于 `src/auth/MyAnimeListHandler.cs`，配置见 `src/auth/README.md`。

跨站数据来自 [Rhilip/BangumiExtLinker](https://github.com/Rhilip/BangumiExtLinker)，许可 CC BY 4.0。数据与上游署名保留在 `third_party/bangumi-ext-linker`；许可原文为 `THIRD_PARTY_LICENSES/BangumiExtLinker-CC-BY-4.0.txt`。Taiga 仅为参考，未复制其 GPL 源码。

标题评分统一使用 Unicode FormKC，忽略空白、标点、符号与大小写；年份、季度、条目类型与集数范围独立核对。头像使用现有 SkiaSharp 解码与 mpv 位图覆盖接口，ID 限定在 0–63，每个服务分别维护图片生命周期；不变的账号状态不重复刷新。
