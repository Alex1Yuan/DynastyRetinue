import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

REPO = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("pack_release", REPO / "tools" / "pack_release.py")
pack_release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pack_release)


class ReleasePackageTests(unittest.TestCase):
    def setUp(self):
        scratch = (REPO / "_tmp").resolve()
        scratch.mkdir(exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="package-test-", dir=scratch)
        self.root = Path(self.temporary.name).resolve()
        self.assertTrue(self.root.is_relative_to(scratch))
        self.addCleanup(self.temporary.cleanup)
        self.src = self.root / "src" / "DynastyRetinue"
        (self.src / "bin" / "Release").mkdir(parents=True)
        for name in ("Info.json", "archetypes.json", "plans.json", "looks.json", "l10n_en.json"):
            (self.src / name).write_text(json.dumps({"Version": "1.8.0"}), encoding="utf-8")
        (self.src / "bin" / "Release" / "DynastyRetinue.dll").write_bytes(b"test-assembly")
        for name in ("README.md", "LICENSE"):
            (self.root / name).write_text(name, encoding="utf-8")
        (self.root / "dist").mkdir()
        self.old = self.root / "dist" / "DynastyRetinue-1.7.101.zip"
        self.old.write_bytes(b"old-release-must-survive")

    def test_allowlist_and_history(self):
        for name in ("Settings.xml", "dynasty_log.txt", "dynasty_dev.flag", "probe.tsv"):
            (self.src / name).write_text("private", encoding="utf-8")
        (self.src / "bin" / "Release" / "DynastyRetinue.pdb").write_bytes(b"private-pdb")
        result = pack_release.create_package(self.root, "1.8.0")
        with zipfile.ZipFile(result) as archive:
            self.assertEqual(set(archive.namelist()), {
                "DynastyRetinue/" + name for name in ("Info.json", "archetypes.json", "plans.json",
                "looks.json", "l10n_en.json", "DynastyRetinue.dll", "README.md", "LICENSE")})
            self.assertEqual(archive.read("DynastyRetinue/DynastyRetinue.dll"), b"test-assembly")
        self.assertEqual(self.old.read_bytes(), b"old-release-must-survive")
        self.assertEqual(len(list((self.root / "dist").iterdir())), 2)

    def test_missing_required_file_keeps_existing_package(self):
        result = pack_release.create_package(self.root, "1.8.0")
        original = result.read_bytes()
        (self.src / "looks.json").unlink()
        with self.assertRaises(FileNotFoundError):
            pack_release.create_package(self.root, "1.8.0")
        self.assertEqual(result.read_bytes(), original)

    def test_version_mismatch_creates_no_archive(self):
        with self.assertRaises(ValueError):
            pack_release.create_package(self.root, "1.8.1")
        self.assertEqual(list((self.root / "dist").iterdir()), [self.old])

    def test_invalid_version_rejected(self):
        with self.assertRaises(ValueError):
            pack_release.create_package(self.root, "../../outside")
        self.assertEqual(list((self.root / "dist").iterdir()), [self.old])


if __name__ == "__main__":
    unittest.main()
