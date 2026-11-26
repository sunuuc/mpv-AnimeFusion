"""Build the pinned Windows ggml base DLL with AnimeJaNai's shared-library unload fix."""
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import tarfile

ROOT = Path(__file__).resolve().parents[2]
VERSION = '1.0.0'
COMMIT = '417a024b4fb6bc3116d41af7c067166a039bdf12'
ARCHIVE_SHA256 = '27de4df871da091112a1e4ea0e13be70f364af5da666c1246fb26fde2b21fac0'
URL = f'https://github.com/ggml-org/ggml/archive/refs/tags/v{VERSION}.tar.gz'
PATCH = ROOT / 'third_party/ggml/ggml-shared-terminate.patch'
TEST = ROOT / 'tests/native/ggml-reload.cpp'
DLL = 'ggml-base-1.dll'


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def pe_exports(path):
    """Read names and their ordinals as in mpv-winbuild's native metadata check."""
    data = path.read_bytes()
    def uint(offset, fmt='<I'):
        if offset < 0 or offset + struct.calcsize(fmt) > len(data):
            raise ValueError('Truncated PE export table')
        return struct.unpack_from(fmt, data, offset)[0]

    pe = uint(0x3c)
    optional = pe + 24
    if (data[:2] != b'MZ' or data[pe:pe + 4] != b'PE\0\0' or
            uint(pe + 4, '<H') != 0x8664 or uint(optional, '<H') != 0x20b):
        raise ValueError('Expected a Windows x64 binary')
    sections = optional + uint(pe + 20, '<H')

    def file_offset(rva):
        for index in range(uint(pe + 6, '<H')):
            section = sections + index * 40
            address, size, raw = struct.unpack_from('<III', data, section + 12)
            if address <= rva < address + size:
                return raw + rva - address
        raise ValueError('PE export points outside file-backed sections')

    table = file_offset(uint(optional + 112))
    base, functions, count = struct.unpack_from('<III', data, table + 16)
    if not 0 < count == functions <= 65536:
        raise ValueError('Expected named ggml exports')
    names = file_offset(uint(table + 32))
    ordinals = file_offset(uint(table + 36))
    result = {}
    for index in range(count):
        start = file_offset(uint(names + index * 4))
        end = data.index(b'\0', start)
        result[data[start:end].decode('ascii')] = base + uint(ordinals + index * 2, '<H')
    return result


def version(path):
    with os.add_dll_directory(str(path.parent)):
        library = ctypes.CDLL(str(path))
        library.ggml_version.restype = ctypes.c_char_p
        return library.ggml_version().decode('ascii')


def build(stock, archive, work, output):
    if stock.name.lower() != DLL or version(stock) != VERSION:
        raise ValueError(f'Expected {DLL} from ggml {VERSION}')
    if sha256(archive) != ARCHIVE_SHA256:
        raise ValueError('Incorrect ggml source archive SHA-256')
    work.mkdir(parents=True, exist_ok=True)
    source = work / f'ggml-{VERSION}'
    if source.exists():
        raise ValueError('Choose a fresh ggml work directory')
    with tarfile.open(archive) as bundle:
        bundle.extractall(work, filter='data')
    # A nested work directory must have its own Git root for git apply.
    subprocess.run(['git', 'init', '--quiet', str(source)], check=True)
    subprocess.run(['git', 'apply', '-'], cwd=source, input=PATCH.read_bytes().replace(b'\r\n', b'\n'),
                   check=True)
    binary_dir = work / 'build'
    subprocess.run(['cmake', '-S', str(source), '-B', str(binary_dir), '-G', 'Ninja',
                    '-DCMAKE_C_COMPILER=gcc', '-DCMAKE_CXX_COMPILER=g++',
                    '-DCMAKE_BUILD_TYPE=Release', '-DBUILD_SHARED_LIBS=ON',
                    '-DGIT_EXE=OFF', f'-DGGML_BUILD_COMMIT={COMMIT}-shared-terminate-fix',
                    '-DCMAKE_SHARED_LINKER_FLAGS=-fopenmp', '-DCMAKE_DLL_NAME_WITH_SOVERSION=ON',
                    '-DGGML_BACKEND_DL=ON', '-DGGML_CPU=OFF', '-DGGML_OPENMP=ON',
                    '-DGGML_NATIVE=OFF', '-DGGML_CCACHE=OFF', '-DGGML_BUILD_EXAMPLES=OFF',
                    '-DGGML_BUILD_TESTS=OFF'], check=True)
    subprocess.run(['cmake', '--build', str(binary_dir), '--target', 'ggml-base', '-j', '1'], check=True)
    fixed, = binary_dir.rglob(DLL)
    exports = pe_exports(stock)
    if pe_exports(fixed) != exports:
        raise RuntimeError('Patched DLL exports or ordinals differ from the original')

    probe = work / 'ggml-reload.exe'
    subprocess.run(['g++', '-Wall', '-Wextra', '-Werror', str(TEST), '-o', str(probe)], check=True)
    env = dict(os.environ)
    env.pop('GGML_NO_BACKTRACE', None)
    subprocess.run([str(probe), str(fixed)], env=env, check=True)
    output.mkdir(parents=True, exist_ok=True)
    shutil.copy2(fixed, output / DLL)
    record = dict(version=VERSION, sourceCommit=COMMIT, sourceUrl=URL, sourceSha256=ARCHIVE_SHA256,
                  patchSha256=sha256(PATCH), originalDllSha256=sha256(stock), patchedDllSha256=sha256(output / DLL),
                  exportsPreserved=True, exportCount=len(exports), applicationHandlerAndReloadTestPassed=True)
    (output / 'ggml-reload.json').write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for argument in ('--stock', '--source-archive', '--work', '--output'):
        parser.add_argument(argument, required=True, type=Path)
    args = parser.parse_args()
    build(*(path.resolve() for path in (args.stock, args.source_archive, args.work, args.output)))
