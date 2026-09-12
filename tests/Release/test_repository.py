"""Keep generated evidence and downloaded binaries out of source releases."""
from pathlib import Path
import json
import re
import subprocess
import unittest
from urllib.parse import unquote, urlsplit


ROOT = Path(__file__).resolve().parents[2]


class RepositoryTests(unittest.TestCase):
    def test_candidate_preferences_decline_online_features_without_stock_agreement_changes(self):
        preferences = json.loads((ROOT / 'tests/Release/offline-redux-config.json').read_text())
        self.assertEqual(set(preferences), {'Online Services', 'Discord'})
        online = preferences['Online Services']
        self.assertEqual(online.pop('Accepted Consent Version'), 1)
        self.assertIs(online.pop('First Launch Consent Completed'), True)
        self.assertEqual(set(online), {
            'Check Mod Versions', 'Download Localization Updates', 'Download Mission Updates',
            'Download Main Menu Vessels', 'Share Anonymous Usage Data',
            'Send Crash and Error Reports', 'Link Telemetry to Anonymous Session ID',
        })
        self.assertTrue(all(value is False for value in online.values()))
        self.assertEqual(preferences['Discord'], {'Enable Rich Presence': False})

    def source_files(self):
        result = subprocess.run(
            ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"],
            cwd=ROOT, check=True, capture_output=True,
        )
        return sorted({Path(p.decode()) for p in result.stdout.split(b"\0")
                       if p and (ROOT / p.decode()).is_file()})

    def test_no_downloaded_tools_or_evidence(self):
        forbidden = {".dll", ".exe", ".pdb", ".zip", ".mp4", ".exr"}
        unexpected = [str(p) for p in self.source_files()
                      if p.suffix.lower() in forbidden
                      or p.parts[0] in {"Artifacts", "Utilities"}
                      or (p.parts[0] == "Packages" and len(p.parts) > 2)]
        self.assertEqual(unexpected, [], "Download tools separately; store evidence outside the repo")

    def test_documentation_links_resolve(self):
        broken = []
        for relative in self.source_files():
            if relative.suffix != ".md":
                continue
            document = ROOT / relative
            for target in re.findall(r"\]\(([^)]+)\)", document.read_text(encoding="utf-8-sig")):
                target = urlsplit(target.strip("<>"))
                if target.scheme or target.netloc or not target.path:
                    continue
                if not (document.parent / unquote(target.path)).exists():
                    broken.append(f"{relative}: {target.path}")
        self.assertEqual(broken, [])


if __name__ == "__main__":
    unittest.main()
