#include <assert.h>
#include <stdint.h>
#include <stddef.h>

struct chain { int swapchain; };
struct gpu { struct { unsigned vendorID; } props; };
struct state { struct chain *current, *retired; };
struct window { int gpu; };

static unsigned calls;
#define PL_HAVE_WIN32 1
#define PL_DEBUG(...) ((void) 0)
#define VK_NULL_HANDLE 0
#define PL_ARRAY_APPEND(sw, retired, chain) ((retired) = (chain))

static void pl_gpu_flush(int gpu) { assert(gpu && calls++ == 0); }
static void vk_wait_idle(struct gpu *vk) { assert(vk && calls++ == 1); }
static void cleanup_retired_swapchains(struct window *sw, uint64_t timeout) {
    assert(sw && timeout == UINT64_MAX && calls++ == 2);
}
static void swapchain_destroy(struct window *sw, struct chain **previous, uint64_t timeout) {
    assert(sw && *previous && timeout == UINT64_MAX && calls++ == 3);
    *previous = NULL;
}

static int replace(unsigned vendor, struct chain *existing) {
    struct gpu device = { .props.vendorID = vendor }, *vk = &device;
    struct state state = { .current = existing }, *p = &state;
    struct window window = { .gpu = 1 }, *sw = &window;
    struct chain *current = existing;
    struct { int oldSwapchain; } sinfo = {0};
    /* ACTUAL_REPLACEMENT */
    assert(p->retired == (sinfo.oldSwapchain ? existing : NULL));
    return sinfo.oldSwapchain;
}

int main(void) {
    struct chain previous = { .swapchain = 42 };
    assert(replace(0x10DE, NULL) == 0 && calls == 0);
    assert(replace(0x1002, &previous) == 42 && calls == 0);
    assert(replace(0x8086, &previous) == 42 && calls == 0);
    assert(replace(0x10DE, &previous) == 0 && calls == 4);
    return 0;
}
