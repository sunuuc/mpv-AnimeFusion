# mpv-AnimeFusion 构建

## 本地编译

需要 Windows x64、Git、Python 3.14、.NET SDK 10、Node.js 24、7-Zip 和 GitHub CLI。在仓库根目录执行：

```powershell
python tools/standalone/build.py prepare --validation-scope headless
$version = (Get-Content release.json | ConvertFrom-Json).version
dotnet publish player/src/MpvNet.Windows/MpvNet.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:EnableDiagnostics=false -p:InformationalVersion=$version -o publish-player
dotnet publish manager/AnimeJaNaiConfEditor/AnimeJaNaiConfEditor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:InformationalVersion=$version -o publish-manager
dotnet publish tools/standalone/Updater.csproj -c Release -o publish-updater
python tools/standalone/build.py stage
```

构建前先提交本次源码、版本和发布说明，使用该提交的干净检出目录。`prepare`、`stage`、`package` 和发布步骤均检查真实 Git 提交及跟踪文件状态；发布阶段不生成额外源码提交。`prepare` 会重新建立构建验证目录，旧报告不会沿用。

诊断日志默认关闭。仅本机测试构建使用 `-p:EnableDiagnostics=true`，日志写入播放器配置目录下的 `logs`，记录请求、重试、字幕处理及播放器异常并脱敏。启用日志不改变 `debug-mode`；普通客户端启动时诊断只写文件，显式调试或从终端启动时才输出到控制台。发布时明确使用 `false`；打包检查会拒绝包含诊断标记的播放器构建，并清理测试日志。

本地原生构建使用 `tools/standalone/build_native.py` 固定的 mpv 提交。将它输出的 `native-output` 目录设置为 `MPV_NATIVE_OUTPUT`，再执行 `stage`。该目录包含本项目的弹幕、字幕缓存和播放记录改动及构建记录。

推理库固定为 `animejanai-inference v0.32.0`（ABI 9）。在该标签的干净源码树应用 `tools/standalone/inference-config.patch`。正式版另应用 `tools/standalone/inference-release.patch`。按上游 Windows x64 的 CUDA 13.4、TensorRT 11.4 和多架构设置启用 Vulkan，编译 `aji`、`aji_trt`、`aji_dml`、`aji_vk` 四个目标，将 `AJI_INFERENCE_OUTPUT` 设为生成这四个 DLL 的目录。打包时仍检查构建日志标记。组件清单分别锁定 runtime-2 的 TensorRT 运行库、3.12.5 的 RIFE 模型和项目既有超分模型包，安装包只携带清单。

本地发行保留源码检查、语言与解析器回归、原生无窗口播放、脚本状态机、弹幕包校验、安全扫描及空目录解包复测。默认使用 `headless` 验证范围；涉及真实播放器窗口、管理器窗口、截图和桌面输入的项目明确记录为未执行，不能据此宣称桌面验收通过。获得桌面交互授权后，可在 `prepare` 时明确选择 `--validation-scope desktop` 并执行对应完整检查。

验证通过后执行 `python tools/standalone/build.py package`，再运行 `python tools/standalone/publish.py`。发布要求构建提交已经推送到 `main`，标签直接指向该提交；上传后核对 GitHub 附件摘要，并重新下载公开文件核对 SHA-256。正式发布只上传一个 EXE 安装包和一个 7z 解压包，源码使用 GitHub 随标签提供的 ZIP 和 TAR.GZ。校验清单和测试证据仅保留本地，模型及原生构建输入沿用固定来源，不作为本次发行附件。

GitHub Actions 工作流保留手动触发入口；如需在 Actions 上执行桌面验证，应显式选择对应验证范围。本地构建来源记录为 `local`，不设置或伪造 Actions 工作流编号。

## EXE 安装包

`build.py package` 同时生成解压版和 `mpv-AnimeFusion-版本-setup-x64.exe`。安装定义为 `src/player/src/Setup/Inno/inno-setup.iss`，通过 `tools/standalone/build_installer.py` 调用固定版本 Inno Setup 6.7.3。编译器和解包工具均下载后校验 SHA-256，仅解包到依赖缓存，不向系统安装构建工具。

安装身份独立于原版 AnimeJaNai；默认按用户安装到 `%LOCALAPPDATA%\Programs\mpv-AnimeFusion`，升级沿用已选择的目录。程序、脚本和模块更新；`portable_config` 的根配置、`script-opts` 及 `animejanai.conf` 使用 Inno Setup 的 `onlyifdoesntexist uninsneveruninstall`，只补充缺失默认值，升级保留配置。旧版两份快捷键文件与 1.3.8 原始文件的 SHA-256 相符时更新管理器绑定，修改过的文件保留。卸载初始化时使用 Inno Setup 原生表单提供三项独立删除选项：个人数据、字幕与弹幕、模型与推理组件，默认全部不勾选。分类数据由卸载事件按本次选择清理，不使用安装阶段求值的 `Check` 条件；静默卸载保留个人数据。程序运行目录、程序资料、着色器缓存及诊断日志通过 `[UninstallDelete]` 正常清理，包括安装后更新或生成、未记入安装日志的程序文件，不递归清空安装根目录。安装包编译后单独执行发行安全扫描，并加入本地附件清单和校验文件。

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

使用与编译头文件相同版本的 FFmpeg 运行库。将带 `build-info/libplacebo-source.json` 的 libplacebo 构建目录传给 `build_native.py --libplacebo-dir`，或将这组文件置于 `--runtime` 目录。构建会先用 Meson 将仓库自带的 AnimeJaNai libass 编译到独立目录，再用其头文件和导入库编译 mpv，并组装同次构建的字幕 DLL。原生构建和打包均核对五项字幕渲染能力、Vulkan 补丁清单和 libplacebo DLL 哈希。

管理器中的“增强切镜检测”使用原版 MVTools。原生构建开启 `ajn-mvtools`，将动态加载的 `ajn-mvtools.dll` 和 mpv 一同组装，并附带检测器许可证。

通过 `tools/standalone/build_ggml.py` 编译与当前 ABI 一致的 ggml，向 `build_native.py --ggml-dir` 传入生成 DLL 和 `ggml-reload.json` 的目录。构建时核对导出和卸载行为。本地打包从 `MPV_NATIVE_OUTPUT` 读取通过验证的构建，核对 mpv 提交、配置、源码补丁和运行库逐文件 SHA-256。源码记录、补丁及验证结果随程序保存在 `app/build-info/native`。
