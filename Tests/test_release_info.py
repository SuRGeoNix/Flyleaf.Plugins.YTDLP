"""Smoke tests for release notes and component version enforcement."""

import json
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from xml.etree import ElementTree as ET

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

        project = self.root / "Plugin/Plugin.csproj"
        plugin_xml = ET.parse(project)
        self.plugin_version = plugin_xml.findtext("./PropertyGroup/Version")
        package_ref = plugin_xml.find(".//PackageReference[@Include='FlyleafLib']")
        self.flyleaf_minimum = package_ref.attrib["Version"][1:-2]
        self.host_version = ET.parse(self.root / "BrowserHost/BrowserHost.csproj").findtext("./PropertyGroup/Version")
        extension_manifest = self.root / "BrowserExtension/manifest.json"
        self.extension_version = json.loads(extension_manifest.read_text(encoding="utf-8-sig"))["version"]
        protocol_source = (self.root / "Shared/BrowserCookieProtocol.cs").read_text(encoding="utf-8-sig")
        self.protocol_version = int(re.search(r"\bconst\s+int\s+Version\s*=\s*(\d+)\s*;", protocol_source).group(1))

        # Pick a strictly earlier synthetic plugin version, regardless of the current release.
        major, minor, patch = map(int, self.plugin_version.split("."))
        if patch:
            self.previous_plugin_version = f"{major}.{minor}.{patch - 1}"
        elif minor:
            self.previous_plugin_version = f"{major}.{minor - 1}.0"
        elif major:
            self.previous_plugin_version = f"{major - 1}.0.0"
        else:
            self.fail("A synthetic previous release requires a nonzero plugin version")
        self.previous_tag = f"v{self.previous_plugin_version}"
        self.run_git("init", "-q")
        original = project.read_text(encoding="utf-8-sig")
        self.assertIn(f"<Version>{self.plugin_version}</Version>", original)
        project.write_text(original.replace(f"<Version>{self.plugin_version}</Version>",
                                            f"<Version>{self.previous_plugin_version}</Version>"), encoding="utf-8")
        self.run_git("add", ".")
        self.run_git("-c", "user.name=Test", "-c", "user.email=test@example.com",
                     "commit", "-qm", "Initial release")
        self.run_git("tag", self.previous_tag)
        project.write_text(original, encoding="utf-8")
        self.run_git("add", ".")
        self.run_git("-c", "user.name=Test", "-c", "user.email=test@example.com",
                     "commit", "-qm", f"Plugin {self.plugin_version}")

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
        self.assertEqual(data["plugin"]["version"], self.plugin_version)
        self.assertEqual(data["host"]["version"], self.host_version)
        self.assertEqual(data["host"]["minProtocol"], self.protocol_version)
        self.assertEqual(data["extension"]["asset"], f"Flyleaf.BrowserExtension-v{self.extension_version}.zip")
        self.assertEqual(data["flyleafLib"]["minimumVersion"], self.flyleaf_minimum)

    def test_unchanged_extension_and_host_reported(self):
        self.run_info("metadata", "--output", "versions.json")
        self.run_info("notes", "--current", "versions.json", "--previous-tag",
                      self.previous_tag, "--output", "notes.md")
        notes = (self.root / "notes.md").read_text()
        self.assertIn(f"| YTDLP plugin | `{self.plugin_version}` | Updated from {self.previous_plugin_version} |", notes)
        self.assertIn(f"| Native AOT host | `{self.host_version}` | Unchanged |", notes)
        self.assertIn(f"| Chrome / Brave extension | `{self.extension_version}` | Unchanged |", notes)

    def test_first_release_notes(self):
        self.run_info("metadata", "--output", "versions.json")
        self.run_info("notes", "--current", "versions.json", "--output", "notes.md")
        notes = (self.root / "notes.md").read_text()
        self.assertIn(f"| YTDLP plugin | `{self.plugin_version}` | Initial release |", notes)
        self.assertIn(f"| Native AOT host | `{self.host_version}` | Initial release |", notes)

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
                                self.previous_tag, "--output", "notes.md", expect_ok=False)
        self.assertIn("without bumping version", failure.stderr)

    def test_protocol_mismatch_is_rejected(self):
        worker = self.root / "BrowserExtension/service-worker.js"
        worker.write_text(worker.read_text().replace(f"const PROTOCOL = {self.protocol_version};",
                                                    f"const PROTOCOL = {self.protocol_version + 1};"))
        failure = self.run_info("metadata", "--output", "versions.json", expect_ok=False)
        self.assertIn("protocol differs", failure.stderr)

    def test_extension_change_requires_version_bump(self):
        self.run_info("metadata", "--output", "versions.json")
        with (self.root / "BrowserExtension/service-worker.js").open("a") as file:
            file.write("\n// updated test fixture\n")
        self.commit()
        failed = self.run_info("notes", "--current", "versions.json",
                               "--previous-tag", self.previous_tag, "--output", "notes.md",
                               expect_ok=False)
        self.assertIn("without bumping version", failed.stderr)
        path = self.root / "BrowserExtension/manifest.json"
        major, minor, patch = map(int, self.extension_version.split("."))
        updated_version = f"{major}.{minor}.{patch + 1}"
        content = path.read_text()
        self.assertIn(f'"version": "{self.extension_version}"', content)
        path.write_text(content.replace(f'"version": "{self.extension_version}"',
                                        f'"version": "{updated_version}"'))
        self.commit()
        self.run_info("metadata", "--output", "versions.json")
        self.run_info("notes", "--current", "versions.json", "--previous-tag",
                      self.previous_tag, "--output", "notes.md")
        self.assertIn(f"| Chrome / Brave extension | `{updated_version}` | Updated from {self.extension_version} |",
                      (self.root / "notes.md").read_text())


if __name__ == "__main__":
    unittest.main()
