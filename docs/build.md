# mpv-AnimeFusion 构建

## GitHub Actions

在 Actions 中选择 **Standalone portable and installer release**，点击 **Run workflow**。`publish` 控制是否上传正式发行包；关闭时输出构建产物。

## 本地编译

需要 Windows x64、Git、Python 3.14、.NET SDK 10、Node.js 24、7-Zip 和 GitHub CLI。在仓库根目录执行：

```powershell
python tools/standalone/build.py prepare
$version = (Get-Content release.json | ConvertFrom-Json).version
dotnet publish player/src/MpvNet.Windows/MpvNet.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:InformationalVersion=$version -o publish-player
dotnet publish manager/AnimeJaNaiConfEditor/AnimeJaNaiConfEditor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:InformationalVersion=$version -o publish-manager
dotnet publish tools/standalone/Updater.csproj -c Release -o publish-updater
python tools/standalone/build.py stage
```

`stage` 目录为组装后的程序。构建脚本下载并校验固定版本的原生运行库，编译 DanmakuFactory，并生成可选组件目录及独立模型包。播放器前端发布不重新替换原生内核或 libass；其字节由 `dependencies.json` 中的运行库输入哈希固定。

完整发行还需运行工作流中的扫描、回归及空目录安装检查，然后执行 `build.py package` 和 `publish.py`。步骤顺序见[构建工作流](https://github.com/sunuuc/mpv-AnimeFusion/blob/main/.github/workflows/standalone.yml)。

## EXE 安装包

`build.py package` 同时生成解压版和 `mpv-AnimeFusion-版本-setup-x64.exe`。安装定义为 `src/player/src/Setup/Inno/inno-setup.iss`，通过 `tools/standalone/build_installer.py` 调用固定版本 Inno Setup 6.7.3。编译器和解包工具均下载后校验 SHA-256，仅解包到依赖缓存，不向系统安装构建工具。

安装身份独立于原版 AnimeJaNai；默认按用户安装到 `%LOCALAPPDATA%\Programs\mpv-AnimeFusion`，升级沿用已选择的目录。程序、脚本和模块更新；`portable_config` 的根配置、`script-opts` 及 `animejanai.conf` 使用 Inno Setup 的 `onlyifdoesntexist uninsneveruninstall`，只补充缺失默认值，升级和卸载均保留配置。未随包提供的账号、来源记录、模型和个人文件不会被清理。安装包编译后单独执行发行安全扫描，并加入附件清单和校验文件。

已有组装目录可单独编译安装包：

```powershell
python tools/standalone/build_installer.py stage dist/installer complete-evidence/installer
python tools/standalone/security_verify.py scan dist/installer complete-evidence/installer/security
```

## 源码与记录

- `src/player`：mpv.net 播放器前端。
- `src/manager`：配置与组件管理器。
- `portable_config`：播放脚本和默认配置。
- `third_party/danmaku-factory`：固定版本的弹幕排布与转换源码。
- `third_party/libass`：字幕渲染源码。
- `tools/standalone`：依赖、编译、打包与发布工具。
- `app/build-info/standalone`：发行包中的构建来源、文件校验值与验证报告。

工作流仅手动触发。待发布改动先写入 `CHANGELOG.md` 的 `Unreleased`；发布时确定版本号，发布页正文读取 `docs/release-features.md` 的简短功能介绍。旧版本保留。

## Bangumi 授权

播放器通过浏览器授权，应用密钥保存在 Docker 授权服务中。用户无需申请应用或配置密钥。服务部署见 [`src/auth/README.md`](../src/auth/README.md)。

源码来源与修改范围见 [Bangumi 来源记录](bangumi-sources.md)。

## 原生运行库

1.3.0 固定使用恢复到 2026-10-07 18:49 前的原生运行库，与本机恢复版本的 `mpv.exe`、`libmpv-2.dll` 和 `libplacebo-360.dll` 哈希一致。使用 AnimeJaNai 三队列配置，保留当时的第二字幕轨实现。

固定输入、源提交、补丁和逐文件哈希在 `app/build-info/native` 中记录。需要主动更新原生内核时，先单独运行原生构建工作流；`MPV_NATIVE_OUTPUT` 可显式指定候选运行库，但普通前端发布默认使用已固定的输入。
