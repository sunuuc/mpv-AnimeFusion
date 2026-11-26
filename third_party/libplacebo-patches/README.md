# Windows 呈现与交换链

Fusion 使用 libplacebo v7.387.0 的动态链接接口。AnimeJaNai 3.12.5 在 Windows 原版静态核心中使用 v7.392.0。本目录从该发行版实际使用的构建提交引入全部四个 libplacebo 补丁。四份文件与上游的 Git blob 一致，可直接按原顺序应用于 ABI 387 的源码。[原版构建任务](https://github.com/the-database/mpv-winbuild/blob/ca38487e28304e4af70de6cee243469883c0f8b5/.github/workflows/mpv.yml) · [原版 Meson 配置](https://github.com/shinchiro/mpv-winbuild-cmake/blob/cd1edc11dc6887a50f705717619d879f5a93a488/packages/libplacebo.cmake)

| 补丁 | 在播放器中的作用 |
|---|---|
| libplacebo-present-queue.patch | 多个渲染队列轮换时，始终在同一队列按顺序呈现画面。 |
| libplacebo-nvidia-rtx-hdr.patch | Windows NVIDIA 设备使用 libplacebo 的队列锁，避免 RTX HDR 开启时在呈现调用中死锁。 |
| libplacebo-nvidia-swapchain-recreate.patch | Windows NVIDIA 重建交换链时先等待此前工作完成并释放旧交换链，再创建新交换链。 |
| libplacebo-opaque-composite-alpha.patch | 应用未要求透明且驱动支持不透明合成时，优先使用不透明交换链，使系统可选择独立翻转、可变刷新率和撕裂呈现。 |
| bounded-acquire.patch | Fusion 保留已有的一秒取帧超时；暂时无法取得画面时释放锁，将控制权交还播放器。 |

另从原版核心所用的 libplacebo 主线引入六项稳定性修复。9a251b0 在跳转、清空队列时保留正在使用的锁和条件变量；e260606 修复 Vulkan 显存分配与回收统计并发访问；9d2abbb 校验上传图像的分量索引；a83aaeb 处理 ICC 非正亮度；0e43522 和 63fdd7d 为各着色器阶段生成对应的声明，避免部分驱动链接失败。[两版主线源码差异](https://github.com/haasn/libplacebo/compare/275f7dea27924cdd34ab2e6c20e448b6bc4fab65...58dfee73d)

ABI 387 的基础提交已经包含 e986aaf 和 de357dc，负责交换链替换后的信号量选择和录制锁内提交。其补丁文件仅供源码溯源。[UPSTREAM.json](UPSTREAM.json) 记录基础提交、原版构建提交和全部十一份实际应用补丁的 SHA-256。上游 libplacebo 许可证为 LGPL-2.1-or-later。

## 构建与校验

从 libplacebo 的固定提交检出源码及其子模块，在 MSYS2 UCRT64 中执行以下命令。配置沿用原版 D3D11、优化级别 3、调试符号以及关闭断言的设置。Fusion 继续使用动态库并启用现有的 Vulkan、D3D11、OpenGL、shaderc 和 xxHash 后端。

~~~sh
python tools/apply_libplacebo_fixes.py libplacebo-source
meson setup libplacebo-build libplacebo-source --buildtype=release --default-library=shared --wrap-mode=nodownload -Ddebug=true -Db_ndebug=true -Dvulkan-registry="$MINGW_PREFIX/share/vulkan/registry/vk.xml" -Ddemos=false -Dtests=false -Dbench=false -Dfuzz=false -Dvulkan=enabled -Dd3d11=enabled -Dopengl=enabled -Dshaderc=enabled -Dxxhash=enabled
meson compile -C libplacebo-build -j 4
python tools/apply_libplacebo_fixes.py libplacebo-source --record-build libplacebo-build --output libplacebo-build/libplacebo-source.json
python tests/test_libplacebo_swapchain.py libplacebo-source
~~~

构建记录包含 DLL 的 SHA-256、补丁校验值、从生成头文件读取的功能及 Meson 编译选项。无窗口回归使用实际取帧和交换链替换代码，覆盖过期重建、设备丢失、超时、首次创建，以及 NVIDIA、AMD 和 Intel 的旧交换链处理。
