# mpv-AnimeFusion Manager 简体中文版

这是 [mpv-AnimeFusion](https://github.com/sunuuc/mpv-AnimeFusion) 内置的简体中文管理器，基于上游 `the-database/AnimeJaNaiManager`。

## 目标

- 不改变 AnimeJaNai 的补帧、超分、TensorRT、DirectML 等核心逻辑。
- 将 Manager 的主界面、配置说明、组件管理、常见状态、弹窗等用户可见文字转换为简体中文。
- 尽量把汉化代码与上游业务代码隔离，减少以后同步上游时的冲突。

## 分支

- `main`：保留上游版本，方便同步。
- `zh-CN`：简体中文版本。

## 编译

`zh-CN` 分支包含 GitHub Actions 工作流 `.github/workflows/zh-cn-build.yml`。工作流成功后会生成 Windows x64 自包含版本：

`mpv-AnimeFusionManager-zh-CN-win-x64.zip`

此版本仅用于个人使用；原项目许可证与版权信息保持不变。
