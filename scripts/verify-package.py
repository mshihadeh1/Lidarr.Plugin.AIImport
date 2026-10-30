#!/usr/bin/env python3
import hashlib
import pathlib
import sys
import zipfile

EXPECTED = {
    "Lidarr.Plugin.AIImport.dll",
    "lidarr-hardlink-import",
    "LICENSE",
    "SOURCE.txt",
}

if len(sys.argv) != 3:
    raise SystemExit("usage: verify-package.py <archive> <version>")
archive = pathlib.Path(sys.argv[1])
version = sys.argv[2]
if archive.name != f"Lidarr.Plugin.AIImport-v{version}.net8.0.zip":
    raise SystemExit(f"unexpected archive name: {archive.name}")
with zipfile.ZipFile(archive) as package:
    infos = package.infolist()
    names = [item.filename for item in infos]
    if len(names) != len(EXPECTED) or set(names) != EXPECTED:
        raise SystemExit(f"unexpected package entries: {names}")
    helper = package.getinfo("lidarr-hardlink-import")
    if (helper.external_attr >> 16) & 0o777 != 0o555:
        raise SystemExit("native helper archive mode is not 0555")
    dll = package.read("Lidarr.Plugin.AIImport.dll")
    if b"Lidarr.Plugin.AIImport.Ui.ai-import.html" not in dll:
        raise SystemExit("embedded workspace resource is missing")
sidecar = archive.with_suffix(archive.suffix + ".sha256")
expected = sidecar.read_text(encoding="utf-8").split()[0]
actual = hashlib.sha256(archive.read_bytes()).hexdigest()
if expected != actual:
    raise SystemExit("archive checksum sidecar does not match")
print(f"Verified four-file v{version} package: {actual}")
