// Verify a shared ggml dependency can unload without changing the application's
// process-wide C++ terminate handler. No inference or graphics device is needed.
#include <exception>
#include <cstdio>
#include <cstdlib>
#include <windows.h>

[[noreturn]] static void application_terminate() { std::abort(); }

int main(int argc, char **argv)
{
    if (argc != 2) return 2;
    const auto original = std::set_terminate(application_terminate);
    for (int i = 0; i < 3; i++) {
        HMODULE module = LoadLibraryA(argv[1]);
        if (!module) {
            std::fprintf(stderr, "LoadLibrary failed: %lu\n", GetLastError());
            return 1;
        }
        if (std::get_terminate() != application_terminate) {
            std::fputs("ggml replaced the application terminate handler\n", stderr);
            return 1;
        }
        if (!FreeLibrary(module) || GetModuleHandleA(argv[1]) ||
            std::get_terminate() != application_terminate) {
            std::fputs("ggml did not unload cleanly with the application handler intact\n", stderr);
            return 1;
        }
    }
    std::set_terminate(original);
    std::puts("PASS ggml load/unload/reload preserves application terminate handler");
    return 0;
}
