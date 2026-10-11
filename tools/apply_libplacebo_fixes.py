"""Backport the upstream Vulkan swapchain fixes to the pinned ABI 360 runtime."""
from pathlib import Path
import subprocess
import sys

source = Path(sys.argv[1]).resolve()
expected = 'cee9b076f2c63104ccfd497fa79c39a867293ec4'
actual = subprocess.check_output(['git', '-C', str(source), 'rev-parse', 'HEAD'], text=True).strip()
if actual != expected:
    raise RuntimeError('Unexpected libplacebo source: ' + actual)
patches = Path(__file__).resolve().parents[1] / 'third_party/libplacebo-patches'
for name in ('e986aaf.patch', 'de357dc.patch'):
    patch = patches / name
    subprocess.run(['git', '-C', str(source), 'apply', '--check', str(patch)], check=True)
    subprocess.run(['git', '-C', str(source), 'apply', str(patch)], check=True)
print('Applied upstream swapchain semaphore and recording-lock fixes')
