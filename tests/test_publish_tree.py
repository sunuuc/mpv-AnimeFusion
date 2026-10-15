"""Check source publication with and without generated file changes."""
from pathlib import Path
import ast
import unittest

ROOT = Path(__file__).resolve().parents[1]

class SourceTreeTests(unittest.TestCase):
    def test_old_releases_are_preserved(self):
        text = (ROOT/'tools/standalone/publish.py').read_text(encoding='utf-8')
        self.assertNotIn('prune_previous_releases', text)
        self.assertNotIn("'DELETE'", text)
        self.assertNotIn('removed_releases', text)

    def test_publish_workflows_are_manual_only(self):
        for name in ('standalone.yml', 'publish-r2.yml', 'publish-language-r3.yml'):
            text = (ROOT/'.github/workflows'/name).read_text(encoding='utf-8')
            self.assertIn('workflow_dispatch:', text)
            self.assertNotIn('  push:', text)
        text = (ROOT/'.github/workflows/standalone.yml').read_text(encoding='utf-8')
        self.assertIn("github.event_name == 'workflow_dispatch' && inputs.publish", text)

    def build_tree(self, changes, api):
        source = ast.parse((ROOT / 'tools/standalone/publish.py').read_text(encoding='utf-8'))
        assignment = next(node for node in source.body if isinstance(node, ast.Assign)
                          and any(isinstance(target, ast.Name) and target.id == 'newtree'
                                  for target in node.targets))
        scope = {'tree': changes, 'base_tree': 'existing-tree', 'REPO': 'owner/project', 'api': api}
        exec(compile(ast.Module(body=[assignment], type_ignores=[]), 'publish.py', 'exec'), scope)
        return scope['newtree']

    def test_unchanged_sources_reuse_tree_without_empty_api_request(self):
        def unexpected(*args):
            self.fail('GitHub rejects an empty tree update')
        self.assertEqual(self.build_tree([], unexpected), 'existing-tree')

    def test_changed_sources_create_tree(self):
        changes = [{'path': 'file.txt', 'mode': '100644', 'type': 'blob', 'content': 'changed'}]
        def api(endpoint, payload):
            self.assertEqual(endpoint, 'repos/owner/project/git/trees')
            self.assertEqual(payload, {'base_tree': 'existing-tree', 'tree': changes})
            return {'sha': 'updated-tree'}
        self.assertEqual(self.build_tree(changes, api), 'updated-tree')

    def test_changed_sources_do_not_ignore_api_failure(self):
        def api(*args):
            raise RuntimeError('request failed')
        with self.assertRaisesRegex(RuntimeError, 'request failed'):
            self.build_tree([{'path': 'file.txt'}], api)

    def test_existing_draft_retains_version_when_target_changes(self):
        source = ast.parse((ROOT/'tools/standalone/publish.py').read_text(encoding='utf-8'))
        statement = next(node for node in source.body if isinstance(node, ast.If)
                         and isinstance(node.test, ast.Name) and node.test.id == 'old')
        requests = []
        def api(endpoint, payload, method):
            requests.append((endpoint, payload, method))
            return {'id': 1, 'tag_name': payload.get('tag_name', 'untagged-draft'), 'draft': True}
        scope = {'api': api, 'REPO': 'owner/project', 'commit': 'new-source', 'notes': 'notes',
                 'META': {'tag': 'v1.3.4', 'name': 'Player', 'version': '1.3.4'},
                 'LOCK': {'native_and_ui_resources': {'name': 'native.7z'}},
                 'old': [{'id': 1, 'draft': True, 'assets': [{'name': 'native.7z'}]}]}
        exec(compile(ast.Module(body=[statement], type_ignores=[]), 'publish.py', 'exec'), scope)
        self.assertEqual(scope['rel']['tag_name'], 'v1.3.4')
        self.assertEqual(requests[0][1]['target_commitish'], 'new-source')
        self.assertEqual(requests[0][2], 'PATCH')

    def test_publishing_draft_explicitly_keeps_version(self):
        source = ast.parse((ROOT/'tools/standalone/publish.py').read_text(encoding='utf-8'))
        payloads = [node.args[1] for node in ast.walk(source)
                    if isinstance(node, ast.Call) and isinstance(node.func, ast.Name)
                    and node.func.id == 'api' and len(node.args) >= 2
                    and isinstance(node.args[1], ast.Dict)]
        publishing = next(payload for payload in payloads
                          if any(isinstance(key, ast.Constant) and key.value == 'make_latest'
                                 for key in payload.keys))
        payload = eval(compile(ast.Expression(publishing), 'publish.py', 'eval'),
                       {'META': {'tag': 'v1.3.4'}})
        self.assertEqual(payload, {'tag_name': 'v1.3.4', 'draft': False, 'make_latest': 'true'})

if __name__ == '__main__':
    unittest.main()
