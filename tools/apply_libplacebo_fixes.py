"""Apply the pinned AnimeJaNai Windows presentation patches to libplacebo ABI 387."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile

PATCH_DIR = Path(__file__).resolve().parents[1] / 'third_party/libplacebo-patches'


def sha256(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument('source', type=Path)
    cli.add_argument('--record-build', type=Path, help='Record an already patched and compiled Meson build')
    cli.add_argument('--output', type=Path, help='Path to the libplacebo-source.json build record')
    args = cli.parse_args()
    if bool(args.record_build) != bool(args.output):
        cli.error('--record-build and --output must be specified together')
    pin = json.loads((PATCH_DIR / 'UPSTREAM.json').read_text(encoding='utf-8'))
    git = ['git', '-C', str(args.source.resolve())]
    actual = subprocess.check_output([*git, 'rev-parse', 'HEAD'], text=True).strip()
    if actual != pin['source_commit']:
        raise RuntimeError('Unexpected libplacebo source: ' + actual)
    patches = []
    for name, digest in pin['patches'].items():
        patch = PATCH_DIR / name
        if sha256(patch) != digest:
            raise RuntimeError('Unexpected patch contents: ' + name)
        patches.append(str(patch))
    # Some upstream commits amend code introduced by a preceding commit. Build
    # the full tree in a separate index, then apply it to the worktree in one step.
    with tempfile.TemporaryDirectory(prefix='libplacebo-index-') as temp:
        env = dict(os.environ, GIT_INDEX_FILE=str(Path(temp) / 'index'))
        subprocess.run([*git, 'read-tree', 'HEAD'], env=env, check=True)
        for patch in patches:
            subprocess.run([*git, 'apply', '--cached', '--ignore-space-change', patch], env=env, check=True)
        diff = subprocess.check_output([*git, 'diff', '--cached', '--binary', '--no-ext-diff', 'HEAD'], env=env)
    apply = [*git, 'apply', '--ignore-space-change']
    if not args.record_build:
        subprocess.run([*apply, '--check'], input=diff, check=True)
        subprocess.run(apply, input=diff, check=True)
        return

    subprocess.run([*apply, '--check', '--reverse'], input=diff, check=True)
    build = args.record_build.resolve()
    info = build / 'meson-info'
    configuration = (build / 'src/include/libplacebo/config.h').read_text(encoding='utf-8')
    dll = build / 'src/libplacebo-387.dll'
    if b'Draining NVIDIA swapchain before replacement' not in dll.read_bytes():
        raise RuntimeError('NVIDIA Windows recreation path missing from the runtime')
    options = json.loads((info / 'intro-buildoptions.json').read_text(encoding='utf-8'))
    recorded = ('default_library', 'optimization', 'debug', 'b_ndebug', 'buildtype')
    pin.update(sha256=sha256(dll), features=sorted(re.findall(r'^#define PL_HAVE_(\w+) 1$', configuration, re.M)),
               options={opt['name']: opt['value'] for opt in options if opt['name'] in recorded})
    args.output.write_text(json.dumps(pin, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
