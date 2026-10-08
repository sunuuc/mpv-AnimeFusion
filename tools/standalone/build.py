"""Portable core and optional component asset builder using pinned upstream inputs.
Commands: prepare / stage / package. Publishing is implemented in publish.py.
No command reads or modifies a user's installation.
"""
from pathlib import Path, PurePosixPath
import configparser, hashlib, json, os, re, shutil, subprocess, sys, time, urllib.request, zipfile
R=Path.cwd(); H=R/'tools/standalone'; ST=R/'stage'; DIST=R/'dist'; E=R/'complete-evidence'
META=json.loads((R/'release.json').read_text(encoding='utf-8'))
LOCK=json.loads((H/'dependencies.json').read_text(encoding='utf-8'))
REPO='sunuuc/mpv-AnimeFusion'
from components import prepare as prepare_components, validate as validate_components
from security_verify import require_result as require_security_result
from build_installer import build as build_installer
FONTS={'.ttf','.otf','.ttc','.woff','.woff2','.fon','.fnt'}
SEVEN=shutil.which('7z') or next((str(p) for p in (Path(r'C:\Program Files\7-Zip\7z.exe'),Path(r'D:\Apps\7-Zip\7z.exe')) if p.is_file()),'7z')
SOURCE_ADDITIONS={
    'src/auth/mpv-AnimeFusion.Auth.csproj',
    'src/auth/Program.cs',
    'src/auth/Dockerfile',
    'src/auth/.dockerignore',
    'src/auth/README.md',
    'src/auth/deploy/compose.yml',
    'src/auth/deploy/nginx.conf',
    'src/auth/deploy/bootstrap.conf',
    'src/auth/deploy/animeve-auth-renew.service',
    'src/auth/deploy/animeve-auth-renew.timer',
    'src/player/src/MpvNet.Windows/Bangumi/BangumiStore.cs',
    'src/player/src/MpvNet.Windows/Bangumi/BangumiOAuth.cs',
    'src/player/src/MpvNet.Windows/Bangumi/BangumiClient.cs',
    'src/player/src/MpvNet.Windows/Bangumi/BangumiMedia.cs',
    'src/player/src/MpvNet.Windows/Bangumi/BangumiPlayback.cs',
    'src/player/src/MpvNet.Windows/WPF/BangumiMatchWindow.xaml',
    'src/player/src/MpvNet.Windows/WPF/BangumiMatchWindow.xaml.cs',
    'tests/bangumi/BangumiChecks.csproj',
    'tests/bangumi/Program.cs',
    'docs/bangumi-sources.md',
    'tools/standalone/components.py',
    'tools/standalone/component-catalog.json',
    'tools/standalone/updater-upstream.json',
    'tests/test_optional_components.py',
    'docs/open-source-notices.md',
    'THIRD_PARTY_LICENSES/NVIDIA/CUDA_LICENSE_9215a6ea5a_LICENSE',
    'THIRD_PARTY_LICENSES/NVIDIA/TensorRT_LICENSE_bee83db7cc_Acknowledgements.txt',
    'THIRD_PARTY_LICENSES/NVIDIA/TensorRT_LICENSE.html',
    'THIRD_PARTY_LICENSES/NVIDIA/sources.json',
    'tools/standalone/build_libass.py',
    'tools/standalone/libass-build-packages.json',
    'tools/standalone/security_verify.py',
    'tools/standalone/security-policy.json',
    'tools/standalone/test_security_verify.py',
    'THIRD_PARTY_LICENSES/PCRE2-BSD.txt',
    'tests/manager-profiles/ManagerProfiles.csproj',
    'tests/manager-profiles/Program.cs',
    'tests/danmaku-wpf/DanmakuWpfChecks.csproj',
    'tests/danmaku-wpf/Program.cs',
    'tests/test_danmaku_search_xaml.py',
    'portable_config/script-modules/player_ui_danmaku_online.lua',
    'portable_config/script-modules/player_ui_danmaku_render.lua',
    'tools/standalone/build_danmaku_factory.py',
    'tools/apply_secondary_sub_sync.py',
    'docs/danmaku-renderer.md',
    'portable_config/script-opts/player_ui_danmaku.conf',
    'tests/test_danmaku_online.py',
    'tools/standalone/verify_player_sources.py',
    'src/player/src/MpvNet.Windows/WPF/DanmakuSourcesWindow.xaml',
    'src/player/src/MpvNet.Windows/WPF/DanmakuSourcesWindow.xaml.cs',
    'src/player/src/MpvNet.Windows/WPF/DanmakuSourceEditWindow.xaml',
    'src/player/src/MpvNet.Windows/WPF/DanmakuSourceEditWindow.xaml.cs',
    'src/player/src/MpvNet.Windows/WPF/DanmakuSearchWindow.xaml',
    'src/player/src/MpvNet.Windows/WPF/DanmakuSearchWindow.xaml.cs',
    'src/player/src/MpvNet.Windows/WPF/DanmakuBlocklistWindow.xaml',
    'src/player/src/MpvNet.Windows/WPF/DanmakuBlocklistWindow.xaml.cs',
}

def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
def dump(p,obj):
    p.parent.mkdir(parents=True,exist_ok=True);p.write_text(json.dumps(obj,ensure_ascii=False,indent=2),encoding='utf-8')
def run(*args,**kwargs):return subprocess.run(list(map(str,args)),check=True,**kwargs)
def download(item):
    dest=R/'downloads'/item['name'];dest.parent.mkdir(exist_ok=True)
    if dest.exists() and sha(dest)==item['sha256']:return dest
    release_path='latest/download' if item['tag']=='latest' else 'download/'+item['tag']
    url=f'https://github.com/{item["repo"]}/releases/{release_path}/{item["name"]}'
    for attempt in range(3):
        try:
            req=urllib.request.Request(url,headers={'User-Agent':'mpv-AnimeFusion-full-build'})
            with urllib.request.urlopen(req,timeout=90) as src, dest.open('wb') as out:shutil.copyfileobj(src,out,1024*1024)
            if sha(dest)!=item['sha256']:raise RuntimeError('Download hash mismatch: '+item['name'])
            print('VERIFIED INPUT',item['name'],dest.stat().st_size,flush=True);return dest
        except Exception:
            dest.unlink(missing_ok=True)
            if attempt==2:raise
            time.sleep(2*(attempt+1))
def valid_path(name):
    p=PurePosixPath(name.replace('\\','/'))
    if p.is_absolute() or '..' in p.parts or ':' in name:raise RuntimeError('Unsafe archive path: '+name)
def extract(archive,dest):
    dest.mkdir(parents=True,exist_ok=True)
    if zipfile.is_zipfile(archive):
        with zipfile.ZipFile(archive) as z:
            for i in z.infolist():valid_path(i.filename)
            z.extractall(dest)
    else:
        listing=subprocess.check_output([SEVEN,'l','-slt','-sccUTF-8',str(archive)]).decode('utf-8').replace('\r\n','\n')
        entries=listing.split('----------\n',1)[-1]
        for name in re.findall(r'^Path = (.+)$',entries,re.M):valid_path(name)
        if 'Symbolic Link = ' in entries or 'Hard Link = ' in entries:raise RuntimeError('Links in archive')
        run(SEVEN,'x','-y','-bd',f'-o{dest}',archive,stdout=subprocess.DEVNULL)
def app_root(p):
    roots=[q.parent for q in p.rglob('libmpv-2.dll')]
    if len(roots)!=1:raise RuntimeError('Ambiguous application root: '+str(roots))
    return roots[0]
def cp(src,dst):
    if src.is_dir():shutil.copytree(src,dst,dirs_exist_ok=True)
    else:dst.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(src,dst)

def copy_validation_reports(src,dst):
    """Ship reports, not the generated media, models or executable fixtures."""
    for path in src.rglob('*'):
        if path.is_file() and path.suffix.lower() in {'.json','.log','.png','.txt'}:
            cp(path,dst/path.relative_to(src))
def source_release_files(folders):
    tracked=set(subprocess.check_output(['git','ls-files','-z'],cwd=R).decode('utf-8').split('\0'))
    files=[]
    for folder in folders:
        for path in (R/folder).rglob('*'):
            if path.is_symlink() or not path.is_file() or path.suffix.lower() in FONTS or any(x in ('bin','obj','__pycache__','.git') for x in path.relative_to(R/folder).parts):continue
            relative=path.relative_to(R).as_posix()
            if relative in ('portable_config/bangumi.json','portable_config/bangumi-app.json'):continue
            if relative in tracked or relative in SOURCE_ADDITIONS or relative.startswith(('third_party/danmaku-factory/','third_party/libass/','THIRD_PARTY_LICENSES/')):files.append(path)
    return sorted(files,key=lambda path:path.relative_to(R).as_posix())
def replace_once(p,old,new):
    s=p.read_text(encoding='utf-8-sig')
    if new in s:return
    if s.count(old)!=1:raise RuntimeError('Source patch context mismatch: '+str(p)+' '+old[:70])
    p.write_text(s.replace(old,new),encoding='utf-8')

def prepare():
    E.mkdir(exist_ok=True)
    if not (R/'src/player').exists():
        raise RuntimeError('Player and manager sources must be present in the source checkout')
    # Remove the retired renderer from the build copy before updating sources.
    retired_renderer=(R/'player/src/MpvNet.Windows/Danmaku').resolve()
    retired_renderer.relative_to(R.resolve())
    shutil.rmtree(retired_renderer,ignore_errors=True)
    for name in ('player','manager'):cp(R/'src'/name,R/name)
    for name in ('Manager','Player'):
        dest=R/'tests-generated'/(name.lower()+'-tests');dest.mkdir(parents=True,exist_ok=True)
        for ext in ('cs','csproj'):cp(R/f'tools/language-r3/{name}Tests.{ext}',dest/f'{name}Tests.{ext}')
    (R/'language-evidence').mkdir(exist_ok=True)
    dump(E/'source-inputs.json',{'workflow_commit':os.environ.get('GITHUB_SHA'),
       'source_files':{p.relative_to(R/'src').as_posix():sha(p) for p in (R/'src').rglob('*') if p.is_file()}})

def stage():
    if ST.exists():shutil.rmtree(ST)
    base=download(LOCK['bootstrap_core']);extract(base,R/'base-unpack')
    cp(app_root(R/'base-unpack'),ST);shutil.rmtree(R/'base-unpack')
    native=download(LOCK['native_and_ui_resources']);extract(native,ST)
    # Releases use the reviewed, hash-pinned native runtime. An explicitly
    # supplied native build is reserved for preparing a new runtime revision.
    native_output=os.environ.get('MPV_NATIVE_OUTPUT')
    if native_output:
        native_output=Path(native_output)
        if not all((native_output/name).is_file() for name in ('mpv.exe','libmpv-2.dll','libass-9.dll')):
            raise RuntimeError('MPV_NATIVE_OUTPUT is missing native runtime files')
        for path in native_output.rglob('*'):
            if path.is_file() and (path.suffix.lower() in ('.dll','.exe') or 'build-info' in path.relative_to(native_output).parts):
                cp(path,ST/path.relative_to(native_output))
    shutil.rmtree(ST/'portable_config/watch_later',ignore_errors=True)
    shutil.rmtree(ST/'portable_config/scripts',ignore_errors=True)
    shutil.copytree(R/'portable_config',ST/'portable_config',dirs_exist_ok=True,
        ignore=shutil.ignore_patterns('cache','watch_later','settings.xml','saved-props.json',
            '*history*.json','*diagnostic*.json*','bangumi*.json','*.bak','*.backup','hills*'))
    cp(R/'animejanai/animejanai.conf',ST/'animejanai/animejanai.conf')
    cp(R/'THIRD_PARTY_LICENSES',ST/'THIRD_PARTY_LICENSES');cp(R/'LICENSE',ST/'LICENSE')
    cp(R/'THIRD_PARTY_LICENSES/DirectML.txt',ST/'animejanai/inference/DirectML_LICENSE.txt')
    cp(R/'docs/open-source-notices.md',ST/'OPEN_SOURCE_NOTICES.md')
    cp(H/'updater-upstream.json',ST/'build-info/standalone/updater-upstream.json')
    run(sys.executable,H/'build_danmaku_factory.py','--output',ST/'animejanai/danmaku/DanmakuFactory.exe')
    cp(R/'third_party/danmaku-factory/UPSTREAM.json',ST/'build-info/danmaku/upstream.json')
    cp(R/'docs/danmaku-renderer.md',ST/'弹幕说明.md')

    # The pinned seed contains the previous frontend binaries. Only the newly
    # compiled product entry points belong in this distribution.
    for name in ('mpvnet.exe','AnimeJaNaiManager.exe','AnimeJaNaiUpdater.exe'):
        (ST/name).unlink(missing_ok=True)
    # The shared self-contained runtime uses the Windows Desktop framework
    # supplied by the player. Copy it last so its full desktop assemblies are
    # retained instead of the Core framework's forwarding assemblies.
    for folder in ('publish-manager','publish-updater','publish-player'):
        for p in (R/folder).rglob('*'):
            if p.name.startswith(('AnimeVE.','AnimeVEManager.','AnimeVEUpdater.')):continue
            if p.is_file() and p.suffix.lower() in ('.exe','.dll','.json'):cp(p,ST/p.relative_to(R/folder))
    for p in ST.glob('AnimeVE*'):
        if p.is_file():p.unlink()
    # libass is part of the same pinned native input, so a frontend release
    # cannot silently change its renderer or compiler output.
    cp(R/'third_party/libass/UPSTREAM.json',ST/'build-info/native/libass-source.json')
    cp(R/'third_party/libass/COPYING',ST/'THIRD_PARTY_LICENSES/libass-ISC.txt')
    candidates=list(Path(r'C:\Program Files\Microsoft Visual Studio\2022').glob('*/VC/Redist/MSVC/*/x64/Microsoft.VC143.CRT'))
    if candidates:
        crt=sorted(candidates)[-1]
        for p in crt.glob('*.dll'):cp(p,ST/p.name)
        dump(E/'vc-runtime.json',{'directory':str(crt),'files':{p.name:sha(p) for p in crt.glob('*.dll')}})
    for p in list(ST.rglob('*')):
        if p.is_file() and p.suffix.lower() in FONTS:p.unlink()
    (ST/'portable_config/scripts/modernx.lua').unlink(missing_ok=True)
    (ST/'portable_config/script-opts/modernx.conf').unlink(missing_ok=True)
    p=ST/'portable_config/scripts/thumbfast.lua';s=p.read_text(encoding='utf-8')
    s=s.replace('local mpv_path = options.mpv_path','local mpv_path = options.mpv_path == "mpv" and mp.command_native({"expand-path", "~~/../app/mpv.exe"}) or options.mpv_path')
    p.write_text(s,encoding='utf-8')
    for n in ('input.conf','input-animejanai.conf'):
        p=ST/'portable_config'/n;s=p.read_text(encoding='utf-8')
        s=s.replace('apply-profile upscale-on; script-message aji-slot','script-message aji-slot')
        p.write_text(s,encoding='utf-8')
    component_assets=prepare_components(ST,DIST,META,SEVEN)
    dump(E/'component-assets.json',component_assets)

    dump(ST/'manifest.json',{'name':META['name'],'version':META['version'],'distribution':'portable','repository':REPO,'component_version':'3.6.0','platform':'win-x64'})
    cp(R/'docs/standalone.md',ST/'使用说明.md')
    for name in ('README.md','README.en.md','CHANGELOG.md'):cp(R/name,ST/name)
    for name in ('standalone.md','build.md','danmaku-renderer.md','open-source-notices.md','bangumi-sources.md'):cp(R/'docs'/name,ST/'docs'/name)
    organize_payload()
    dump(E/'components.json',validate_components(ST))
    inspect_payload()

def organize_payload():
    runtime=ST/'app';runtime.mkdir(exist_ok=True)
    docs=ST/'docs';docs.mkdir(exist_ok=True)
    for name in ('README.md','README.en.md','CHANGELOG.md','LICENSE','OPEN_SOURCE_NOTICES.md',
                 'THIRD_PARTY_LICENSES','licenses','使用说明.md','弹幕说明.md'):
        source=ST/name
        if source.exists():
            cp(source,docs/name)
            if source.is_dir():shutil.rmtree(source)
            else:source.unlink()
    for name in ('准备使用.txt','README-full.txt','更新说明-r2.md','更新说明-r4.md'):
        (ST/name).unlink(missing_ok=True)
    for source in list(ST.iterdir()):
        if source.name in ('app','docs','portable_config','animejanai'):continue
        target=runtime/source.name
        cp(source,target)
        if source.is_dir():shutil.rmtree(source)
        else:source.unlink()
    for name in ('README.md','README.en.md'):
        path=docs/name
        path.write_text(path.read_text(encoding='utf-8').replace('(docs/','('),encoding='utf-8')
    for project,published,name in (
        ('player/src/MpvNet.Windows/MpvNet.Windows.csproj','publish-player','mpv-AnimeFusion'),
        ('manager/AnimeJaNaiConfEditor/AnimeJaNaiConfEditor.csproj','publish-manager','mpv-AnimeFusionManager')):
        run('dotnet','msbuild',R/project,'-t:GeneratePortableAppHost','-p:Configuration=Release',
            '-p:RuntimeIdentifier=win-x64',f'-p:PortableAssembly={R/published/(name+".dll")}',
            f'-p:PortableAppHostPath={ST/(name+".exe")}', '-verbosity:quiet')
        (runtime/(name+'.exe')).unlink()
    expected={'mpv-AnimeFusion.exe','mpv-AnimeFusionManager.exe','app','docs','portable_config','animejanai'}
    if {p.name for p in ST.iterdir()}!=expected:raise RuntimeError('Unexpected portable root entries')


def clean_session_files(app):
    for folder in ('cache','watch_later'):
        shutil.rmtree(app/'portable_config'/folder,ignore_errors=True)
    for name in ('settings.xml','saved-props.json','startup-diagnostic.json','startup-diagnostic.json.tmp','playback-diagnostic.json','bangumi.json','bangumi-app.json'):
        (app/'portable_config'/name).unlink(missing_ok=True)

def inspect_payload():
    clean_session_files(ST)
    expected={'mpv-AnimeFusion.exe','mpv-AnimeFusionManager.exe','app','docs','portable_config','animejanai'}
    if {p.name for p in ST.iterdir()}!=expected:raise RuntimeError('Unexpected portable root entries')
    for name in ('mpv-AnimeFusion','mpv-AnimeFusionManager'):
        if ('app/'+name+'.dll').encode() not in (ST/(name+'.exe')).read_bytes():
            raise RuntimeError('Portable apphost points to the wrong assembly: '+name)
    manifest=json.loads((ST/'app/manifest.json').read_text(encoding='utf-8'))
    if manifest['name']!=META['name'] or manifest['version']!=META['version']:
        raise RuntimeError('Package identity does not match release metadata')
    files=[p for p in ST.rglob('*') if p.is_file()]
    dump(E/'file-inventory.json',{p.relative_to(ST).as_posix():p.stat().st_size for p in files})
    required=['mpv-AnimeFusion.exe','app/mpv.exe','app/libmpv-2.dll','mpv-AnimeFusionManager.exe','app/mpv-AnimeFusionUpdater.exe',
       'portable_config/mpv.conf','portable_config/mpv-animejanai.conf','portable_config/input.conf',
       'portable_config/script-opts/player_ui.conf','portable_config/script-opts/player_ui_danmaku.conf',
       'animejanai/danmaku/DanmakuFactory.exe',
       'portable_config/scripts/network_playback.lua','portable_config/scripts/player_ui.lua','portable_config/scripts/player_ui_danmaku.lua','portable_config/scripts/thumbfast.lua',
       'portable_config/script-modules/player_ui_core.lua','portable_config/script-modules/player_ui_metrics.lua',
       'portable_config/script-modules/player_ui_menu.lua','portable_config/script-modules/player_ui_danmaku_online.lua',
       'portable_config/script-modules/player_ui_danmaku_render.lua','app/build-info/danmaku/upstream.json',
       'animejanai/animejanai.conf','animejanai/inference/aji.dll','animejanai/inference/aji_trt.dll',
       'animejanai/inference/aji_dml.dll','app/7za.exe','app/manifest.json','docs/OPEN_SOURCE_NOTICES.md','docs/LICENSE',
       'docs/THIRD_PARTY_LICENSES/mpv-source/Copyright','docs/THIRD_PARTY_LICENSES/DirectML.txt',
       'app/Locale/zh-CN/LC_MESSAGES/mpvnet.mo']
    for name in required:
        if not (ST/name).is_file() or (ST/name).stat().st_size==0:raise RuntimeError('Incomplete package: '+name)
    component_report=validate_components(ST)
    if any(p.suffix.lower() in FONTS for p in files):raise RuntimeError('Unexpected standalone font file')
    dump(E/'payload.json',{'required_files':required,**component_report,
       'files':len(files),'unpacked_bytes':sum(p.stat().st_size for p in files),'gpu_inference_tested':False})

def release_notes():
    text=(R/'docs/release-features.md').read_text(encoding='utf-8').strip()
    if not text:raise RuntimeError('Missing release feature descriptions')
    return text+'\n'

def package():
    require_security_result(ST,E/'security/results.json')
    run(sys.executable,H/'verify_player_sources.py')
    for name,marker in [('manager-tests.txt','PASS Manager language suite'),('player-tests.txt','PASS Player language suite'),('parser-tests.txt','PASS')]:
        if marker not in (R/'language-evidence'/name).read_text(encoding='utf-8-sig'):raise RuntimeError('UI regression failed: '+name)
    for n in ('results.json','scripts-results.json'):
        results=json.loads((E/'runtime'/n).read_text(encoding='utf-8'))
        if not results or not all(r['passed'] for r in results):raise RuntimeError('Runtime tests failed')
    danmaku=json.loads((E/'danmaku/results.json').read_text(encoding='utf-8'))
    if not danmaku or not all(r['passed'] for r in danmaku):raise RuntimeError('Upstream danmaku package verification failed')
    shutil.rmtree(ST/'portable_config/watch_later',ignore_errors=True)
    inspect_payload();DIST.mkdir(exist_ok=True)
    info=ST/'app/build-info/standalone'
    shutil.rmtree(info/'validation',ignore_errors=True)
    shutil.rmtree(info/'ui-validation',ignore_errors=True)
    copy_validation_reports(E,info/'validation')
    copy_validation_reports(R/'language-evidence',info/'ui-validation')
    # Adding reports must leave the scanned executable/script inventory exactly
    # unchanged. The release check rejects new files, changed bytes, stale scans
    # and changed policy rather than treating earlier evidence as sufficient.
    require_security_result(ST,E/'security/results.json')
    cp(E/'security',info/'validation/security')
    dump(info/'provenance.json',{'version':META['version'],'input_commit':os.environ['GITHUB_SHA'],
      'run_id':os.environ['GITHUB_RUN_ID'],'dependencies':LOCK,'self_contained_dotnet':True,
      'gpu_inference_tested':False,'player_ui_server_tested':False})
    dump(info/'SHA256.json',{p.relative_to(ST).as_posix():sha(p) for p in ST.rglob('*') if p.is_file() and p!=info/'SHA256.json'})
    installer=build_installer(ST,DIST/'installer',E/'installer')
    run(sys.executable,H/'security_verify.py','scan',DIST/'installer',E/'installer/security')
    require_security_result(DIST/'installer',E/'installer/security/results.json')
    cp(installer,DIST/installer.name)
    installer=DIST/installer.name
    archive=DIST/f'{META["name"]}-{META["version"]}-win-x64.7z'
    run(SEVEN,'a','-t7z','-mx=3','-mmt=2','-bd',archive,'.',cwd=ST,stdout=subprocess.DEVNULL)
    run(SEVEN,'t',archive,stdout=subprocess.DEVNULL)
    archives=[archive]
    if archive.stat().st_size>=2*1024**3:
        archives=[]
        with archive.open('rb') as f:
            index=1
            while f.tell()<archive.stat().st_size:
                part=Path(str(archive)+f'.{index:03}')
                with part.open('wb') as out:
                    remaining=1900*1024**2
                    while remaining:
                        data=f.read(min(remaining,1024*1024))
                        if not data:break
                        out.write(data);remaining-=len(data)
                archives.append(part);index+=1
        archive.unlink()
    native=LOCK['native_and_ui_resources'];cp(download(native),DIST/native['name'])
    user_packages=archives+[installer]
    dump(DIST/'artifacts.json',json.loads((E/'component-assets.json').read_text())+[native]+[{'repo':REPO,'tag':META['tag'],'name':p.name,'sha256':sha(p),'bytes':p.stat().st_size} for p in user_packages])
    shutil.rmtree(ST);extract(archives[0],R/'clean-install')
    require_security_result(R/'clean-install',E/'security/results.json')
    run(sys.executable,R/'tests/test_optional_components.py',R/'clean-install')
    run(sys.executable,H/'test_complete.py',R/'clean-install',E/'fresh-install')
    run(sys.executable,R/'tests/test_player_ui_windows.py',R/'clean-install',E/'fresh-install/player_ui')
    run(sys.executable,R/'tests/test_network_playback.py',R/'clean-install',E/'fresh-install/network')
    run(sys.executable,R/'tests/test_startup_playback.py',R/'clean-install',E/'fresh-install/startup')
    run(sys.executable,R/'tests/test_player_ui_empty_scope.py',R/'clean-install',E/'fresh-install/player_ui-handoff')
    run(sys.executable,H/'test_danmaku_package.py',R/'clean-install',E/'fresh-install/danmaku')
    cp(E/'fresh-install',DIST/'fresh-install-evidence')
    sourcezip=DIST/f'{META["name"]}-{META["version"]}-sources.zip'
    with zipfile.ZipFile(sourcezip,'w',zipfile.ZIP_DEFLATED) as z:
        for p in source_release_files(('src','tools','tests','portable_config','animejanai','THIRD_PARTY_LICENSES','third_party','docs')):
            if p.suffix.lower() not in FONTS:z.write(p,p.relative_to(R).as_posix())
        for n in ('LICENSE','release.json','README.md','README.en.md','CHANGELOG.md'):z.write(R/n,n)
    (DIST/'SHA256SUMS.txt').write_text(''.join(sha(p)+'  '+p.name+'\n' for p in user_packages+[DIST/native['name']]+[DIST/a['name'] for a in json.loads((E/'component-assets.json').read_text())]+[sourcezip]),encoding='utf-8')
    (DIST/'RELEASE.md').write_text(release_notes(),encoding='utf-8')
    cp(E/'payload.json',DIST/'payload-verification.json')
    print('FULL PACKAGE VERIFIED',[(p.name,p.stat().st_size) for p in archives],flush=True)

def api(endpoint,payload=None,method=None):
    cmd=['gh','api',endpoint]
    if payload is not None:result=subprocess.check_output(cmd+['--method',method or 'POST','--input','-'],input=json.dumps(payload).encode())
    else:result=subprocess.check_output(cmd+(['--method',method] if method else []))
    return json.loads(result) if result else None

if __name__=='__main__':{'prepare':prepare,'stage':stage,'package':package}[sys.argv[1]]()
