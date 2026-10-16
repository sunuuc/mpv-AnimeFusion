"""Publish verified installer and portable assets from their committed source."""
import hashlib,json,sys,urllib.request
from build import (R,H,E,DIST,REPO,META,sha,dump,run,api,require_build_source,
                   require_results,RUNTIME_CASES,SCRIPT_CASES,DANMAKU_CASES,COMPLETE_CASES)
from security_verify import require_result as require_security_result


def verified_assets():
    expected={f'{META["name"]}-{META["version"]}-setup-x64.exe',
              f'{META["name"]}-{META["version"]}-win-x64.7z'}
    assets=json.loads((DIST/'artifacts.json').read_text(encoding='utf-8'))
    if len(assets)!=2 or {asset['name'] for asset in assets}!=expected:
        raise RuntimeError('A release must contain exactly one installer and one portable archive')
    checksums=(DIST/'SHA256SUMS.txt').read_text(encoding='utf-8').splitlines()
    checksum_map={line.split('  ',1)[1]:line.split('  ',1)[0] for line in checksums}
    if len(checksums)!=2 or set(checksum_map)!=expected:
        raise RuntimeError('Publication checksum list does not match the two release assets')
    for asset in assets:
        path=DIST/asset['name']
        if asset['repo']!=REPO or asset['tag']!=META['tag']:
            raise RuntimeError('Publication asset identity mismatch: '+path.name)
        if sha(path)!=asset['sha256'] or checksum_map[path.name]!=asset['sha256'] or path.stat().st_size!=asset['bytes']:
            raise RuntimeError('Publication checksum mismatch: '+path.name)
    return assets


def verify_validation(source):
    desktop=source['validation_scope']=='desktop'
    require_security_result(R/'clean-install',E/'security/results.json')
    require_security_result(DIST/'installer',E/'installer/security/results.json')
    run(sys.executable,H/'verify_player_sources.py')
    provenance=json.loads((R/'clean-install/app/build-info/standalone/provenance.json').read_text(encoding='utf-8'))
    if any(provenance.get(key)!=value for key,value in source.items()) or provenance['version']!=META['version']:
        raise RuntimeError('Packaged source provenance does not match this build')
    scope=json.loads((E/'validation-scope.json').read_text(encoding='utf-8'))
    if scope['scope']!=source['validation_scope'] or scope['desktop_interaction']!=desktop:
        raise RuntimeError('Validation scope changed after packaging')
    require_results(E/'fresh-install/results.json',COMPLETE_CASES,() if desktop else COMPLETE_CASES[-2:])
    for base in (E,E/'fresh-install'):
        require_results(base/'runtime/results.json',RUNTIME_CASES,() if desktop else ('frontend',))
        require_results(base/'runtime/scripts-results.json',SCRIPT_CASES)
        require_results(base/'danmaku/results.json',DANMAKU_CASES)
    if not desktop:return scope
    for base in (E,E/'fresh-install'):
        for folder,count in (('player_ui',2),('network',None),('startup',7),('player_ui-handoff',3)):
            path=base/folder/'results.json'
            result=json.loads(path.read_text(encoding='utf-8'))
            if not result or (count is not None and len(result)!=count) or not all(t.get('passed') is True for t in result):
                raise RuntimeError('Desktop regression failed: '+str(path))
            if folder=='player_ui-handoff':
                playlist=next(t for t in result if t['case']=='player_ui-playlist-selected')
                if playlist['wrong_episode_requests']!=0 or playlist['selected_episode_requests']!=1:
                    raise RuntimeError('Desktop playlist request regression failed')
    return scope


def tag_commit():
    refs=api(f'repos/{REPO}/git/matching-refs/tags/{META["tag"]}')
    ref=next((ref for ref in refs if ref['ref']=='refs/tags/'+META['tag']),None)
    if ref is None:return None
    target=ref['object']
    while target['type']=='tag':
        target=api(f'repos/{REPO}/git/tags/{target["sha"]}')['object']
    if target['type']!='commit':raise RuntimeError('Release tag must point to a source commit')
    return target['sha']


def verify_remote_assets(release,assets):
    expected={asset['name'] for asset in assets}
    if len(release['assets'])!=len(assets) or {asset['name'] for asset in release['assets']}!=expected:
        raise RuntimeError('Remote release contains unexpected or missing assets')
    for local in assets:
        remote=next(asset for asset in release['assets'] if asset['name']==local['name'])
        if remote.get('digest')!='sha256:'+local['sha256'] or remote['size']!=local['bytes'] or remote['state']!='uploaded':
            raise RuntimeError('Remote artifact verification failed: '+local['name'])


def publish():
    source=require_build_source()
    scope=verify_validation(source)
    assets=verified_assets()
    commit=source['input_commit']
    if api(f'repos/{REPO}/git/ref/heads/main')['object']['sha']!=commit:
        raise RuntimeError('Push the exact build commit to main before publishing')
    existing_tag=tag_commit()
    if existing_tag is not None and existing_tag!=commit:
        raise RuntimeError('Existing release tag points to different sources')
    old=[release for release in api(f'repos/{REPO}/releases?per_page=100') if release['tag_name']==META['tag']]
    notes=(DIST/'RELEASE.md').read_text(encoding='utf-8')
    if old:
        if len(old)!=1 or not old[0]['draft'] or old[0]['target_commitish']!=commit or old[0]['body']!=notes:
            raise RuntimeError('Existing release does not match this unfinished publication')
        rel=old[0]
        existing_names={asset['name'] for asset in rel['assets']}
        verify_remote_assets(rel,[asset for asset in assets if asset['name'] in existing_names])
    else:
        rel=api(f'repos/{REPO}/releases',{'tag_name':META['tag'],'target_commitish':commit,
            'name':f'{META["name"]} {META["version"]}','body':notes,'draft':True,'prerelease':META['prerelease']})
    if rel['tag_name']!=META['tag'] or not rel['draft']:
        raise RuntimeError('Draft release lost the requested version tag')
    uploads=[DIST/asset['name'] for asset in assets if asset['name'] not in {item['name'] for item in rel['assets']}]
    if uploads:run('gh','release','upload',META['tag'],'-R',REPO,*uploads)
    verify_remote_assets(api(f'repos/{REPO}/releases/{rel["id"]}'),assets)
    api(f'repos/{REPO}/releases/{rel["id"]}',{'tag_name':META['tag'],'draft':False,'make_latest':'true'},'PATCH')
    published=api(f'repos/{REPO}/releases/tags/{META["tag"]}')
    if published['id']!=rel['id'] or published['draft'] or published['prerelease']!=META['prerelease'] or tag_commit()!=commit:
        raise RuntimeError('Published release identity does not match the verified build')
    verify_remote_assets(published,assets)

    # Download the public bytes, rather than relying only on upload metadata.
    public_assets=[]
    for local in assets:
        asset=next(asset for asset in published['assets'] if asset['name']==local['name'])
        digest=hashlib.sha256();count=0
        request=urllib.request.Request(asset['browser_download_url'],headers={'User-Agent':'mpv-AnimeFusion-release-verification'})
        with urllib.request.urlopen(request,timeout=120) as response:
            while chunk:=response.read(1024*1024):
                digest.update(chunk);count+=len(chunk)
        if count!=local['bytes'] or digest.hexdigest()!=local['sha256']:
            raise RuntimeError('Public download checksum mismatch: '+local['name'])
        public_assets.append({'name':local['name'],'bytes':count,'sha256':digest.hexdigest()})
    dump(DIST/'public-downloads.json',{'passed':True,'assets':public_assets})
    dump(DIST/'publication.json',{'release_id':rel['id'],'source_commit':commit,'build_commit':commit,
         'tag':META['tag'],'assets':assets,'validation':scope})
    print('PUBLISHED',META['tag'],commit)


if __name__=='__main__':publish()
