"""Check committed-source publication without network writes or desktop actions."""
from pathlib import Path
from unittest.mock import patch
import io,json,os,sys,tempfile,unittest

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'tools/standalone'))
import build
import publish

COMMIT='a'*40


class BuildSourceTests(unittest.TestCase):
    def test_local_build_uses_git_without_workflow_identifiers(self):
        with patch.dict(os.environ,{'GITHUB_SHA':'unrelated','GITHUB_RUN_ID':'not-a-workflow'},clear=True), \
             patch.object(build.subprocess,'check_output',side_effect=[COMMIT+'\n','']):
            self.assertEqual(build.committed_source(),{'input_commit':COMMIT,'build_environment':'local'})

    def test_uncommitted_sources_cannot_be_built(self):
        with patch.object(build.subprocess,'check_output',side_effect=[COMMIT,' M src/player.cs']):
            with self.assertRaisesRegex(RuntimeError,'Commit all tracked'):
                build.committed_source()

    def test_actions_identity_must_match_real_checkout(self):
        with patch.dict(os.environ,{'GITHUB_ACTIONS':'true','GITHUB_SHA':'wrong','GITHUB_RUN_ID':'123'},clear=True), \
             patch.object(build.subprocess,'check_output',side_effect=[COMMIT,'']):
            with self.assertRaisesRegex(RuntimeError,'provenance'):
                build.committed_source()

    def test_source_cannot_change_after_prepare(self):
        with tempfile.TemporaryDirectory() as folder,patch.object(build,'E',Path(folder)), \
             patch.object(build,'committed_source',return_value={'input_commit':'b'*40,'build_environment':'local'}):
            build.dump(Path(folder)/'source-inputs.json',{'input_commit':COMMIT,'build_environment':'local','validation_scope':'headless'})
            with self.assertRaisesRegex(RuntimeError,'changed after prepare'):
                build.require_build_source()

    def test_headless_checks_cannot_claim_desktop_passes(self):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'results.json'
            build.dump(path,[{'case':'native','passed':True},{'case':'desktop','status':'skipped'}])
            build.require_results(path,('native','desktop'),('desktop',))
            build.dump(path,[{'case':'native','passed':True},{'case':'desktop','status':'skipped','passed':True}])
            with self.assertRaisesRegex(RuntimeError,'must not claim'):
                build.require_results(path,('native','desktop'),('desktop',))

    def test_required_regression_failure_cannot_be_skipped(self):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'results.json'
            build.dump(path,[{'case':'native','status':'skipped'}])
            with self.assertRaisesRegex(RuntimeError,'Regression failed'):
                build.require_results(path,('native',))


class PublicationTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.dist=Path(self.temp.name)
        self.meta={'name':'Player','version':'1.3.5','tag':'v1.3.5','prerelease':False}
        for name,value in (('DIST',self.dist),('META',self.meta),('REPO','owner/project')):
            replacement=patch.object(publish,name,value)
            replacement.start();self.addCleanup(replacement.stop)
        self.contents={
            'Player-1.3.5-setup-x64.exe':b'verified installer',
            'Player-1.3.5-win-x64.7z':b'verified portable archive',
        }
        self.assets=[]
        for name,content in self.contents.items():
            path=self.dist/name;path.write_bytes(content)
            self.assets.append({'repo':'owner/project','tag':'v1.3.5','name':name,'sha256':build.sha(path),'bytes':len(content)})
        build.dump(self.dist/'artifacts.json',self.assets)
        (self.dist/'SHA256SUMS.txt').write_text(''.join(asset['sha256']+'  '+asset['name']+'\n' for asset in self.assets),encoding='utf-8')
        (self.dist/'RELEASE.md').write_text('release notes\n',encoding='utf-8')

    def remote_assets(self):
        return [{'name':asset['name'],'size':asset['bytes'],'digest':'sha256:'+asset['sha256'],
                 'state':'uploaded','browser_download_url':'https://example.invalid/'+asset['name']} for asset in self.assets]

    def test_only_two_expected_assets_are_publishable(self):
        self.assertEqual(publish.verified_assets(),self.assets)
        build.dump(self.dist/'artifacts.json',self.assets+[{'name':'native-build-inputs.7z'}])
        with self.assertRaisesRegex(RuntimeError,'exactly one installer'):
            publish.verified_assets()

    def test_changed_bytes_cannot_be_published(self):
        (self.dist/self.assets[0]['name']).write_bytes(b'changed')
        with self.assertRaisesRegex(RuntimeError,'checksum mismatch'):
            publish.verified_assets()

    def test_extra_checksum_entry_is_rejected(self):
        with (self.dist/'SHA256SUMS.txt').open('a') as stream:stream.write('untrusted  sources.zip\n')
        with self.assertRaisesRegex(RuntimeError,'checksum list'):
            publish.verified_assets()

    def test_remote_digest_and_asset_set_are_verified(self):
        remote={'assets':self.remote_assets()}
        publish.verify_remote_assets(remote,self.assets)
        remote['assets'][0]['digest']='sha256:wrong'
        with self.assertRaisesRegex(RuntimeError,'verification failed'):
            publish.verify_remote_assets(remote,self.assets)
        remote['assets']=self.remote_assets()+[{'name':'extra.7z'}]
        with self.assertRaisesRegex(RuntimeError,'unexpected'):
            publish.verify_remote_assets(remote,self.assets)

    def publish_mock(self,existing=None,tag=None):
        requests=[]
        remote={'id':123,'tag_name':'v1.3.5','target_commitish':COMMIT,'draft':True,
                'prerelease':False,'body':'release notes\n','assets':[]}
        def api(endpoint,payload=None,method=None):
            requests.append((endpoint,payload,method))
            if endpoint.endswith('/git/ref/heads/main'):return {'object':{'sha':COMMIT}}
            if endpoint.endswith('/releases?per_page=100'):return existing or []
            if endpoint.endswith('/releases') and payload is not None:
                self.assertEqual(payload['target_commitish'],COMMIT)
                return remote
            if endpoint.endswith('/releases/123'):
                if payload:remote.update(payload)
                return remote
            if endpoint.endswith('/releases/tags/v1.3.5'):return remote
            self.fail('Unexpected GitHub operation: '+endpoint)
        def run(*args):
            self.assertEqual(args[:6],('gh','release','upload','v1.3.5','-R','owner/project'))
            self.assertEqual({Path(path).name for path in args[6:]},set(self.contents))
            remote['assets']=self.remote_assets()
        def download(request,timeout):
            return io.BytesIO(self.contents[request.full_url.rsplit('/',1)[1]])
        source={'input_commit':COMMIT,'build_environment':'local','validation_scope':'headless'}
        with patch.object(publish,'require_build_source',return_value=source), \
             patch.object(publish,'verify_validation',return_value={'scope':'headless','desktop_ui_verified':False}), \
             patch.object(publish,'tag_commit',side_effect=[tag,COMMIT]), \
             patch.object(publish,'api',side_effect=api),patch.object(publish,'run',side_effect=run), \
             patch.object(publish.urllib.request,'urlopen',side_effect=download):
            publish.publish()
        return requests

    def test_publication_binds_exact_source_without_creating_commit(self):
        requests=self.publish_mock()
        self.assertFalse(any('/git/commits' in endpoint or '/git/trees' in endpoint or '/git/refs' in endpoint
                             for endpoint,_,_ in requests))
        result=json.loads((self.dist/'publication.json').read_text())
        self.assertEqual(result['source_commit'],COMMIT)
        self.assertEqual(result['build_commit'],COMMIT)
        self.assertFalse(result['validation']['desktop_ui_verified'])
        public=json.loads((self.dist/'public-downloads.json').read_text())
        self.assertTrue(public['passed'])
        self.assertEqual(len(public['assets']),2)

    def test_existing_public_release_is_preserved(self):
        with self.assertRaisesRegex(RuntimeError,'Existing release'):
            self.publish_mock(existing=[{'tag_name':'v1.3.5','draft':False}])

    def test_tag_for_other_source_is_not_reused(self):
        with self.assertRaisesRegex(RuntimeError,'different sources'):
            self.publish_mock(tag='b'*40)

    def test_publish_workflows_are_manual_only(self):
        for name in ('standalone.yml','publish-r2.yml','publish-language-r3.yml'):
            text=(ROOT/'.github/workflows'/name).read_text(encoding='utf-8')
            self.assertIn('workflow_dispatch:',text)
            self.assertNotIn('  push:',text)


if __name__=='__main__':unittest.main()
