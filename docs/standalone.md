# mpv-AnimeFusion 使用说明

## 程序与文件

| 文件 | 用途 |
| --- | --- |
| `mpv-AnimeFusion-1.3.0-win-x64.7z` | 播放器便携包 |
| `mpv-AnimeFusion-1.3.0-sources.zip` | 对应版本源码 |
| `SHA256SUMS.txt` | 下载校验值 |
| `mpv-AnimeFusion.exe` | 播放器 |
| `mpv-AnimeFusionManager.exe` | 配置与组件管理器 |
| `app/mpv-AnimeFusionUpdater.exe` | 组件管理命令行工具 |

解压播放器包后运行 `mpv-AnimeFusion.exe`。模型和显卡组件在管理器“组件”页下载。

## 目录

- `mpv-AnimeFusion.exe`：播放器。
- `mpv-AnimeFusionManager.exe`：管理器。
- `app`：运行库、语言资源和组件工具。
- `portable_config`：播放器配置。
- `animejanai`：AI 配置、模型和缓存。
- `docs`：文档与许可证。

## 配置

| 位置 | 内容 |
| --- | --- |
| `portable_config/mpv.conf` | mpv 播放选项 |
| `portable_config/mpv-AnimeFusion.conf` | 播放器前端选项 |
| `portable_config/input.conf` | 快捷键 |
| `portable_config/script-opts/player_ui.conf` | 底栏、时钟及界面设置 |
| `portable_config/script-opts/player_ui_danmaku.conf` | 弹幕脚本选项 |
| `animejanai/animejanai.conf` | AI 后端和处理方案 |

界面语言在管理器全局设置中选择，重启播放器和管理器后生效。自定义方案可设置为默认，也可使用 Ctrl+1–9 切换；Ctrl+0 关闭 AI 处理。

## 弹幕线路与匹配

在“设置 → 弹幕设置 → 弹幕线路”中添加地址，使用上移／下移调整优先级。

首次匹配会并行搜索各线路，使用成功匹配的结果，并按作品和季度记住 API、平台及剧集编号表。后续剧集直接请求同一来源的对应集，不重复搜索作品，也不因弹幕数量少而换源。原来源加载失败或没有对应集时才重新搜索；成功后记住新来源。连载新增集数时先直接刷新原平台的剧集列表。手动搜索中可按线路、季度和平台筛选，再选择集数。

匹配使用作品名称和线路提供的别名，忽略大小写、全半角、标点及文件标签，并允许有限的错字、缺字和多字。年份仅作辅助；季数和集数不做模糊匹配，相近作品无法区分时需手动选择。

线路保存在 `%LOCALAPPDATA%\mpv-AnimeFusion-danmaku.conf`；字号、速度、显示区域及屏蔽设置保存在 `%LOCALAPPDATA%\mpv-AnimeFusion-Danmaku.json`。发布包不包含个人弹幕线路，首次使用需自行添加。

弹幕菜单提供“导入本地弹幕”，字幕菜单提供“导入本地字幕”。与视频同名的 XML 弹幕可自动加载。

弹幕使用恢复版本的 mpv 原生第二字幕轨呈现，设置中保留 30、60、90 FPS 选项。具体实现见[弹幕渲染](danmaku-renderer.md)。

## Bangumi 同步

点击底栏账号图标，在浏览器登录 Bangumi 并确认授权。未登录时显示账号图标，登录后显示圆形头像。无需申请应用或填写令牌。

授权失败后可点击“重新授权”；等待期间也可重新开始授权。

头像上方的面板显示本季封面、标题、评分与当前剧集。点击“收藏”选择想看、看过、在看、搁置或抛弃；收藏后点击剧集格子，“看过”仅标记该集，“看到”标记本季正篇从第一集到所选集。匹配不正确时使用“选择条目”；匹配失败时可重试。

在“设置 → 同步设置”中分别设置自动收藏和自动看过：默认播放至 10% 时收藏为在看，至 90% 时标记当前剧集看过，各自可关闭或调整到 1–100%。已有收藏状态保留，已看剧集不会重复写入。

连接令牌使用 Windows 账号加密，保存在 `%LOCALAPPDATA%\mpv-AnimeFusion\Bangumi\account.bin`。自动同步选项与条目绑定保存在 `portable_config/bangumi.json`。点击头像面板右上角的退出图标删除本机保存的令牌。

## 问题反馈

提交 [Issue](https://github.com/sunuuc/mpv-AnimeFusion/issues) 时附上版本、显卡型号、启用的处理方案和复现步骤。

启动与播放诊断位于 `portable_config/startup-diagnostic.json`、`portable_config/playback-diagnostic.json`，可用于排查播放列表、加载和缓冲问题。
