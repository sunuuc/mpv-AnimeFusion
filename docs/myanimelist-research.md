# MyAnimeList 同步研究

研究日期：2026-10-11。范围为官方授权与同步契约、开源识别代码及公开目录数据；MAL 原生客户端及 Docker 授权服务已接入。未操作桌面或写入用户账号记录。

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

本轮读取公开 `anime_map.json`，核到：

| 作品 | Bangumi ID | MAL ID |
| --- | --- | --- |
| 摇曳百合 | 14588 | 10495 |
| 摇曳百合♪♪ | 28900 | 12403 |
| 摇曳百合 3☆High! | 127573 | 30279 |

另读取 [MAL 12403 的公开目录页](https://myanimelist.net/anime/12403)，标题为 `Yuru Yuri♪♪`，集数为 12。这说明该作品已有可用的跨站候选；不是对本播放器自动匹配或账号同步的实测证明。

`manami-project/anime-offline-database` 当前仓库已归档（2026-07-04），不宜作为新番唯一且持续更新的数据来源。

## 本次实现

1. 优先使用视频元数据中明确的 MAL ID 或用户已经确认的条目对应关系。
2. 视频元数据提供 Bangumi ID 时，读取公开跨站 ID 数据取得 MAL 候选；无 ID 时对其中的原名与中文名评分，核对数据的季度、目标名称、首播年月、作品类型和集数范围；不要求用户额外登录 Bangumi。
3. 没有 ID 对应时，从公开作品资料取得原名与别名，搜索 MAL 并核对候选。年份仅辅助区分重制等情况，不代表季度编号。
4. 使用现有 AnitomySharp 提取季集；当前对连续编号与拆分条目要求手动确认当前条目和集数，保存明确的编号差值，不猜测跨季偏移。Anime Relations 为后续可参考的数据，当前未使用其代码或分发其数据。
5. 确认后按作品与季度保存 MAL ID、集数对应关系，后续集数直接核对该条目。
6. 达到观看阈值时读取最新个人列表状态，再更新必要字段；普通重看不能倒退进度，也不覆盖评分、标签等无关字段。若总集数未知，不根据最后一个本地文件推断作品已完结。

截图提供的公开 Client ID 已成功查询 MAL 官方 `/v2/anime/12403`：独立条目、原名与别名、TV 类型、首播日期及 12 集均返回。编译通过并部署域名服务。未执行用户浏览器登录、令牌交换、个人列表读写或视频播放，不宣称这些流程实测成功，也没有匹配成功率统计。

## 申请表建议

- 名称：`mpv-AnimeFusion`。
- 类型：`Other`／其他。
- 描述：`A desktop video player for anime that synchronizes watching status and episode progress with MyAnimeList after user authorization.`
- 主页：`https://github.com/sunuuc/mpv-AnimeFusion`。
- 回调：`https://mal.sunuuc.de5.net/oauth/callback`。使用用户指定的独立域名，沿用现有服务器；该 MAL 回调服务已通过现有 Docker 与 Nginx 部署。域名 A 记录指向 `173.249.220.97`。
- 商业／非商业：非商业；使用目的：业余爱好者；姓名填写申请者本人的姓名或开发者名称。
- 应用标志、隐私政策和使用条款属于可选地址，填写真实已有页面；没有时留空。

实现沿用现有服务框架的授权扩展、一次性交接、原生 HTTP、用户数据事件和头像面板，不新增渲染技术栈。


## 来源与服务记录

- 服务 `mal-auth`，独立数据保护目录 `mal-data`，本地交接端口 33167；浏览器授权体验沿用 Bangumi/Trakt。
- 2026-10-11 修复部署时 Nginx 文件绑定仍读取旧配置的问题：MAL 域名曾落到默认 Trakt 虚拟主机，失败返回 33166。重建 Nginx 挂载后，域名明确到达 MyAnimeList，授权入口指向官方 MAL 域名及正确回调。后续更新需在容器内核对挂载内容；健康响应增加 provider，防止把其他服务的 ready 响应误认成成功。
- ASP.NET Core OAuth 原始处理器通过 `AddOAuth<TOptions,THandler>`、`BuildChallengeUrl` 和 `ExchangeCodeAsync` 扩展；框架负责 state、回调与关联 Cookie，MAL 扩展只调整公开客户端验证、plain PKCE 和不含 Secret 的表单交换。
- 来源：[ASP.NET Core OAuthHandler](https://github.com/dotnet/aspnetcore/blob/main/src/Security/Authentication/OAuth/src/OAuthHandler.cs)、[OAuthOptions](https://github.com/dotnet/aspnetcore/blob/main/src/Security/Authentication/OAuth/src/OAuthOptions.cs)，MIT。没有引入替代授权框架。
- `third_party/bangumi-ext-linker/anime-map.json` 来源于 https://rhilip.github.io/BangumiExtLinker/data/anime_map.json ，获取于 2026-10-11，仅压缩 JSON 排版，未修改对应关系。Rhilip/BangumiExtLinker，CC BY 4.0；保留项目声明和上游来源于同目录 `README.md`，许可原文为 `THIRD_PARTY_LICENSES/BangumiExtLinker-CC-BY-4.0.txt`。
- 匹配对应关系与阈值位于个人 `portable_config/mal.json`；账号令牌使用 DPAPI，不随发行包分发。普通自动同步不倒退已看集数，手动“看到”可调整累计进度；未知总集数不自动判完结。

## 标题匹配修复（2026-10-11）

MAL 候选评分原先直接比较中文原名，文件名省略音乐符号等装饰时，四字标题会被距离评分排除。标题评分和跨站名称校验现统一使用 Unicode FormKC、忽略空白与标点符号、忽略大小写；年份、季度、条目类型与集数范围继续单独核对，不添加作品专用规则。

使用实际播放器程序集调用匹配入口和 MAL 官方只读接口，修复前最近播放的 `摇曳百合 (2012) S2E1 - 回归的主角.mp4` 返回无候选，修复后返回第二季条目 12403。第一季仍返回 10495，第二季第 13 集仍不匹配。验证使用隔离配置，不更改正式匹配记录，不播放视频或提交观看进度。

## 头像与封面修复（2026-10-11）

mpv 原版 `player/command.c` 的 `cmd_overlay_add` 拒绝 `id >= 64`，`DOCS/man/input.rst` 规定位图 ID 为 0–63。MAL 头像原先使用 64，添加失败后又调用删除所有头像与封面的函数，导致头像无法显示并带动其他图片反复消失。现改用空闲 ID 60，单个头像失败只清理自身；同步事件不再先删除全部图片，不变的状态不触发重复刷新，退出或关闭对应入口时再移除图片。继续使用原有 mpv `overlay-add`、`overlay-remove` 和状态事件，不增加轮询。

正式账号已保存 MAL 头像 URL，缓存为有效的 64×64 BGRA 图片，图片并非未下载。MAL 官方接口使用 `num_watched_episodes` 更新累计观看集数，因此剧集菜单的“看到”设置截至所选集的累计进度，不提供无法表示的独立单集状态。
