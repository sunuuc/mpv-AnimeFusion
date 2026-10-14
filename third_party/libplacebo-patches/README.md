# libplacebo Vulkan 修复

保留 libplacebo v7.360.1 的 ABI 360，回补以下原版提交，并在现有 Vulkan 取帧入口处理表面不可用时的超时：

- 基础提交：`cee9b076f2c63104ccfd497fa79c39a867293ec4`
- [e986aaf：交换链过期重建后重新获取信号量](https://github.com/haasn/libplacebo/commit/e986aaf6696c8d16c428d589f3028d79224a75d2)
- [de357dc：在 GPU 录制锁内提交呈现命令](https://github.com/haasn/libplacebo/commit/de357dcdd817b72e4acf81fcb0acff0a60ef5cf4)
- `bounded-acquire.patch`：本项目修改，取帧最多等待一秒；超时释放交换链锁并沿用原有返回失败、后续重试的流程。遵循 [Vulkan 表面前进保证要求](https://docs.vulkan.org/refpages/latest/refpages/source/vkAcquireNextImageKHR.html)，正常取得图像时立即返回，不额外等待。

通过 `python tools/apply_libplacebo_fixes.py <libplacebo-source>` 应用。原项目采用 LGPL-2.1-or-later，补丁保留上游作者与提交说明。播放器继续使用现有 mpv 视频输出、弹幕渲染和模型处理链。

## 构建

使用原项目的 Meson 构建，保留 Vulkan、D3D11 和 OpenGL 后端：

```sh
meson setup libplacebo-build libplacebo-source --buildtype=release --default-library=shared --wrap-mode=nodownload -Ddemos=false -Dtests=false -Dbench=false -Dfuzz=false -Dvulkan=enabled -Dd3d11=enabled -Dopengl=enabled -Dshaderc=enabled -Dxxhash=enabled
meson compile -C libplacebo-build
```

将生成的 `libplacebo-360.dll` 放入新的原生构建输入目录，并记录基础提交、两项上游提交、本项目取帧补丁和 DLL 的 SHA-256。通过现有 `MPV_NATIVE_OUTPUT` 入口组装原生输入；下一次发行前更新 `dependencies.json` 中的原生输入锁定，避免使用旧输入覆盖修复。原生输入仍须包含原有 mpv、libmpv、libass 及其依赖。

`tests/test_libplacebo_swapchain.py` 对原始、未限定等待与修复后的实际取帧函数进行对照，覆盖正常、交换链过期重建、信号量选择、设备丢失、超时和暂未就绪；这项非桌面测试不等同于显卡驱动上的后台或全屏实测。
