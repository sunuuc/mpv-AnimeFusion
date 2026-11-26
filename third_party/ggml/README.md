# Windows 共享 GGML

Fusion 使用 MSYS2 UCRT64 的 ggml 1.0.0 ABI。Windows 共享库安装全局 C++ 终止处理程序后，卸载 DLL 会留下指向已卸载代码的指针。AnimeJaNai 的修复让 Windows 共享构建将这个处理程序留给宿主，保留 GGML 自身的断言与回溯。[GGML 源码](https://github.com/ggml-org/ggml/tree/417a024b4fb6bc3116d41af7c067166a039bdf12) · [MSYS2 构建配方](https://github.com/msys2/MINGW-packages/blob/master/mingw-w64-ggml/PKGBUILD)

补丁与加载／卸载测试取自 AnimeJaNai 3.12.5 的 Windows 构建，对应 [修复](https://github.com/the-database/mpv-winbuild/blob/ca38487e28304e4af70de6cee243469883c0f8b5/test-build/ggml-shared-terminate.patch)、[测试](https://github.com/the-database/mpv-winbuild/blob/ca38487e28304e4af70de6cee243469883c0f8b5/test-build/ggml-reload.cpp) 和 [上游构建程序](https://github.com/the-database/mpv-winbuild/blob/ca38487e28304e4af70de6cee243469883c0f8b5/test-build/build-ggml-reload-fix.py)。Fusion 的构建程序核对 1.0.0 官方源包哈希，使用 UCRT64 的 GCC、Ninja 与 CMake 重建 ggml-base，验证原 DLL 的全部导出和序号，并运行三次加载、卸载测试。

```powershell
python tools/standalone/build_ggml.py --stock runtime/ggml-base-1.dll --source-archive ggml-v1.0.0.tar.gz --work build/ggml --output output/ggml
```

输出的 `ggml-base-1.dll` 和 `ggml-reload.json` 供原生运行库组装时核对。源包使用 [v1.0.0 官方归档](https://github.com/ggml-org/ggml/archive/refs/tags/v1.0.0.tar.gz)，许可证见 [GGML](../../THIRD_PARTY_LICENSES/MSYS2/ggml/LICENSE) 与 [mpv-winbuild](../../THIRD_PARTY_LICENSES/mpv-winbuild-MIT.txt)。
