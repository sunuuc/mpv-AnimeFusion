"""Build the offline catalog and optional model assets using upstream component packs."""
from pathlib import Path
import hashlib
import json
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[2]
CATALOG = Path(__file__).with_name('component-catalog.json')

def sha(path):
    with path.open('rb') as f:
        return hashlib.file_digest(f, 'sha256').hexdigest()

def prepare(app, dist, meta, seven):
    catalog = json.loads(CATALOG.read_text(encoding='utf-8'))
    dist.mkdir(parents=True, exist_ok=True)
    assets = []
    # The upstream core ships its small upscaling models. Split each one so the
    # user can choose a model rather than downloading the whole collection.
    for number, model in enumerate(sorted((app/'animejanai/onnx').glob('*.onnx')), 1):
        name = f'upscale-model-{number}'
        archive = dist/f'component-{name}.7z'
        archive.unlink(missing_ok=True)
        relative = model.relative_to(app).as_posix()
        subprocess.run([str(seven), 'a', '-t7z', '-mx=3', '-bd', str(archive.resolve()), relative],
                       cwd=app, stdout=subprocess.DEVNULL, check=True)
        entry = {'name': name, 'asset': archive.name,
                 'url': f'https://github.com/sunuuc/mpv-AnimeFusion/releases/latest/download/{archive.name}',
                 'sha256': sha(archive), 'bytes': archive.stat().st_size,
                 'installed_bytes': model.stat().st_size, 'files': [relative],
                 'requires': [], 'recommended': False, 'title': model_title(model.stem),
                 'description': model.stem}
        catalog['packs'].append(entry)
        assets.append({'repo': 'sunuuc/mpv-AnimeFusion', 'tag': meta['tag'],
                       'name': archive.name, 'sha256': entry['sha256'], 'bytes': entry['bytes']})
    if not assets:
        raise RuntimeError('No upscaling models found in the pinned upstream core')
    # Model selection is explicit: no model is marked for automatic installation.
    for folder in ('onnx', 'rife'):
        shutil.rmtree(app/'animejanai'/folder, ignore_errors=True)
    # These files are downloaded from the pinned official upstream release.
    for pack in catalog['packs']:
        if pack['name'].startswith('upscale-model-'):
            continue
        for name in pack['files']:
            if 'LICENSE' not in name.upper():
                (app/name).unlink(missing_ok=True)
    (app/'animejanai/inference/gpu-target.json').unlink(missing_ok=True)
    target = app/'app/build-info/standalone/components.json'
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(catalog, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    return assets

def model_title(name):
    if '_SD_' in name:
        return 'SD upscaling model'
    sharp = 'Sharp' in name
    if '_Performance_' in name:
        return 'HD upscaling: fast (sharp)' if sharp else 'HD upscaling: fast'
    if '_Balanced_' in name:
        return 'HD upscaling: balanced (sharp)' if sharp else 'HD upscaling: balanced'
    return name

def validate(app):
    if list(app.rglob('*.onnx')) or list(app.rglob('*.engine')):
        raise RuntimeError('Core distribution must not contain AI models or engine caches')
    catalog = json.loads((app/'app/build-info/standalone/components.json').read_text(encoding='utf-8'))
    if not any(p['name'].startswith('upscale-model-') for p in catalog['packs']):
        raise RuntimeError('Missing optional model catalog')
    for pack in catalog['packs']:
        if any((app/f).exists() for f in pack['files']):
            # DirectML license is also a mandatory core notice, not a runtime.
            if any((app/f).exists() and 'LICENSE' not in f.upper() for f in pack['files']):
                raise RuntimeError('Optional component bundled: '+pack['name'])
    for name in ('aji.dll', 'aji_trt.dll', 'aji_dml.dll', 'DirectML.dll', 'onnxruntime.dll'):
        if not (app/'animejanai/inference'/name).is_file():
            raise RuntimeError('Missing inference backend: '+name)
    return {'distribution': 'portable', 'models_bundled': 0,
            'optional_components': len(catalog['packs']), 'hardware_specific_kernel_bundled': False}
