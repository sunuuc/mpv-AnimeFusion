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

if __name__ == '__main__':
    unittest.main()
