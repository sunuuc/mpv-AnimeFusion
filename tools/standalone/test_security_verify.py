"""Offline release-security regressions; synthetic data, no antivirus changes."""
from pathlib import Path
import copy
import json
import tempfile
import unittest
from datetime import datetime, timezone, timedelta
from unittest.mock import patch
from security_verify import POLICY, VERSION, dump, inventory, policy_issues, preflight, require_result, scan, sha


class SecurityChecks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.app = self.root / 'app'
        self.component = self.app / 'animejanai/danmaku/DanmakuFactory.exe'
        self.component.parent.mkdir(parents=True)
        self.component.write_bytes(b'Synthetic harmless regression data, not executable')
        self.files = inventory(self.app)
        self.name = self.component.relative_to(self.app).as_posix()
        self.policy = {'schema': 1, 'reported_files': [],
                       'required_reviews': [{'path': self.name, 'vendor': '360'}], 'reviews': []}
        self.policy_path = self.root / 'policy.json'
        self.evidence = self.root / 'evidence.json'

    def approve_fixture(self):
        self.policy['reviews'] = [{'path': self.name, 'vendor': '360', 'verdict': 'clean',
                                  'sha256': self.files[self.name], 'review_id': 'SYNTHETIC-TEST-ONLY'}]
        dump(self.policy_path, self.policy)
        result = {'schema': 1, 'passed': True, 'scanner_exit_code': 0, 'infected_files': 0,
                  'known_viruses': 1, 'scanned_files': len(self.files), 'files': self.files,
                  'engine_version': VERSION, 'policy_sha256': sha(self.policy_path),
                  'checked_at': datetime.now(timezone.utc).isoformat()}
        dump(self.evidence, result)
        return result

    def test_release_policy_blocks_actual_reported_hash(self):
        policy = json.loads(POLICY.read_text(encoding='utf-8'))
        for digest in ('db3734fb45118ba08c4929214d4aef000875c2e80ac04130651bc482e5d9b58f',
                       '46a2ae0cf5897d816f9022b97dcb2f94fb53677aeedfdcc9e2b0c4e0db0a19b6'):
            with self.subTest(sha256=digest):
                issues = policy_issues({'renamed-tool.exe': digest}, policy)
                self.assertTrue(any(x['reason'] == 'antivirus-report-unresolved' for x in issues))

    def test_current_owner_acceptance_preserves_pending_vendor_review(self):
        preflight()
        policy = json.loads(POLICY.read_text(encoding='utf-8'))
        self.assertEqual(policy['reviews'], [])
        self.assertEqual(policy['release_assessment']['official_360_review'], 'not-obtained')
        acceptance = policy['owner_acceptances'][0]
        self.assertEqual(policy_issues({acceptance['path']: acceptance['sha256']}, policy), [])
        release = self.root / 'release.json'
        dump(release, {'tag': 'standalone-v-next'})
        with patch('security_verify.RELEASE', release):
            self.assertTrue(policy_issues({acceptance['path']: acceptance['sha256']}, policy))
            with self.assertRaises(RuntimeError):
                preflight()

    def test_new_hash_needs_official_review(self):
        self.assertTrue(policy_issues(self.files, self.policy))

    def test_preflight_stops_held_release_before_building(self):
        dump(self.policy_path, self.policy)
        with self.assertRaises(RuntimeError):
            preflight(self.policy_path)
        self.approve_fixture()
        preflight(self.policy_path)

    def test_previous_acceptance_does_not_clear_reported_builds(self):
        policy = json.loads(POLICY.read_text(encoding='utf-8'))
        policy['owner_acceptances'] = []
        for reported in policy['reported_files']:
            issues = policy_issues({self.name: reported['sha256']}, policy)
            self.assertTrue(any(x['reason'] == 'antivirus-report-unresolved' for x in issues))
        self.assertTrue(policy_issues({self.name: policy['release_assessment']['accepted_desktop_sha256']}, policy))
        self.assertEqual(policy['release_assessment']['official_360_review'], 'not-obtained')

    def test_owner_acceptance_requires_exact_path_hash_vendor_and_scope(self):
        policy = json.loads(POLICY.read_text(encoding='utf-8'))
        acceptance = policy['owner_acceptances'][0]
        files = {acceptance['path']: acceptance['sha256']}
        for key, value in [('path', 'renamed.exe'), ('sha256', '0' * 64),
                           ('vendor', 'other'), ('tag', 'another-release'),
                           ('scope', 'local-only'), ('decision', 'clean'), ('reason', '')]:
            changed = copy.deepcopy(policy)
            changed['owner_acceptances'][0][key] = value
            with self.subTest(field=key):
                self.assertTrue(policy_issues(files, changed))

    def test_installer_evidence_is_bound_to_its_payload(self):
        self.approve_fixture()
        installer = self.root / 'installer'
        installer.mkdir()
        (installer / 'setup.exe').write_bytes(b'Synthetic harmless installer fixture')
        result = json.loads(self.evidence.read_text())
        result.update(files=inventory(installer), payload_files=self.files,
                      scanned_files=len(inventory(installer)))
        dump(self.evidence, result)
        require_result(installer, self.evidence, self.policy_path, payload=self.app)
        with self.assertRaises(RuntimeError):
            require_result(installer, self.evidence, self.policy_path)
        self.component.write_bytes(b'Changed harmless payload')
        with self.assertRaises(RuntimeError):
            require_result(installer, self.evidence, self.policy_path, payload=self.app)

    def test_missing_component_cannot_bypass_review(self):
        self.assertTrue(policy_issues({'other.exe': '0' * 64}, self.policy))

    def test_review_is_bound_to_exact_file_hash(self):
        self.approve_fixture()
        self.assertEqual(policy_issues(self.files, self.policy), [])
        changed = {self.name: '0' * 64}
        self.assertTrue(policy_issues(changed, self.policy))

    def test_pending_review_blocks_before_tool_download_or_execution(self):
        dump(self.policy_path, self.policy)
        with patch('security_verify.scanner') as tool, patch('security_verify.subprocess.run') as process:
            with self.assertRaises(RuntimeError):
                scan(self.app, self.root / 'out', self.root / 'cache', self.policy_path)
            tool.assert_not_called()
            process.assert_not_called()
        self.assertFalse(json.loads((self.root / 'out/results.json').read_text())['passed'])

    def test_review_and_scan_pass_are_both_required(self):
        self.approve_fixture()
        require_result(self.app, self.evidence, self.policy_path)
        for field, value in [('passed', False), ('scanner_exit_code', 1), ('infected_files', 1),
                             ('scanned_files', 0), ('known_viruses', 0), ('engine_version', 'unknown')]:
            with self.subTest(field=field):
                result = self.approve_fixture()
                result[field] = value
                dump(self.evidence, result)
                with self.assertRaises(RuntimeError):
                    require_result(self.app, self.evidence, self.policy_path)

    def test_payload_changed_after_scan_is_rejected(self):
        self.approve_fixture()
        self.component.write_bytes(b'Changed harmless fixture')
        with self.assertRaises(RuntimeError):
            require_result(self.app, self.evidence, self.policy_path)

    def test_missing_evidence_is_rejected(self):
        self.approve_fixture()
        self.evidence.unlink()
        with self.assertRaises(FileNotFoundError):
            require_result(self.app, self.evidence, self.policy_path)

    def test_policy_changed_after_scan_is_rejected(self):
        self.approve_fixture()
        changed = copy.deepcopy(self.policy)
        changed['reviews'][0]['review_id'] = 'SYNTHETIC-OTHER-REVIEW'
        dump(self.policy_path, changed)
        with self.assertRaises(RuntimeError):
            require_result(self.app, self.evidence, self.policy_path)

    def test_stale_future_and_naive_scan_timestamps_are_rejected(self):
        for checked in [datetime.now(timezone.utc) - timedelta(days=2),
                        datetime.now(timezone.utc) + timedelta(hours=1), datetime.now()]:
            result = self.approve_fixture()
            result['checked_at'] = checked.isoformat()
            dump(self.evidence, result)
            with self.assertRaises(RuntimeError):
                require_result(self.app, self.evidence, self.policy_path)

    def test_extensionless_pe_is_included(self):
        (self.app / 'tool.data').write_bytes(b'MZ harmless fixture')
        self.assertIn('tool.data', inventory(self.app))

    def test_scanner_detection_error_and_incomplete_summary_fail_closed(self):
        from types import SimpleNamespace
        for code, count, infected in [(1, 1, 1), (2, 1, 0), (0, 0, 0)]:
            self.approve_fixture()
            cache = self.root / 'cache'
            cache.mkdir(exist_ok=True)
            def invoke(args, **kwargs):
                if 'clamscan.exe' in args[0]:
                    kwargs['stdout'].write(('Known viruses: 1\nScanned files: ' + str(count)
                                            + '\nInfected files: ' + str(infected) + '\n').encode())
                    return SimpleNamespace(returncode=code)
                return SimpleNamespace(returncode=0)
            with patch('security_verify.scanner', return_value=self.root), \
                    patch('security_verify.subprocess.run', side_effect=invoke):
                with self.assertRaises(RuntimeError):
                    scan(self.app, self.root / 'out', cache, self.policy_path)
            result = json.loads((self.root / 'out/results.json').read_text())
            self.assertFalse(result['passed'])


if __name__ == '__main__':
    unittest.main(verbosity=2)
