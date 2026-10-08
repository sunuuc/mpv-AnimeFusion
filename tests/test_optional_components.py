"""Model-free packaging and component-manager regressions; no desktop interaction."""
from pathlib import Path
import json
import sys
import tempfile
import unittest
import hashlib
import shutil
import subprocess
import argparse
from unittest.mock import patch
import io

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT/'tools/standalone'))
from components import prepare, validate
from build import copy_validation_reports, release_notes, download, source_release_files, clean_session_files

class ComponentContracts(unittest.TestCase):
    def test_bangumi_sources_and_licenses_are_packaged(self):
        files={p.relative_to(ROOT).as_posix() for p in source_release_files(('src','tests','docs','THIRD_PARTY_LICENSES'))}
        for name in ('src/player/src/MpvNet.Windows/Bangumi/BangumiClient.cs',
                     'src/player/src/MpvNet.Windows/WPF/BangumiSyncWindow.xaml',
                     'tests/bangumi/Program.cs','docs/bangumi-sources.md',
                     'THIRD_PARTY_LICENSES/BangumiNet-MIT.txt',
                     'THIRD_PARTY_LICENSES/czy0729-Bangumi-MIT.txt',
                     'THIRD_PARTY_LICENSES/mpv_bangumi_sync-MIT.txt',
                     'THIRD_PARTY_LICENSES/Google-OAuth-Desktop-Apache-2.0.txt',
                     'THIRD_PARTY_LICENSES/NuGet/Bangumi-dependencies.json'):
            self.assertIn(name,files)
    def test_bangumi_personal_configuration_is_not_packaged(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp)
            config=root/'portable_config';config.mkdir()
            for name in ('bangumi.json','bangumi-app.json'):
                (config/name).write_text('private-fixture',encoding='utf-8')
            clean_session_files(root)
            self.assertFalse((config/'bangumi.json').exists())
            self.assertFalse((config/'bangumi-app.json').exists())

    def test_release_page_contains_only_current_version_changes(self):
        import re
        notes=release_notes()
        version=json.loads((ROOT/'release.json').read_text(encoding='utf-8'))['version']
        changelog=(ROOT/'CHANGELOG.md').read_text(encoding='utf-8')
        section=re.search(
            r'(?ms)^## \['+re.escape(version)+r'\][^\n]*\n(.*?)(?=^## |\Z)', changelog)
        self.assertEqual(notes, section.group(1).strip()+'\n')
        self.assertNotIn('Hills Lite',notes)
        self.assertNotIn('首次',notes)

    def test_release_requires_version_changes(self):
        with tempfile.TemporaryDirectory() as temp, patch('build.R', Path(temp)):
            (Path(temp)/'CHANGELOG.md').write_text('# Changes\n## [Unreleased]\n- Pending\n', encoding='utf-8')
            with self.assertRaisesRegex(RuntimeError, 'Missing version changes'):
                release_notes()
    def test_latest_build_input_is_still_hash_verified(self):
        content=b'pinned native input'
        item={'repo':'sunuuc/mpv-AnimeFusion','tag':'latest','name':'native-build-inputs.7z',
              'sha256':hashlib.sha256(content).hexdigest()}
        with tempfile.TemporaryDirectory() as temp,patch('build.R',Path(temp)):
            with patch('build.urllib.request.urlopen',return_value=io.BytesIO(content)) as opened:
                self.assertEqual(download(item).read_bytes(),content)
                self.assertEqual(opened.call_args.args[0].full_url,
                                 'https://github.com/sunuuc/mpv-AnimeFusion/releases/latest/download/native-build-inputs.7z')
            with patch('build.urllib.request.urlopen',return_value=io.BytesIO(b'damaged')) as opened:
                self.assertEqual(download(item).read_bytes(),content)
                opened.assert_not_called()
            (Path(temp)/'downloads'/item['name']).unlink()
            with patch('build.urllib.request.urlopen',side_effect=lambda *a,**kw:io.BytesIO(b'damaged')),patch('build.time.sleep'):
                with self.assertRaisesRegex(RuntimeError,'Download hash mismatch'):download(item)
                self.assertFalse((Path(temp)/'downloads'/item['name']).exists())
    def test_converter_targets_generic_windows_x64(self):
        source=(ROOT/'tools/standalone/build_danmaku_factory.py').read_text(encoding='utf-8')
        self.assertIn("'-target', 'x86_64-windows-gnu'",source)
        self.assertIn("'-mcpu=baseline'",source)
        self.assertIn('-ffile-prefix-map=',source)
    def test_validation_package_excludes_generated_models_and_media(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);source=root/'source';target=root/'reports';source.mkdir()
            for name in ('results.json','test.log','capture.png','suite.txt',
                         'fixture.onnx','fixture.engine','video.y4m','helper.exe'):
                (source/name).write_text('fixture')
            copy_validation_reports(source,target)
            self.assertEqual({p.name for p in target.iterdir()},
                             {'results.json','test.log','capture.png','suite.txt'})
    def test_catalog_covers_hardware_and_downloads_are_pinned(self):
        catalog=json.loads((ROOT/'tools/standalone/component-catalog.json').read_text())
        names={p['name'] for p in catalog['packs']}
        self.assertTrue({'trt-runtime','rife','trt-ptx','trt-sm75','trt-sm80','trt-sm86',
                         'trt-sm89','trt-sm90','trt-sm100','trt-sm120'} <= names)
        for p in catalog['packs']:
            self.assertEqual(len(p['sha256']),64)
            self.assertTrue(p['url'].startswith('https://github.com/the-database/mpv-AnimeJaNai/releases/download/3.6.0/'))
            self.assertGreater(p['bytes'],0)
    def test_component_labels_identify_distinct_gpu_architectures(self):
        catalog=json.loads((ROOT/'tools/standalone/component-catalog.json').read_text())
        packs={p['name']:p for p in catalog['packs']}
        titles=[p['title'] for p in packs.values()]
        self.assertEqual(len(titles),len(set(titles)))
        for name,devices in {'trt-sm80':('A100','A30'), 'trt-sm86':('RTX 30',),
                             'trt-sm100':('B200','GB200'), 'trt-sm120':('RTX 50',)}.items():
            for device in devices:self.assertIn(device,packs[name]['title'])
            self.assertIn('SM'+name[6:],packs[name]['title'])
        self.assertNotIn('RTX 50',packs['trt-sm100']['title'])
        self.assertNotIn('RTX 30',packs['trt-sm80']['title'])
        translations=json.loads((ROOT/'src/manager/AnimeJaNaiConfEditor/LanguageStrings.json').read_text(encoding='utf-8'))['translations']
        for p in packs.values():
            self.assertIn(p['title'],translations)
            self.assertIn(p['description'],translations)
    def test_runtime_size_counts_binary_payload_without_shared_notice(self):
        catalog=json.loads((ROOT/'tools/standalone/component-catalog.json').read_text())
        runtime=next(p for p in catalog['packs'] if p['name']=='trt-runtime')
        self.assertIn('animejanai/inference/DirectML_LICENSE.txt',runtime['files'])
        # Sizes from the pinned upstream archive; its notice is also shipped by the core.
        self.assertEqual(runtime['installed_bytes'],551024+388010096+45488240+2251888+918528)
    def test_ui_uses_component_owner_and_about_tab(self):
        text=(ROOT/'src/manager/AnimeJaNaiConfEditor/Views/MainWindow.axaml').read_text(encoding='utf-8')
        self.assertIn('ComponentManager.Apply',text)
        self.assertIn('ComponentManager.SelectRecommended',text)
        self.assertNotIn('IsChecked="{Binding Selected}" IsEnabled="False"',text)
        self.assertGreater(text.index('Header="{local:Text Key=about}"'),text.index('ComponentManager.Packs'))
        vm=(ROOT/'src/manager/AnimeJaNaiConfEditor/ViewModels/MainWindowViewModel.cs').read_text(encoding='utf-8')
        self.assertNotIn('TensorRtOnly',vm)
        self.assertNotIn('dialog.ShowAsync()',vm[vm.index('private async Task InitializeComponentManagerAsync'):vm.index('private string[] _commonResolutions')])
        conf=(ROOT/'animejanai/animejanai.conf').read_text(encoding='utf-8')
        self.assertIn('default_slot=0',conf)
    def test_builder_does_not_disable_components_or_include_models(self):
        text=(ROOT/'tools/standalone/build.py').read_text(encoding='utf-8')
        self.assertIn('prepare_components(ST,DIST,META,SEVEN)',text)
        self.assertNotIn('records=prune(',text)
        self.assertNotIn('standaloneComponentsNote',text)
        self.assertNotIn('runtime_seed',text)

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def verify_updater(updater, seven, live=False, model_assets=None, package=None):
    with tempfile.TemporaryDirectory(prefix='neko-components-') as temp:
        root=Path(temp);app=root/'package';runtime_dir=app/'app';runtime_dir.mkdir(parents=True)
        # Updater publish directory contains runtime files, but no GUI is launched.
        shutil.copytree(updater.parent,runtime_dir,dirs_exist_ok=True)
        shutil.copy2(seven,runtime_dir/'7za.exe')
        target=runtime_dir/'build-info/standalone/components.json';target.parent.mkdir(parents=True,exist_ok=True)
        def write(packs):target.write_text(json.dumps({'package_version':'3.6.0','packs':packs}),encoding='utf-8')
        def call(*args,ok=0):
            p=subprocess.run([str(runtime_dir/updater.name),*args],capture_output=True,timeout=180)
            if p.returncode!=ok:raise AssertionError((args,p.returncode,p.stdout.decode('utf-8',errors='replace'),p.stderr.decode('utf-8',errors='replace')))
            return p
        def pack(name,content,requires=[],notices=None):
            relative=f'animejanai/onnx/{name}.onnx';path=root/'source'/relative;path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(content)
            files=[relative]
            for notice,data in (notices or {}).items():
                target=root/'source'/notice;target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(data)
                files.append(notice)
            archive=root/(name+'.7z')
            subprocess.run([str(seven),'a','-t7z','-bd',str(archive),*files],cwd=root/'source',stdout=subprocess.DEVNULL,check=True)
            cache=app/'app/.component-downloads'/name;cache.mkdir(parents=True);shutil.copy2(archive,cache/'package.7z')
            return {'name':name,'asset':archive.name,'url':'https://github.com/sunuuc/mpv-AnimeFusion/releases/latest/download/'+archive.name,
                    'sha256':digest(archive),'bytes':archive.stat().st_size,'installed_bytes':len(content),'files':files,
                    'requires':requires,'recommended':False}
        runtime=pack('fixture-runtime',b'test runtime')
        model=pack('fixture-model',b'test model',['fixture-runtime'])
        write([runtime,model])
        state=json.loads(call('--components','--json').stdout)
        assert state['offline'] and not any(p['installed'] for p in state['packs'])
        print('PASS offline discovery and actual GPU detection:',state['gpu'])
        custom=app/'animejanai/onnx/custom.onnx';custom.parent.mkdir(parents=True,exist_ok=True);custom.write_bytes(b'user content')
        call('--install','fixture-model')
        assert (app/model['files'][0]).read_bytes()==b'test model'
        assert all(p['installed'] for p in json.loads(call('--components','--json').stdout)['packs'])
        call('--remove','fixture-runtime',ok=1)
        call('--remove','fixture-model');call('--remove','fixture-runtime')
        assert custom.read_bytes()==b'user content'
        print('PASS dependency install, verification, protected dependency removal and custom-model preservation')
        notice='animejanai/inference/DirectML_LICENSE.txt'
        shared=app/notice;shared.parent.mkdir(parents=True,exist_ok=True)
        core_notice=b'core license\n';shared.write_bytes(core_notice)
        noticed=pack('notice-runtime',b'payload',notices={notice:b'core license\r\n'})
        write([noticed]);call('--install','notice-runtime')
        assert shared.read_bytes()==core_notice
        assert json.loads(call('--components','--json').stdout)['packs'][0]['installed']
        repeated=call('--install','notice-runtime')
        assert b'already installed' in repeated.stdout and b'Downloading' not in repeated.stdout
        assert not (app/'app/.component-downloads/notice-runtime').exists()
        # Simulate replacing the core package with a new copy of its mandatory notice.
        shared.write_bytes(b'updated core notice with a different size\n')
        assert json.loads(call('--components','--json').stdout)['packs'][0]['installed']
        payload=app/noticed['files'][0];payload.unlink()
        assert not json.loads(call('--components','--json').stdout)['packs'][0]['installed']
        # A truncated payload still fails installation detection.
        payload.write_bytes(b'part')
        assert not json.loads(call('--components','--json').stdout)['packs'][0]['installed']
        payload.write_bytes(b'payload');call('--remove','notice-runtime')
        assert shared.exists() and not payload.exists()
        print('PASS shared-license changes preserve installation state, repeated installs stay offline, missing payloads remain uninstalled')
        write([{**noticed,'files':[notice]}]);call('--components','--json',ok=1)
        bad={**model,'files':['../escape.onnx']};write([runtime,bad]);call('--components','--json',ok=1)
        bad={**model,'url':'https://example.com/untrusted.7z'};write([runtime,bad]);call('--install','fixture-model',ok=1)
        print('PASS traversal and untrusted source rejection')
        # A busy second file must roll the already-written first file back.
        first='animejanai/onnx/rollback-one.onnx';second='animejanai/onnx/rollback-two.onnx'
        (root/'source'/first).write_bytes(b'new one');(root/'source'/second).write_bytes(b'new two')
        archive=root/'rollback.7z'
        subprocess.run([str(seven),'a','-t7z','-bd',str(archive),first,second],cwd=root/'source',stdout=subprocess.DEVNULL,check=True)
        rollback={**model,'name':'rollback','requires':[], 'files':[first,second], 'installed_bytes':14,
                  'bytes':archive.stat().st_size,'sha256':digest(archive)}
        cache=app/'app/.component-downloads/rollback';cache.mkdir(parents=True);shutil.copy2(archive,cache/'package.7z')
        (app/first).write_bytes(b'original one');(app/second).write_bytes(b'original two')
        write([rollback])
        import ctypes as C
        kernel=C.WinDLL('kernel32',use_last_error=True)
        kernel.CreateFileW.argtypes=[C.c_wchar_p,C.c_uint32,C.c_uint32,C.c_void_p,C.c_uint32,C.c_uint32,C.c_void_p]
        kernel.CreateFileW.restype=C.c_void_p
        kernel.CloseHandle.argtypes=[C.c_void_p]
        handle=kernel.CreateFileW(str(app/second),0x80000000,0,None,3,0,None)
        assert handle not in (None,C.c_void_p(-1).value)
        try:call('--install','rollback',ok=1)
        finally:kernel.CloseHandle(handle)
        assert (app/first).read_bytes()==b'original one' and (app/second).read_bytes()==b'original two'
        print('PASS failed commit restores existing files')
        cancel=pack('cancel',b'cancel fixture');write([cancel])
        p=subprocess.run([str(runtime_dir/updater.name),'--install','cancel'],input=b'cancel\n',capture_output=True,timeout=20)
        assert p.returncode==3,(p.returncode,p.stdout,p.stderr)
        assert not (app/cancel['files'][0]).exists()
        print('PASS cancellation leaves no partial installed model')
        catalog=json.loads((ROOT/'tools/standalone/component-catalog.json').read_text())
        write(catalog['packs'])
        state=json.loads(call('--components','--json').stdout)
        sm=state['gpu']['sm']
        if state['gpu']['nvidia']:
            kernel='trt-'+sm if 'trt-'+sm in {p['name'] for p in catalog['packs']} else 'trt-ptx'
            assert {p['name'] for p in state['packs'] if p['recommended']}=={'trt-runtime',kernel}
        else:assert not any(p['recommended'] for p in state['packs'])
        print('PASS recommendations select the detected GPU generation without selecting models')
        if model_assets:
            catalog=json.loads((package/'app/build-info/standalone/components.json').read_text())
            models=[p for p in catalog['packs'] if p['name'].startswith('upscale-model-')]
            write(catalog['packs'])
            for p in models:
                assert p['url']=='https://github.com/sunuuc/mpv-AnimeFusion/releases/latest/download/'+p['asset']
                archive=model_assets/p['asset'];assert digest(archive)==p['sha256']
                cache=app/'app/.component-downloads'/p['name'];cache.mkdir(parents=True)
                shutil.copy2(archive,cache/'package.7z')
                call('--install',p['name'])
                assert (app/p['files'][0]).stat().st_size==p['installed_bytes']
                assert next(x for x in json.loads(call('--components','--json').stdout)['packs'] if x['name']==p['name'])['installed']
                call('--remove',p['name']);assert not (app/p['files'][0]).exists()
            print('PASS all separate upstream model assets install, register and remove:',len(models))
        if live:
            catalog=json.loads((ROOT/'tools/standalone/component-catalog.json').read_text())
            p=next(p for p in catalog['packs'] if p['name']=='trt-sm75').copy()
            p['requires']=[] # kernel download fixture does not execute inference
            write([p]);r=call('--install',p['name']);assert b'100%' in r.stdout
            assert (app/p['files'][0]).stat().st_size==p['installed_bytes']
            call('--remove',p['name']);assert not (app/p['files'][0]).exists()
            print('PASS official upstream download, SHA-256, extraction and removal:',p['url'])

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('app',nargs='?');parser.add_argument('--updater',type=Path);parser.add_argument('--seven',type=Path);parser.add_argument('--live',action='store_true');parser.add_argument('--model-assets',type=Path)
    args=parser.parse_args()
    suite=unittest.defaultTestLoader.loadTestsFromTestCase(ComponentContracts)
    if not unittest.TextTestRunner().run(suite).wasSuccessful():raise SystemExit(1)
    if args.app:print('PASS model-free package:',validate(Path(args.app)))
    if args.model_assets and not args.app:parser.error('--model-assets requires the package directory')
    if args.updater:verify_updater(args.updater.resolve(),args.seven.resolve(),args.live,args.model_assets,Path(args.app) if args.app else None)
