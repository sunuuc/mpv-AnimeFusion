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
    def test_actual_acquire_retries_with_the_new_swapchain_semaphore(self):
        fixture = (ROOT / 'tests/native/libplacebo_swapchain_fixture.c').read_text()
        source = (SOURCE / 'src/vulkan/swapchain.c').read_text()
        original = subprocess.check_output(['git', '-C', str(SOURCE), 'show', 'HEAD:src/vulkan/swapchain.c'], text=True)
        compiler = shutil.which('gcc') or str(ROOT / '_probe/build-tools/msys/ucrt64/bin/gcc.exe')
        toolchain = Path(compiler).resolve().parent
        env = dict(os.environ, PATH=str(toolchain) + os.pathsep + os.environ['PATH'])
        (ROOT / '_probe').mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(prefix='swapchain-test-', dir=ROOT / '_probe') as temporary:
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
        # Presentation must be submitted while the normal recording lock is held;
        # capture the actual queue index before the command is submitted/freed.
        submit = source[source.index('static bool vk_sw_submit_frame('):source.index('static void vk_sw_swap_buffers(')]
        self.assertIn('CMD_BEGIN(GRAPHICS)', submit)
        self.assertIn('CMD_SUBMIT(&cmd)', submit)
        self.assertLess(submit.index('int qidx = cmd->qindex'), submit.index('CMD_SUBMIT(&cmd)'))
        self.assertNotIn('pl_vk_steal_cmd', (SOURCE / 'src/vulkan/gpu.c').read_text())


if __name__ == '__main__':
    unittest.main(argv=[sys.argv[0]])
