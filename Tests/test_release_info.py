"""Smoke tests for release notes and component version enforcement."""

import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SOURCE = Path(__file__).resolve().parent.parent


class ReleaseNotesTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for relative in (
            "Scripts/release_info.py",
            "Plugin/Plugin.csproj",
            "BrowserHost/BrowserHost.csproj",
            "BrowserHost/Program.cs",
            "Shared/BrowserCookieProtocol.cs",
            "BrowserExtension/manifest.json",
            "BrowserExtension/service-worker.js",
        ):
            target = self.root / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(SOURCE / relative, target)
        self.run_git("init", "-q")
        # Simulate a prior release to verify version comparison.
        project = self.root / "Plugin/Plugin.csproj"
        project.write_text(project.read_text().replace("<Version>2.0.0</Version>",
                                                       "<Version>1.9.9</Version>"))
        self.run_git("add", ".")
        self.run_git("-c", "user.name=Test", "-c", "user.email=test@example.com",
                     "commit", "-qm", "Initial release")
        self.run_git("tag", "v1.9.9")
        project.write_text(project.read_text().replace("<Version>1.9.9</Version>",
                                                       "<Version>2.0.0</Version>"))
        self.run_git("add", ".")
        self.run_git("-c", "user.name=Test", "-c", "user.email=test@example.com",
                     "commit", "-qm", "Plugin 2.0.0")

    def run_git(self, *args):
        return subprocess.run(["git", *args], cwd=self.root, capture_output=True,
                              text=True, check=True)

    def run_info(self, *args, expect_ok=True):
        result = subprocess.run([sys.executable, "Scripts/release_info.py", *args],
                                cwd=self.root, capture_output=True, text=True)
        if expect_ok:
            self.assertEqual(result.returncode, 0, result.stderr)
        else:
            self.assertNotEqual(result.returncode, 0)
        return result

    def commit(self):
        self.run_git("add", ".")
        self.run_git("-c", "user.name=Test", "-c", "user.email=test@example.com",
                     "commit", "-qm", "Modified sources")

    def test_metadata_versions_and_assets(self):
        self.run_info("metadata", "--output", "versions.json")
        data = json.loads((self.root / "versions.json").read_text())
        self.assertEqual(data["plugin"]["version"], "2.0.0")
        self.assertEqual(data["host"]["version"], "1.0.0")
        self.assertEqual(data["host"]["minProtocol"], 3)
        self.assertEqual(data["extension"]["asset"], "Flyleaf.BrowserExtension-v1.0.0.zip")
        self.assertEqual(data["flyleafLib"]["minimumVersion"], "3.11.16")

    def test_unchanged_extension_and_host_reported(self):
        self.run_info("metadata", "--output", "versions.json")
        # The plugin advances while host and extension stay unchanged.
        self.run_info("notes", "--current", "versions.json", "--previous-tag",
                      "v1.9.9", "--output", "notes.md")
        notes = (self.root / "notes.md").read_text()
        self.assertIn("| YTDLP plugin | `2.0.0` | Updated from 1.9.9 |", notes)
        self.assertIn("| Native AOT host | `1.0.0` | Unchanged |", notes)
        self.assertIn("| Chrome / Brave extension | `1.0.0` | Unchanged |", notes)

    def test_first_release_notes(self):
        self.run_info("metadata", "--output", "versions.json")
        self.run_info("notes", "--current", "versions.json", "--output", "notes.md")
        notes = (self.root / "notes.md").read_text()
        self.assertIn("| YTDLP plugin | `2.0.0` | Initial release |", notes)
        self.assertIn("| Native AOT host | `1.0.0` | Initial release |", notes)

    def test_extension_key_must_match_native_origin(self):
        manifest = self.root / "BrowserExtension/manifest.json"
        content = manifest.read_text()
        manifest.write_text(content.replace('"key": "MIIB', '"key": "AAAA'))
        failure = self.run_info("metadata", "--output", "versions.json", expect_ok=False)
        self.assertIn("public key does not match", failure.stderr)

    def test_host_change_requires_version_bump(self):
        self.run_info("metadata", "--output", "versions.json")
        with (self.root / "BrowserHost/Program.cs").open("a") as file:
            file.write("\n// updated test fixture\n")
        self.commit()
        failure = self.run_info("notes", "--current", "versions.json", "--previous-tag",
                                "v1.9.9", "--output", "notes.md", expect_ok=False)
        self.assertIn("without bumping version", failure.stderr)

    def test_protocol_mismatch_is_rejected(self):
        worker = self.root / "BrowserExtension/service-worker.js"
        worker.write_text(worker.read_text().replace("const PROTOCOL = 3;", "const PROTOCOL = 4;"))
        failure = self.run_info("metadata", "--output", "versions.json", expect_ok=False)
        self.assertIn("protocol differs", failure.stderr)

    def test_extension_change_requires_version_bump(self):
        self.run_info("metadata", "--output", "versions.json")
        with (self.root / "BrowserExtension/service-worker.js").open("a") as file:
            file.write("\n// updated test fixture\n")
        self.commit()
        failed = self.run_info("notes", "--current", "versions.json",
                               "--previous-tag", "v1.9.9", "--output", "notes.md",
                               expect_ok=False)
        self.assertIn("without bumping version", failed.stderr)
        path = self.root / "BrowserExtension/manifest.json"
        text = path.read_text().replace('"version": "1.0.0"', '"version": "1.1.0"')
        path.write_text(text)
        self.commit()
        self.run_info("metadata", "--output", "versions.json")
        self.run_info("notes", "--current", "versions.json", "--previous-tag",
                      "v1.9.9", "--output", "notes.md")
        self.assertIn("| Chrome / Brave extension | `1.1.0` | Updated from 1.0.0 |",
                      (self.root / "notes.md").read_text())


if __name__ == "__main__":
    unittest.main()
