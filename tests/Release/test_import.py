"""Exercise first-build reference import without Unity or game binaries."""
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]


class ImportTests(unittest.TestCase):
    def test_import_preserves_inputs_filters_dependencies_and_writes_valid_metadata(self):
        with tempfile.TemporaryDirectory(prefix="betteraa-import-") as temporary:
            root = Path(temporary)
            project = root / "project"
            game = root / "game"
            editor = root / "editor"
            managed = game / "KSP2_x64_Data/Managed"
            provided = editor / "Data/Managed"
            cache = project / "Library/PackageCache/provider"
            for folder in (managed, provided, cache, project / "tools"):
                folder.mkdir(parents=True)
            (game / "KSP2_x64.exe").touch()
            for name in ("Assembly-CSharp.dll", "EditorOnly.dll", "PackageOnly.dll", "SourcePackage.dll"):
                (managed / name).write_bytes(b"unaltered input")
            (provided / "EditorOnly.dll").touch()
            (cache / "PackageOnly.dll").touch()
            (cache / "SourcePackage.asmdef").write_text('{"name":"SourcePackage"}')
            script = project / "tools/Import-GameReferences.ps1"
            shutil.copyfile(ROOT / "tools" / script.name, script)
            subprocess.run([
                "pwsh", "-NoProfile", "-File", str(script),
                "-Ksp2Root", str(game), "-UnityEditorRoot", str(editor), "-EditorVersion", "6000.4.1f1",
            ], check=True, capture_output=True, text=True)
            package = project / "Packages/KSP2_x64"
            self.assertEqual([p.name for p in package.glob("*.dll")], ["Assembly-CSharp.dll"])
            self.assertEqual((package / "Assembly-CSharp.dll").read_bytes(), b"unaltered input")
            for dll in managed.glob("*.dll"):
                self.assertEqual(dll.read_bytes(), b"unaltered input")
            metadata = (package / "Assembly-CSharp.dll.meta").read_text()
            self.assertTrue(metadata.endswith("\n"), "Unity rejects unterminated metadata")
            self.assertIn("PluginImporter:", metadata)
            self.assertIn("isExplicitlyReferenced: 1", metadata)
            manifest = json.loads((package / "package.json").read_text())
            self.assertEqual(manifest["name"], "ksp2_x64")
            # A legacy editor refuses packages that declare a newer minimum Unity version.
            self.assertEqual(manifest["unity"], "6000.4")


if __name__ == "__main__":
    unittest.main()
