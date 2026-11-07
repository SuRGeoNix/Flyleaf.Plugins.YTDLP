#!/usr/bin/env python3
"""Create component-version metadata and GitHub release notes without third-party deps."""

import argparse
import base64
import hashlib
import json
import re
import subprocess
import sys
from pathlib import Path
from xml.etree import ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
VERSION_RE = re.compile(r"^\d+\.\d+\.\d+$")
TAG_RE = re.compile(r"^v\d+\.\d+\.\d+$")


def project_property(path: Path, name: str) -> str:
    root = ET.fromstring(path.read_text(encoding="utf-8-sig"))
    values = [node.text.strip() for node in root.iter(name) if node.text]
    if len(values) != 1:
        raise ValueError(f"Expected exactly one {name} in {path}: {values}")
    return values[0]


def nuget_minimum(path: Path) -> str:
    root = ET.fromstring(path.read_text(encoding="utf-8-sig"))
    packages = [node for node in root.iter("PackageReference")
                if node.attrib.get("Include") == "FlyleafLib"]
    if len(packages) != 1:
        raise ValueError("Expected one FlyleafLib PackageReference")
    version = packages[0].attrib.get("Version", "")
    match = re.fullmatch(r"\[(\d+\.\d+\.\d+),\)", version)
    if not match:
        raise ValueError(f"FlyleafLib version must be minimum-inclusive: {version}")
    return match.group(1)


def read_versions(base: Path) -> dict:
    plugin_csproj = base / "Plugin/Plugin.csproj"
    host_csproj = base / "BrowserHost/BrowserHost.csproj"
    extension_manifest = base / "BrowserExtension/manifest.json"
    plugin = project_property(plugin_csproj, "Version")
    host = project_property(host_csproj, "Version")
    extension_info = json.loads(extension_manifest.read_text(encoding="utf-8-sig"))
    extension = extension_info["version"]
    shared_path = base / "Shared/BrowserCookieProtocol.cs"
    if shared_path.exists():
        host_source = shared_path.read_text(encoding="utf-8-sig")
        protocol_match = re.search(r"\bconst\s+int\s+Version\s*=\s*(\d+)\s*;", host_source)
    else:
        # Metadata for older releases predating the shared protocol definition.
        host_source = (base / "BrowserHost/Program.cs").read_text(encoding="utf-8-sig")
        protocol_match = re.search(r"\bconst\s+int\s+Protocol\s*=\s*(\d+)\s*;", host_source)
    if not protocol_match:
        raise ValueError("Could not read browser-cookie protocol version from host source")
    protocol = int(protocol_match.group(1))
    if shared_path.exists():
        origin_match = re.search(r'const\s+string\s+ExtensionOrigin\s*=\s*"chrome-extension://([a-p]{32})/";', host_source)
        if not origin_match:
            raise ValueError("Missing native host extension origin")
        try:
            public_key = base64.b64decode(extension_info["key"], validate=True)
        except (KeyError, ValueError) as exc:
            raise ValueError("Missing or invalid extension public key") from exc
        derived_id = "".join(chr(ord("a") + int(nibble, 16))
                             for nibble in hashlib.sha256(public_key).hexdigest()[:32])
        if derived_id != origin_match.group(1):
            raise ValueError("Extension public key does not match the native host extension origin")
        worker = (base / "BrowserExtension/service-worker.js").read_text(encoding="utf-8-sig")
        worker_protocol = re.search(r"\bconst\s+PROTOCOL\s*=\s*(\d+)\s*;", worker)
        if not worker_protocol or int(worker_protocol.group(1)) != protocol:
            raise ValueError("Browser extension protocol differs from native host protocol")
    for name, version in (("Plugin", plugin), ("Host", host), ("Extension", extension)):
        if not VERSION_RE.fullmatch(version):
            raise ValueError(f"Invalid {name} version: {version}")
    if extension_info.get("manifest_version") != 3:
        raise ValueError("Expected a Manifest V3 extension")
    return {
        "schemaVersion": 1,
        "plugin": {"version": plugin, "assembly": "FlyleafLib.Plugins.YTDLP.dll"},
        "host": {"version": host, "minProtocol": protocol, "maxProtocol": protocol,
                 "asset": "Flyleaf.BrowserCookieHost-win-x64.zip"},
        "extension": {"version": extension,
                      "asset": f"Flyleaf.BrowserExtension-v{extension}.zip"},
        "flyleafLib": {"minimumVersion": nuget_minimum(plugin_csproj)},
    }


def previous_file(tag: str, file: str) -> bytes:
    return subprocess.check_output(["git", "show", f"{tag}:{file}"], cwd=ROOT)


def previous_versions(tag: str) -> dict:
    # A previous release may predate component-versions.json, so inspect its sources.
    import tempfile
    with tempfile.TemporaryDirectory() as directory:
        base = Path(directory)
        for file in ("Plugin/Plugin.csproj", "BrowserHost/BrowserHost.csproj",
                     "BrowserHost/Program.cs", "BrowserExtension/manifest.json",
                     "BrowserExtension/service-worker.js"):
            path = base / file
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(previous_file(tag, file))
        try:
            shared = "Shared/BrowserCookieProtocol.cs"
            path = base / shared
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(previous_file(tag, shared))
        except subprocess.CalledProcessError:
            pass
        return read_versions(base)


def version_tuple(version: str) -> tuple[int, int, int]:
    if not VERSION_RE.fullmatch(version):
        raise ValueError(f"Invalid semantic version: {version}")
    return tuple(int(value) for value in version.split("."))


def status(current: str, previous: str | None) -> str:
    if previous is None:
        return "Initial release"
    if version_tuple(current) < version_tuple(previous):
        raise ValueError(f"Version decreased from {previous} to {current}")
    if current == previous:
        return "Unchanged"
    return f"Updated from {previous}"


def ensure_version_bumped_on_change(tag: str, path: str, version: str, previous: str) -> None:
    """Avoid silently changing extension/host code without changing its version."""
    if version != previous:
        return
    old_tree = subprocess.check_output(["git", "rev-parse", f"{tag}:{path}"], cwd=ROOT).strip()
    new_tree = subprocess.check_output(["git", "rev-parse", f"HEAD:{path}"], cwd=ROOT).strip()
    if old_tree != new_tree:
        raise ValueError(f"{path} changed since {tag} without bumping version {version}")


def make_notes(current: dict, tag: str | None) -> str:
    prev = previous_versions(tag) if tag else None
    if prev is not None:
        # This check ensures 'Unchanged' actually means unchanged in source.
        for component, path in (("host", "BrowserHost"), ("extension", "BrowserExtension")):
            ensure_version_bumped_on_change(
                tag, path, current[component]["version"], prev[component]["version"])
        if (ROOT / "Shared/BrowserCookieProtocol.cs").exists():
            # Shared C# protocol code is part of the native host binary.
            try:
                previous_file(tag, "Shared/BrowserCookieProtocol.cs")
            except subprocess.CalledProcessError:
                pass  # The prior release predates the shared file.
            else:
                ensure_version_bumped_on_change(tag, "Shared", current["host"]["version"], prev["host"]["version"])

    label = f" (compared with `{tag}`)" if tag else ""
    lines = [f"## Component versions{label}", "",
             "| Component | Version | Status |", "|---|---|---|"]
    for key, label in (("plugin", "YTDLP plugin"), ("host", "Native AOT host"),
                       ("extension", "Chrome / Brave extension")):
        previous = prev[key]["version"] if prev else None
        lines.append(f"| {label} | `{current[key]['version']}` | {status(current[key]['version'], previous)} |")
    lines += ["", f"**Minimum FlyleafLib version:** `{current['flyleafLib']['minimumVersion']}`",
              "", "**Extension installation:** Download the versioned extension ZIP, extract it "
              "to a permanent directory and use **Load unpacked** in `chrome://extensions` "
              "or `brave://extensions`. For updates, replace its files and click **Reload**. "
              "There is no automatic extension update via GitHub Releases.", "",
              "The native host and yt-dlp dependencies are managed by the plugin; "
              "the plugin DLL is deployed by each application.", ""]
    return "\n".join(lines) + "\n"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("metadata", "notes"))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--current", type=Path, help="component-versions.json for notes")
    parser.add_argument("--previous-tag", default="", help="Prior published release tag")
    args = parser.parse_args()
    if args.command == "metadata":
        output = json.dumps(read_versions(ROOT), indent=2, ensure_ascii=False) + "\n"
    else:
        if not args.current:
            parser.error("notes requires --current")
        if args.previous_tag and not TAG_RE.fullmatch(args.previous_tag):
            parser.error("Invalid --previous-tag")
        data = json.loads(args.current.read_text(encoding="utf-8-sig"))
        output = make_notes(data, args.previous_tag or None)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(output, encoding="utf-8")
    print(f"Wrote {args.output}")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, subprocess.CalledProcessError, OSError, KeyError) as exc:
        print(f"Release metadata error: {exc}", file=sys.stderr)
        sys.exit(1)
