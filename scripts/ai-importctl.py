#!/usr/bin/env python3
"""Minimal authenticated client for the AI Import companion."""

import argparse
import getpass
import json
import os
import stat
import sys
import time
import urllib.error
import urllib.request


def call(base, api_token, method, path, body=None):
    data = None if body is None else json.dumps(body).encode("utf-8")
    request = urllib.request.Request(
        base.rstrip("/") + path,
        data=data,
        method=method,
        headers={
            "X-Companion-Api-Key": api_token,
            "Content-Type": "application/json",
            "Accept": "application/json",
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=65) as response:
            payload = response.read(1024 * 1024 + 1)
            if len(payload) > 1024 * 1024:
                raise RuntimeError("Companion response exceeded 1 MiB")
            return response.status, json.loads(payload or b"null")
    except urllib.error.HTTPError as error:
        payload = error.read(1024 * 1024)
        try:
            detail = json.loads(payload or b"null")
        except json.JSONDecodeError:
            detail = payload.decode("utf-8", "replace")
        raise RuntimeError(f"HTTP {error.code}: {detail}") from error
    except (urllib.error.URLError, TimeoutError) as error:
        raise RuntimeError(f"Companion request failed: {error}") from error
    except json.JSONDecodeError as error:
        raise RuntimeError("Companion returned malformed JSON") from error


def add_approval_token_options(command):
    command.add_argument("--token-file", help="read approval token from an owner-only file")
    command.add_argument("--token-stdin", action="store_true", help="read approval token from stdin")


def read_approval_token(args, parser):
    token = ""
    sources = int(bool(args.token_file)) + int(bool(args.token_stdin)) + int(bool(os.getenv("AIIMPORT_APPROVAL_TOKEN")))
    if sources > 1:
        parser.error("use only one of --token-file, --token-stdin, or AIIMPORT_APPROVAL_TOKEN")
    if args.token_file:
        flags = os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
        descriptor = os.open(args.token_file, flags)
        info = os.fstat(descriptor)
        mode = stat.S_IMODE(info.st_mode)
        if not stat.S_ISREG(info.st_mode) or mode & 0o077:
            os.close(descriptor)
            parser.error("approval token file must be a regular owner-only file")
        with os.fdopen(descriptor, "r", encoding="utf-8") as stream:
            token = stream.read().strip()
    elif args.token_stdin:
        token = sys.stdin.readline().strip()
    elif os.getenv("AIIMPORT_APPROVAL_TOKEN"):
        token = os.environ["AIIMPORT_APPROVAL_TOKEN"].strip()
    elif sys.stdin.isatty():
        token = getpass.getpass("Approval token: ").strip()
    else:
        parser.error("provide --token-file, --token-stdin, or AIIMPORT_APPROVAL_TOKEN")
    if not token:
        parser.error("approval token is empty")
    return token


def main():
    parser = argparse.ArgumentParser(description="Operate the Lidarr AI Import companion")
    parser.add_argument("--base", default=os.getenv("AIIMPORT_URL", "http://127.0.0.1:8080"))
    parser.add_argument("--api-token", default=os.getenv("COMPANION_API_TOKEN"))
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("safety")
    sub.add_parser("list")
    approve = sub.add_parser("approve")
    approve.add_argument("analysis_id")
    execute = sub.add_parser("execute")
    execute.add_argument("approval_id")
    add_approval_token_options(execute)
    approval_status = sub.add_parser("status")
    approval_status.add_argument("approval_id")
    add_approval_token_options(approval_status)
    reconcile = sub.add_parser("reconcile")
    reconcile.add_argument("approval_id")
    add_approval_token_options(reconcile)
    reconcile.add_argument("--wait", action="store_true")
    reconcile.add_argument("--interval", type=float, default=2.0)
    reconcile.add_argument("--wait-timeout", type=float, default=300.0)
    args = parser.parse_args()
    if not args.api_token:
        parser.error("set COMPANION_API_TOKEN or pass --api-token")

    if args.command == "safety":
        status, result = call(args.base, args.api_token, "GET", "/api/v1/import-safety")
    elif args.command == "list":
        status, result = call(args.base, args.api_token, "GET", "/api/v1/failed-imports")
    elif args.command == "approve":
        status, result = call(args.base, args.api_token, "POST", f"/api/v1/analyses/{args.analysis_id}/approve-import", {})
    elif args.command == "execute":
        token = read_approval_token(args, parser)
        status, result = call(args.base, args.api_token, "POST", f"/api/v1/approvals/{args.approval_id}/execute", {"token": token})
    elif args.command == "status":
        token = read_approval_token(args, parser)
        status, result = call(args.base, args.api_token, "POST", f"/api/v1/approvals/{args.approval_id}/status", {"token": token})
    else:
        token = read_approval_token(args, parser)
        path = f"/api/v1/approvals/{args.approval_id}/reconcile"
        deadline = time.monotonic() + max(args.wait_timeout, 0.0)
        while True:
            status, result = call(args.base, args.api_token, "POST", path, {"token": token})
            if status != 202 or not args.wait:
                break
            if time.monotonic() >= deadline:
                raise RuntimeError("reconciliation wait timed out; query status before taking further action")
            time.sleep(max(args.interval, 0.25))

    json.dump(result, sys.stdout, indent=2, sort_keys=True)
    sys.stdout.write("\n")
    return 0 if status < 400 else 1


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (RuntimeError, OSError, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(1)
