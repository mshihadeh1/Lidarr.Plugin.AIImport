#!/usr/bin/env python3
"""Exercise one disposable AI Import album through Lidarr.

This script never creates, moves, or deletes media. It invokes the saved in-process
provider and verifies that every source inode has a second path under the declared
library root. Pass --execute explicitly to cross the import boundary.
"""

import argparse
import hashlib
import json
import os
import pathlib
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

AUDIO_EXTENSIONS = {".flac", ".mp3", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".aif", ".aiff", ".ape", ".wv", ".alac"}


def request(base, api_key, method, path, body=None):
    payload = None if body is None else json.dumps(body).encode("utf-8")
    req = urllib.request.Request(
        base.rstrip("/") + "/api/v1" + path,
        data=payload,
        method=method,
        headers={"X-Api-Key": api_key, "Accept": "application/json", "Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(req, timeout=90) as response:
            return json.loads(response.read() or b"null")
    except urllib.error.HTTPError as error:
        detail = error.read(1024 * 1024).decode("utf-8", "replace")
        raise RuntimeError(f"HTTP {error.code} for {path}: {detail}") from error


def action(base, api_key, provider, name, **query):
    suffix = urllib.parse.urlencode(query)
    path = f"/notification/action/{urllib.parse.quote(name)}"
    if suffix:
        path += "?" + suffix
    return request(base, api_key, "POST", path, provider)


def digest(path):
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(chunk)
    return value.hexdigest()


def snapshot(folder):
    files = []
    for path in sorted(folder.rglob("*")):
        if path.is_file() and path.suffix.lower() in AUDIO_EXTENSIONS:
            info = path.stat()
            files.append({
                "path": path,
                "device": info.st_dev,
                "inode": info.st_ino,
                "size": info.st_size,
                "mtime_ns": info.st_mtime_ns,
                "sha256": digest(path),
            })
    if not files:
        raise RuntimeError("the source folder contains no supported audio files")
    return files


def verify_sources(before, library_root, source_root):
    library_inodes = {}
    for path in library_root.rglob("*"):
        if not path.is_file():
            continue
        if path == source_root or source_root in path.parents:
            continue
        info = path.stat()
        library_inodes.setdefault((info.st_dev, info.st_ino), []).append(str(path))

    failures = []
    results = []
    for item in before:
        path = item["path"]
        info = path.stat()
        unchanged = (
            info.st_dev == item["device"] and info.st_ino == item["inode"] and
            info.st_size == item["size"] and info.st_mtime_ns == item["mtime_ns"] and
            digest(path) == item["sha256"]
        )
        destinations = library_inodes.get((item["device"], item["inode"]), [])
        valid = unchanged and bool(destinations) and info.st_nlink >= 2
        results.append({"source": str(path), "destinations": destinations, "linkCount": info.st_nlink, "verified": valid})
        if not valid:
            failures.append(str(path))
    if failures:
        raise RuntimeError("hardlink/source verification failed for: " + ", ".join(failures))
    return results


def main():
    parser = argparse.ArgumentParser(description="Run one disposable real-album AI Import acceptance check")
    parser.add_argument("--url", default=os.getenv("LIDARR_URL", "http://127.0.0.1:8686"))
    parser.add_argument("--api-key", default=os.getenv("LIDARR_API_KEY"))
    parser.add_argument("--provider-id", type=int)
    parser.add_argument("--source-folder", required=True, type=pathlib.Path)
    parser.add_argument("--library-root", required=True, type=pathlib.Path)
    parser.add_argument("--execute", action="store_true", help="approve and execute the proposed import")
    parser.add_argument("--timeout", type=float, default=300)
    args = parser.parse_args()
    if not args.api_key:
        parser.error("set LIDARR_API_KEY or pass --api-key")

    source = args.source_folder.resolve(strict=True)
    library = args.library_root.resolve(strict=True)
    if source == library or source in library.parents:
        parser.error("source folder must not contain or equal the library root")

    providers = request(args.url, args.api_key, "GET", "/notification")
    matches = [p for p in providers if p.get("implementation") == "AiImportConnector" and (args.provider_id is None or p.get("id") == args.provider_id)]
    if len(matches) != 1:
        raise RuntimeError(f"expected one saved AI Import provider, found {len(matches)}")
    provider = matches[0]

    before = snapshot(source)
    discovery = action(args.url, args.api_key, provider, "discover")
    analyses = discovery.get("analyses", []) if isinstance(discovery, dict) else discovery
    selected = [a for a in analyses if pathlib.Path(a.get("folderPath") or "").resolve() == source]
    if len(selected) != 1:
        raise RuntimeError(f"expected one analysis for {source}, found {len(selected)}")
    analysis = selected[0]
    proposal = analysis.get("proposal") or {}
    summary = {
        "analysisId": analysis.get("analysisId"),
        "status": proposal.get("status"),
        "confidence": proposal.get("confidence"),
        "sourceFiles": len(analysis.get("files") or []),
        "mappedTracks": len(proposal.get("tracks") or []),
    }
    print(json.dumps({"discovery": summary}, indent=2))
    if not args.execute:
        print("Discovery completed. Re-run with --execute only after reviewing this disposable album.")
        return 0
    if proposal.get("status") != "proposed" or summary["sourceFiles"] == 0 or summary["mappedTracks"] != summary["sourceFiles"]:
        raise RuntimeError("analysis is not a complete proposed one-to-one mapping")

    approval = action(args.url, args.api_key, provider, "approve", analysisId=analysis["analysisId"])
    execution = action(args.url, args.api_key, provider, "execute", approvalId=approval["approvalId"], executeConfirmation="IMPORT")
    deadline = time.monotonic() + args.timeout
    while True:
        result = action(args.url, args.api_key, provider, "reconcile", approvalId=approval["approvalId"])
        status = str(result.get("status", "")).lower()
        if status in {"succeeded", "1"}:
            break
        if time.monotonic() >= deadline:
            raise RuntimeError(f"reconciliation did not complete: {result}")
        time.sleep(2)

    verified = verify_sources(before, library, source)
    print(json.dumps({"execution": execution, "reconciliation": result, "hardlinks": verified}, indent=2))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(1)
