# Bangumi、Trakt 与 MyAnimeList 授权服务

mpv-AnimeFusion 使用 ASP.NET Core 原生 OAuth / OpenID Connect 处理授权、关联 Cookie 和 PKCE。应用密钥只保留在服务器；浏览器回到播放器时只携带短期、一次性、需要 SHA-256 证明的交接码。

## 域名与回调

新服务器为 `45.133.117.9`，统一入口为 `https://api.sunuuc.de5.net`。三个服务仍使用独立容器和数据保护密钥目录。Nginx 保留路径前缀，ASP.NET Core 的 `UsePathBase` 负责路由及回调地址生成。

| 服务 | 路径前缀 | 平台登记的回调地址 | 播放器本地监听 |
| --- | --- | --- | --- |
| Bangumi | `/bangumi` | `https://api.sunuuc.de5.net/bangumi/oauth/callback` | `http://127.0.0.1:33165/` |
| Trakt | `/trakt` | `https://api.sunuuc.de5.net/trakt/oauth/callback` | `http://127.0.0.1:33166/` |
| MyAnimeList | `/mal` | `https://api.sunuuc.de5.net/mal/oauth/callback` | `http://127.0.0.1:33167/` |

每个前缀下提供 `/authorize`、`/token`、`/refresh`、`/healthz` 和 `/readyz`。`PublicOrigin` 只填 HTTPS 域名，不带路径。健康及就绪响应含 `provider`，验收时必须同时核对服务名；不能仅凭 `status=ready` 判断路由正确。

原 `bangumi.sunuuc.de5.net`、`trakt.sunuuc.de5.net`、`mal.sunuuc.de5.net` 也解析到新服务器，展示“请更新到最新版本”和[发布列表](https://github.com/sunuuc/mpv-AnimeFusion/releases)。下载按钮进入完整发布列表，不跳转到某个版本详情页。旧入口不再发起授权或交换令牌；POST 返回 410。旧服务器已停用。

## 凭据

Bangumi 的应用 ID、密钥分别写入宿主机 `deploy/secrets/app-id`、`deploy/secrets/app-secret`。宿主机的 `secrets` 目录仅允许管理员访问；挂载到容器 `/run/secrets` 的文件需允许容器 UID 1654 读取。授权应用开启收藏 READ、WRITE 权限，不申请目录、论坛或维基写权限。

Trakt 使用官方 OpenID Connect 和 S256 PKCE，MyAnimeList 使用 `Other` 类型公开客户端及 plain PKCE；公开 Client ID 在客户端和 `compose.yml` 中一致配置，不使用 Client Secret。Trakt 的 allowed origins 留空。更换应用 ID 后，原应用签发的账号凭据不能视为已完成迁移。

## 本地构建与部署

先在本机运行回归测试，再发布 Linux 运行文件。产物目录位于源码目录外；服务器只装配运行镜像，不编译代码。以下命令从仓库根目录运行：

```powershell
$stage = 'D:\CODE-ARTIFACTS\mpv-AnimeFusion\auth-deploy'
dotnet run --project tests/auth/AuthChecks.csproj -c Release --artifacts-path "$stage\build"
dotnet run --project tests/bangumi/BangumiChecks.csproj -c Release --artifacts-path "$stage\build" -- --oauth-only
dotnet publish src/auth/AnimeVE.Auth.csproj -c Release -r linux-x64 --self-contained false --artifacts-path "$stage\build" -o "$stage\publish"
Copy-Item src/auth/Dockerfile "$stage\Dockerfile"
Copy-Item src/auth/.dockerignore "$stage\.dockerignore"
Copy-Item src/auth/deploy "$stage\deploy" -Recurse
```

将暂存目录中的 `Dockerfile`、`.dockerignore`、`publish`、`deploy` 上传到新服务器的 `/opt/mpv-animefusion-auth`。升级时保留宿主机的凭据、证书和三个数据目录，不能用整个目录替换覆盖。给 `deploy/data`、`deploy/trakt-data`、`deploy/mal-data` 设置 UID/GID 1654 所有权；这些目录分别挂载为各容器内部的 `/data`。

首次部署先创建 `deploy/acme`、`deploy/certificates`，用 `bootstrap.conf` 临时监听 HTTP，为四个域名通过 Certbot webroot 验证。证书名称统一为 `api.sunuuc.de5.net`，SAN 包含四个域名；容器内部 webroot 为 `/var/www/acme`。HTTPS 正式启动前移除临时 HTTP 容器。已有服务续期时沿用 Compose 中的 Certbot 和定时任务。

```sh
cd /opt/mpv-animefusion-auth/deploy
docker compose up -d --build
cp animeve-auth-renew.service animeve-auth-renew.timer /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now animeve-auth-renew.timer
```

核对运行容器 `/app/AnimeVE.Auth.dll` 的 SHA-256 与本机发布文件一致。逐一检查三个 `/readyz`、授权跳转的回调地址和关联 Cookie 路径；检查三个旧域名的更新页面及下载链接。真实账号的授权确认和刷新仍需独立验收，不能以就绪检查代替。

请求访问日志关闭；凭据、授权码和令牌不写入应用日志。Nginx 只信任 Cloudflare 官方地址段提供的客户端 IP，应用只信任内部 Nginx 地址的转发头。

## 更新 Nginx 配置

`nginx.conf` 与 `update.html` 是文件绑定挂载。原文件原位写入后可以验证并重载；若通过解压、原子重命名等方式替换了宿主机 inode，需先执行 `docker compose up -d --no-deps --force-recreate nginx`，然后运行 `nginx -t`。检查容器内部挂载内容，避免仍读取旧文件。

框架及来源见 `docs/trakt-sources.md`、`docs/myanimelist-research.md`。ASP.NET Core 使用 MIT 许可证；Nginx 与 Certbot 保留官方镜像中的许可证。
