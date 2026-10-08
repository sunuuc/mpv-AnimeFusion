"""Product identity contracts for source and extracted portable releases."""
import json
import os
import re
from pathlib import Path
import subprocess
import sys
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
META = json.loads((ROOT / 'release.json').read_text(encoding='utf-8'))
NAME = 'mpv-AnimeFusion'
PROJECT_NAME = 'mpv-AnimeFusion'
ENTRY_POINTS = (NAME + '.exe', NAME + 'Manager.exe', NAME + 'Updater.exe')


class BrandingTests(unittest.TestCase):
    def test_release_identity_and_documented_entry_points(self):
        self.assertEqual(META['name'], PROJECT_NAME)
        self.assertEqual(META['tag'], 'standalone-v' + META['version'])
        for filename in ('README.md', 'docs/standalone.md'):
            text = (ROOT / filename).read_text(encoding='utf-8')
            self.assertTrue(text.startswith('# ' + PROJECT_NAME))
            self.assertIn(ENTRY_POINTS[0], text)
            self.assertIn(ENTRY_POINTS[1], text)
            self.assertNotIn('AnimeJaNai-zh-CN-', text)
        self.assertIn('https://github.com/sunuuc/mpv-AnimeFusion/releases',
                      (ROOT / 'README.md').read_text(encoding='utf-8'))

    def test_distribution_names_follow_project_identity(self):
        for filename in ('tools/standalone/build.py', 'tools/standalone/publish.py'):
            text = (ROOT / filename).read_text(encoding='utf-8')
            self.assertIn('f\'{META["name"]}-{META["version"]}', text)
            self.assertNotIn('f\'AnimeVE-{META["version"]}', text)
        publisher = (ROOT / 'tools/standalone/publish.py').read_text(encoding='utf-8')
        self.assertIn('f\'{META["name"]} {META["version"]}', publisher)
        documentation = (ROOT / 'docs/standalone.md').read_text(encoding='utf-8')
        self.assertIn(PROJECT_NAME + '-' + META['version'] + '-win-x64.7z', documentation)
        self.assertIn(PROJECT_NAME + '-' + META['version'] + '-sources.zip', documentation)
        for filename in ('src/player/src/MpvNet.Windows/MpvNet.Windows.csproj',
                         'src/manager/AnimeJaNaiConfEditor/AnimeJaNaiConfEditor.csproj',
                         'tools/standalone/Updater.csproj'):
            project = ET.parse(ROOT / filename)
            version = project.findtext('.//Version') or project.findtext('.//InformationalVersion')
            self.assertEqual(version, META['version'])

    def test_compiled_names_and_component_launcher_agree(self):
        projects = ('src/player/src/MpvNet.Windows/MpvNet.Windows.csproj',
                    'src/manager/AnimeJaNaiConfEditor/AnimeJaNaiConfEditor.csproj',
                    'tools/standalone/Updater.csproj')
        for project, executable in zip(projects, ENTRY_POINTS):
            xml = ET.parse(ROOT / project)
            self.assertEqual(xml.findtext('.//AssemblyName'), Path(executable).stem)
        for filename in ('portable_config/input.conf', 'portable_config/input-animejanai.conf',
                         'portable_config/scripts/player_ui.lua'):
            text = (ROOT / filename).read_text(encoding='utf-8')
            self.assertIn(ENTRY_POINTS[1], text)
            self.assertNotIn('AnimeJaNaiManager.exe', text)
        self.assertIn(ENTRY_POINTS[2], ((ROOT / projects[1]).parent /
            'ViewModels/ComponentManagerViewModel.cs').read_text(encoding='utf-8'))

    def test_localized_window_and_about_identity(self):
        for filename in ('src/player/src/MpvNet/LanguageStrings.json',
                         'src/manager/AnimeJaNaiConfEditor/LanguageStrings.json'):
            data = json.loads((ROOT / filename).read_text(encoding='utf-8'))
            self.assertEqual(data['keys']['sa3d61b60b957b367'], NAME + ' Manager')
            self.assertEqual(data['translations'][NAME + ' Manager'], NAME + ' 管理器')
            self.assertEqual(data['keys']['s5926043be98ac75f'], 'About ' + NAME)
        title = (ROOT / 'src/player/src/MpvNet.Windows/WinForms/MainForm.cs').read_text(encoding='utf-8')
        self.assertIn('text = "AnimeVE"', title)
        self.assertIn('"} - AnimeVE"', title)
        about = (ROOT / 'src/player/src/MpvNet.Windows/WPF/Views/AboutWindow.xaml').read_text(encoding='utf-8')
        self.assertIn('>AnimeVE', about)
        resources = (ROOT / 'src/player/src/MpvNet.Windows/WPF/WpfApplication.cs').read_text(encoding='utf-8')
        self.assertIn('AnimeVE;component/WPF/Resources.xaml', resources)
        self.assertNotIn('mpvnet;component', resources)
        acceptance = (ROOT / 'tools/standalone/test_complete.py').read_text(encoding='utf-8')
        self.assertIn("('AnimeVE Manager', 'AnimeVE 管理器')", acceptance)
        self.assertNotIn("'AnimeJaNai' in title", acceptance)

    def test_original_upstream_model_and_license_identity_is_preserved(self):
        self.assertIn('the-database/AnimeJaNaiManager', (ROOT / 'docs/open-source-notices.md').read_text(encoding='utf-8'))

    def test_bilingual_readme_about_and_statistics_shortcut(self):
        english = (ROOT / 'README.en.md').read_text(encoding='utf-8')
        chinese = (ROOT / 'README.md').read_text(encoding='utf-8')
        self.assertIn('(README.md)', english)
        self.assertIn('(README.en.md)', chinese)
        for text in (english, chinese):
            self.assertTrue(text.startswith('# ' + PROJECT_NAME))
            self.assertIn('| Tab |', text)
        manager = (ROOT / 'src/manager/AnimeJaNaiConfEditor/Views/MainWindow.axaml').read_text(encoding='utf-8')
        data = json.loads((ROOT / 'src/manager/AnimeJaNaiConfEditor/LanguageStrings.json').read_text(encoding='utf-8'))
        for key in re.findall(r'local:Text Key=([^}]+)', manager):
            self.assertIn(key, data['keys'])
        self.assertNotIn('aboutLicenseScope', manager)
        for key in ('aboutChanges', 'aboutDownloads'):
            self.assertNotIn(key, manager)
        self.assertLess(manager.index('Key=aboutCurrent'),manager.index('Content="sunuuc/mpv-AnimeFusion"'))
        self.assertLess(manager.index('Key=aboutUpstream'),manager.index('Content="the-database/mpv-AnimeJaNai"'))
        for key in ('aboutCurrent','aboutUpstream'):
            self.assertIn(data['keys'][key],data['translations'])
        self.assertIn('RIFE',data['translations'][data['keys']['aboutUpstream']])
        self.assertIn('mpv-AnimeFusion',data['translations'][data['keys']['aboutCurrent']])
        player = (ROOT / 'src/player/src/MpvNet/App.cs').read_text(encoding='utf-8')
        self.assertIn('https://github.com/sunuuc/mpv-AnimeFusion', player)
        self.assertIn('OPEN_SOURCE_NOTICES.md', player)
        for name in ('input.conf', 'input-animejanai.conf'):
            rows = (ROOT / 'portable_config' / name).read_text(encoding='utf-8').splitlines()
            bindings = [row.split('#', 1)[0].split() for row in rows if row.strip() and not row.startswith('#')]
            self.assertEqual([row for row in bindings if row[0] == 'TAB'],
                             [['TAB', 'script-binding', 'stats/display-stats-toggle']])
        self.assertIn('2x_AnimeJaNai', (ROOT / 'animejanai/animejanai.conf').read_text(encoding='utf-8'))
        self.assertTrue((ROOT / 'THIRD_PARTY_LICENSES/AnimeJaNaiManager-GPL-3.0.txt').is_file())


def verify_package(folder):
    app = Path(folder).resolve()
    assert {p.name for p in app.iterdir()} == {
        'mpv-AnimeFusion.exe', 'mpv-AnimeFusionManager.exe', 'app', 'docs', 'portable_config', 'animejanai'}
    assert not list(app.glob('*.dll'))
    for name in ('mpv-AnimeFusion', 'mpv-AnimeFusionManager'):
        assert ('app/'+name+'.dll').encode() in (app/(name+'.exe')).read_bytes()
    for name in ('LICENSE', 'OPEN_SOURCE_NOTICES.md', 'THIRD_PARTY_LICENSES'):
        assert (app/'docs'/name).exists(), name
    for filename in ENTRY_POINTS:
        assert (app / ('app/'+filename if filename.endswith('Updater.exe') else filename)).is_file(), filename
    for filename in ('mpvnet.exe', 'AnimeJaNaiManager.exe', 'AnimeJaNaiUpdater.exe'):
        assert not (app / filename).exists(), 'Obsolete entry point: ' + filename
    env = dict(os.environ, BRANDING_APP=str(app))
    script = '''
$ErrorActionPreference = 'Stop'
$names = 'mpv-AnimeFusion.exe', 'mpv-AnimeFusionManager.exe', 'app/mpv-AnimeFusionUpdater.exe'
$result = foreach ($name in $names) {
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $env:BRANDING_APP $name))
    @{name=$name;product=$info.ProductName;version=$info.ProductVersion}
}
ConvertTo-Json -InputObject @($result) -Compress
'''
    result = subprocess.run(['powershell.exe', '-NoProfile', '-NonInteractive', '-Command', script],
                            env=env, capture_output=True, text=True, check=True)
    records = json.loads(result.stdout)
    assert len(records) == 3
    for item in records:
        assert item['product'] == NAME, item
        assert item['version'].split('+', 1)[0] == META['version'], item
    print('PASS extracted product entry points and binary metadata', records)


if __name__ == '__main__':
    if len(sys.argv) > 1:
        verify_package(sys.argv.pop(1))
    unittest.main()
