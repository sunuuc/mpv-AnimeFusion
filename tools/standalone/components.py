"""Prepare the model-free core with the pinned optional component catalog."""
from pathlib import Path
import configparser
import json
import shutil

CATALOG = Path(__file__).with_name('component-catalog.json')

def prepare(app):
    catalog = json.loads(CATALOG.read_text(encoding='utf-8'))
    defaults = configparser.ConfigParser(interpolation=None)
    defaults.read(app/'animejanai/animejanai.conf', encoding='utf-8-sig')
    configured_models = {value for section in defaults.values() for key, value in section.items()
                         if key.startswith('chain_') and '_model_' in key and key.endswith('_name') and value}
    # Reuse the published, hash-pinned packs instead of recreating the same
    # optional downloads for every player release.
    models = {model.relative_to(app).as_posix(): model
              for model in (app/'animejanai/onnx').glob('*.onnx')}
    model_packs = [pack for pack in catalog['packs'] if pack['name'].startswith('upscale-model-')]
    if not model_packs or set(models) != {name for pack in model_packs for name in pack['files']}:
        raise RuntimeError('Pinned model catalog does not match the upstream core')
    for pack in model_packs:
        if sum(models[name].stat().st_size for name in pack['files']) != pack['installed_bytes']:
            raise RuntimeError('Pinned model size does not match the upstream core: '+pack['name'])
        pack['recommended'] = any(models[name].stem in configured_models for name in pack['files'])
    # Recommendations never install a model without the user's Apply action.
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
