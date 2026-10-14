// The real vk_sw_start_frame is inserted below. No display or Vulkan device.
#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <inttypes.h>
#include <vulkan/vulkan.h>

struct vk_ctx {
    VkDevice dev;
    PFN_vkAcquireNextImageKHR AcquireNextImageKHR;
    PFN_vkWaitForFences WaitForFences;
    PFN_vkResetFences ResetFences;
};
struct vk_swapchain {
    VkSwapchainKHR swapchain;
    struct { VkSemaphore *elem; int num; } sems_in;
    struct { VkFence *elem; int num; } fences_out;
    struct { void **elem; } images;
    int idx_sems_in, last_imgidx;
};
struct priv {
    struct vk_ctx *vk;
    struct vk_swapchain *current;
    struct { bool allow_suboptimal; } params;
    bool suboptimal, needs_recreate;
    int lock;
    int color_repr, color_space;
};
typedef struct swapchain { struct priv *priv; void *gpu; } *pl_swapchain;
struct pl_swapchain_frame { void *fbo; bool flipped; int color_repr, color_space; };
struct release_params { void *tex; int layout; uint32_t qf; struct { VkSemaphore handle; } semaphore; };
#define PL_PRIV(sw) ((sw)->priv)
#define PL_TRACE(...) ((void) 0)
#define PL_ERR(...) ((void) 0)
#define pl_vulkan_release_params(...) (&(struct release_params){__VA_ARGS__})
static void pl_mutex_lock(int *lock) { assert(!*lock); *lock = 1; }
static void pl_mutex_unlock(int *lock) { assert(*lock); *lock = 0; }
static uintptr_t valid_semaphore;
static int calls, mode, invalid_uses;
static VkSemaphore semaphores[1];
static void *images[1];
static struct vk_swapchain chain;
static bool vk_sw_recreate(pl_swapchain sw, int w, int h) {
    semaphores[0] = (VkSemaphore) ++valid_semaphore;
    chain = (struct vk_swapchain){
        .swapchain = (VkSwapchainKHR) valid_semaphore,
        .sems_in = {semaphores, 1}, .images = {images},
        .last_imgidx = -1,
    };
    sw->priv->current = &chain;
    sw->priv->suboptimal = sw->priv->needs_recreate = false;
    return true;
}
static bool vk_sw_image_init(pl_swapchain sw, int idx) { return true; }
static void pl_vulkan_release_ex(void *gpu, struct release_params *p) {
    assert((uintptr_t)p->semaphore.handle == valid_semaphore);
}
static VKAPI_ATTR VkResult VKAPI_CALL acquire(VkDevice dev, VkSwapchainKHR sw,
    uint64_t timeout, VkSemaphore semaphore, VkFence fence, uint32_t *idx)
{
    calls++;
    if (mode >= 5 && timeout == UINT64_MAX) {
        invalid_uses++;
        return VK_TIMEOUT;
    }
    if ((uintptr_t)semaphore != valid_semaphore) {
        invalid_uses++;
        return VK_ERROR_VALIDATION_FAILED_EXT;
    }
    if ((mode == 1 && calls == 1) || mode == 2)
        return VK_ERROR_OUT_OF_DATE_KHR;
    if (mode == 4)
        return VK_ERROR_DEVICE_LOST;
    if (mode == 5)
        return VK_TIMEOUT;
    if (mode == 6)
        return VK_NOT_READY;
    *idx = 0;
    return mode == 3 ? VK_SUBOPTIMAL_KHR : VK_SUCCESS;
}

/* ACTUAL_ACQUIRE */

int main(void) {
    struct vk_ctx vk = { .AcquireNextImageKHR = acquire };
    for (int repeat = 0; repeat < 1000; repeat++) {
        for (mode = 0; mode < 7; mode++) {
            calls = invalid_uses = 0;
            struct priv p = { .vk = &vk };
            struct swapchain sw = { .priv = &p };
            struct pl_swapchain_frame frame;
            bool ok = vk_sw_start_frame(&sw, &frame);
            bool expected = mode == 0 || mode == 1 || mode == 3;
            if (ok) pl_mutex_unlock(&p.lock);
            if (ok != expected || p.lock || invalid_uses) {
                printf("FAIL mode=%d calls=%d invalid_semaphore_uses=%d\n", mode, calls, invalid_uses);
                return 2;
            }
        }
    }
    puts("PASS 7000 acquire/recreate cases: normal, outdated, repeated outdated, suboptimal, device lost, timeout, not ready");
    return 0;
}
