"""Compile and exercise the upstream acquire function with deterministic WSI."""
from pathlib import Path
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SOURCE = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 and Path(sys.argv[1]).is_dir() else ROOT / '_probe/libplacebo-source'


class SwapchainTest(unittest.TestCase):
    def test_windows_replacement_preserves_other_vendors_and_first_creation(self):
        source = (SOURCE / 'src/vulkan/swapchain.c').read_text()
        recreate = source.index('static bool vk_sw_recreate(')
        start = source.index('#ifdef PL_HAVE_WIN32\n    // Avoid overlapping', recreate)
        end = source.index('    p->current = current = pl_zalloc_ptr', start)
        fixture = (ROOT / 'tests/native/libplacebo_recreate_fixture.c').read_text()
        with tempfile.TemporaryDirectory(prefix='placebo-recreate-') as temporary:
            program = Path(temporary) / 'recreate.c'
            output = program.with_suffix('.exe')
            program.write_text(fixture.replace('/* ACTUAL_REPLACEMENT */', source[start:end]))
            compiler = shutil.which('gcc') or str(ROOT / '_probe/build-tools/msys/ucrt64/bin/gcc.exe')
            subprocess.run([compiler, '-std=c11', '-Wall', '-Werror', str(program), '-o', str(output)], check=True)
            subprocess.run([str(output)], check=True, timeout=10)

    def test_actual_acquire_retries_with_the_new_swapchain_semaphore(self):
        fixture = (ROOT / 'tests/native/libplacebo_swapchain_fixture.c').read_text()
        source = (SOURCE / 'src/vulkan/swapchain.c').read_text()
        original = subprocess.check_output(['git', '-C', str(SOURCE), 'show', 'HEAD:src/vulkan/swapchain.c'], text=True)
        compiler = shutil.which('gcc') or str(ROOT / '_probe/build-tools/msys/ucrt64/bin/gcc.exe')
        toolchain = Path(compiler).resolve().parent
        env = dict(os.environ, PATH=str(toolchain) + os.pathsep + os.environ['PATH'])
        with tempfile.TemporaryDirectory(prefix='swapchain-test-') as temporary:
            work = Path(temporary)
            unbounded = source.replace('1000000000ULL', 'UINT64_MAX')
            for name, content in [('unpatched', original), ('unbounded', unbounded), ('patched', source)]:
                start = content.index('static bool vk_sw_start_frame(')
                end = content.index('static void present_cb(', start)
                program = work / (name + '.c')
                program.write_text(fixture.replace('/* ACTUAL_ACQUIRE */', content[start:end]))
                binary = work / (name + '.exe')
                subprocess.run([compiler, '-std=c11', '-O2', str(program), '-o', str(binary)], env=env, check=True, capture_output=True)
                result = subprocess.run([str(binary)], env=env, capture_output=True, text=True, timeout=10)
                print(name + ': ' + result.stdout.strip())
                self.assertEqual(result.returncode, 0 if name == 'patched' else 2, result.stdout + result.stderr)
        # Render queues rotate; the ordered presentation must consistently use queue 0.
        submit = source[source.index('static bool vk_sw_submit_frame('):source.index('static void vk_sw_swap_buffers(')]
        self.assertIn('CMD_BEGIN(GRAPHICS)', submit)
        self.assertIn('CMD_SUBMIT(&cmd)', submit)
        self.assertIn('int qidx = 0;', submit)
        self.assertIn('VkQueue queue = pool->queues[qidx]', submit)
        self.assertIn('vk_rotate_queues(p->vk)', submit)
        self.assertIn('.pWaitSemaphores = &sem_out', submit)
        self.assertNotIn('pl_vk_steal_cmd', (SOURCE / 'src/vulkan/gpu.c').read_text())


if __name__ == '__main__':
    unittest.main(argv=[sys.argv[0]])
