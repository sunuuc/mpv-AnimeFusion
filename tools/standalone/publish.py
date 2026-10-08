"Publish the tested full package and corresponding source tree."
from pathlib import Path
import base64,json,os,subprocess,sys,zipfile
from build import R,H,E,DIST,REPO,META,LOCK,FONTS,sha,dump,run,api,source_release_files
from security_verify import require_result as require_security_result

require_security_result(R/'clean-install',E/'security/results.json')
require_security_result(DIST/'installer',E/'installer/security/results.json')

run(sys.executable,H/'verify_player_sources.py')

for line in (DIST/'SHA256SUMS.txt').read_text().splitlines():
    h,n=line.split('  ',1)
    if sha(DIST/n)!=h:raise RuntimeError('Publication checksum mismatch: '+n)

fresh=json.loads((E/'fresh-install/results.json').read_text())
assert len(fresh)==5 and all(x['passed'] for x in fresh)

for path in (E/'player_ui/results.json',E/'fresh-install/player_ui/results.json'):
    result=json.loads(path.read_text());assert len(result)==2 and all(t['passed'] for t in result),path
for path in (E/'danmaku/results.json',E/'fresh-install/danmaku/results.json'):
    result=json.loads(path.read_text());assert len(result)==4 and all(t['passed'] for t in result),path
for path in (E/'network/results.json',E/'fresh-install/network/results.json'):
    result=json.loads(path.read_text());assert result and all(t['passed'] for t in result),path
for path in (E/'startup/results.json',E/'fresh-install/startup/results.json'):
    result=json.loads(path.read_text());assert len(result)==7 and all(t['passed'] for t in result),path
for path in (E/'player_ui-handoff/results.json',E/'fresh-install/player_ui-handoff/results.json'):
    result=json.loads(path.read_text());assert len(result)==3 and all(t['passed'] for t in result),path
    playlist=next(t for t in result if t['case']=='player_ui-playlist-selected')
    assert playlist['wrong_episode_requests']==0 and playlist['selected_episode_requests']==1,playlist

head=api(f'repos/{REPO}/git/ref/heads/main')['object']['sha']
assert head==os.environ['GITHUB_SHA'],'Main changed during the build'
assets=json.loads((DIST/'artifacts.json').read_text())
assert assets and all(x['repo']==REPO for x in assets)
dump(H/'dependencies.json',LOCK)
sourcezip=DIST/f'{META["name"]}-{META["version"]}-sources.zip'

# Record the exact runtime input for rebuilding this release.
temp=sourcezip.with_suffix('.pending.zip')
with zipfile.ZipFile(sourcezip) as src,zipfile.ZipFile(temp,'w',zipfile.ZIP_DEFLATED) as dst:
    for item in src.infolist():
        if item.filename not in ('tools/standalone/dependencies.json','README.md','README.en.md'):dst.writestr(item,src.read(item.filename))
    dst.write(H/'dependencies.json','tools/standalone/dependencies.json')
    dst.write(R/'README.md','README.md')
    dst.write(R/'README.en.md','README.en.md')
    dst.write(R/'.github/workflows/standalone.yml','.github/workflows/standalone.yml')
temp.replace(sourcezip)

user_assets=[DIST/x['name'] for x in assets]
checksums=DIST/'SHA256SUMS.txt'
checksums.write_text(''.join(sha(p)+'  '+p.name+'\n' for p in user_assets+[sourcezip]),encoding='utf-8')
paths=source_release_files(('src','tools','portable_config','animejanai','tests','third_party','THIRD_PARTY_LICENSES','docs'))
tree=[]
tracked_blobs={}
for record in subprocess.check_output(['git','ls-tree','-r','-z','HEAD']).decode('utf-8').split('\0'):
    if record:
        metadata,name=record.split('\t',1)
        tracked_blobs[name]=metadata.split()[2]
blob_reader=subprocess.Popen(['git','cat-file','--batch'],stdin=subprocess.PIPE,stdout=subprocess.PIPE)
for p in paths:
    assert p.suffix.lower() not in FONTS|{'.exe','.dll'},p
    entry={'path':p.relative_to(R).as_posix(),'mode':'100644','type':'blob'};raw=p.read_bytes()
    previous=tracked_blobs.get(entry['path'])
    if previous:
        blob_reader.stdin.write((previous+'\n').encode());blob_reader.stdin.flush()
        header=blob_reader.stdout.readline().split()
        committed=blob_reader.stdout.read(int(header[2]));blob_reader.stdout.read(1)
        if raw.replace(b'\r\n',b'\n')==committed.replace(b'\r\n',b'\n'):
            continue
    try:entry['content']=raw.decode('utf-8').replace('\r\n','\n')
    except UnicodeDecodeError:entry['sha']=api(f'repos/{REPO}/git/blobs',{'content':base64.b64encode(raw).decode(),'encoding':'base64'})['sha']
    tree.append(entry)
blob_reader.stdin.close();assert blob_reader.wait()==0
tracked=set(subprocess.check_output(['git','ls-files'],text=True).splitlines())
for name in ('portable_config/scripts/modernx.lua','portable_config/script-opts/modernx.conf'):
    if name in tracked:tree.append({'path':name,'mode':'100644','type':'blob','sha':None})
base_tree=api(f'repos/{REPO}/git/commits/{head}')['tree']['sha']
newtree=api(f'repos/{REPO}/git/trees',{'base_tree':base_tree,'tree':tree})['sha'] if tree else base_tree
commit=api(f'repos/{REPO}/git/commits',{'message':f'Release {META["version"]} sources','tree':newtree,'parents':[head]})['sha']
old=[r for r in api(f'repos/{REPO}/releases?per_page=100') if r['tag_name']==META['tag']]
notes=(DIST/'RELEASE.md').read_text(encoding='utf-8')
if old:
    assert len(old)==1 and old[0]['draft'] and {a['name'] for a in old[0]['assets']}=={LOCK['native_and_ui_resources']['name']},'Existing release is not the prepared build-input draft'
    rel=api(f'repos/{REPO}/releases/{old[0]["id"]}',{'target_commitish':commit,'name':f'{META["name"]} {META["version"]}','body':notes},'PATCH')
else:
    rel=api(f'repos/{REPO}/releases',{'tag_name':META['tag'],'target_commitish':commit,
        'name':f'{META["name"]} {META["version"]}','body':notes,'draft':True,'prerelease':META['prerelease']})
uploads=[p for p in user_assets if p.name!=LOCK['native_and_ui_resources']['name']] if old else user_assets
run('gh','release','upload',META['tag'],'-R',REPO,*uploads,sourcezip,checksums)
uploaded=api(f'repos/{REPO}/releases/{rel["id"]}')
for p in user_assets+[sourcezip,checksums]:
    a=next(x for x in uploaded['assets'] if x['name']==p.name)
    assert a.get('digest')=='sha256:'+sha(p) and a['state']=='uploaded'
assert {a['name'] for a in uploaded['assets']}=={p.name for p in user_assets+[sourcezip,checksums]}
api(f'repos/{REPO}/git/refs/heads/main',{'sha':commit,'force':False},'PATCH')
api(f'repos/{REPO}/releases/{rel["id"]}',{'draft':False,'make_latest':'true'},'PATCH')
published=api(f'repos/{REPO}/releases/tags/{META["tag"]}')
assert published['id']==rel['id'] and not published['draft'] and published['prerelease']==META['prerelease']

# Validate every published download against the tested local artifact.
import hashlib, urllib.request
public_assets=[]
for file in user_assets+[sourcezip,checksums]:
    asset=next(a for a in published['assets'] if a['name']==file.name)
    digest=hashlib.sha256();count=0
    request=urllib.request.Request(asset['browser_download_url'],headers={'User-Agent':'mpv-AnimeFusion-release-verification'})
    with urllib.request.urlopen(request,timeout=120) as response:
        while chunk:=response.read(1024*1024):
            digest.update(chunk);count+=len(chunk)
    assert count==file.stat().st_size and digest.hexdigest()==sha(file),file.name
    public_assets.append({'name':file.name,'bytes':count,'sha256':digest.hexdigest()})
dump(DIST/'public-downloads.json',{'passed':True,'assets':public_assets})
dump(DIST/'publication.json',{'release_id':rel['id'],'source_commit':commit,'build_commit':head,'tag':META['tag'],'assets':assets})
print('PUBLISHED',META['tag'],commit)
