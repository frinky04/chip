#!/usr/bin/env python3
"""Download the Chip release a game repository is pinned to.

Copy this file into the game repo (for example tools/fetch-chip.py). The pin is a one-line file, `.chip-version`, at
the repo root holding a release tag such as `v0.2.0`. Running the script installs that release into `.chip/` at the
repo root (add `.chip/` to .gitignore), so Chip is at `.chip/Chip.exe`. It does nothing if that version is already there.

    python tools/fetch-chip.py                  # install the pinned version
    python tools/fetch-chip.py --pin latest     # pin the newest release, then install it
    python tools/fetch-chip.py --pin v0.3.0     # pin a given release, then install it
    python tools/fetch-chip.py --sync-addon     # also copy the release's Godot addon into addons/chip_import
    python tools/fetch-chip.py --check          # exit 1 if .chip/ doesn't hold the pinned version

If the Chip repository is private, set GITHUB_TOKEN (or GH_TOKEN), or be logged in with the GitHub CLI (`gh auth login`).
Set CHIP_REPO to download from a fork. Needs Python 3.8+ and nothing else.
"""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

REPO = os.environ.get("CHIP_REPO", "frinky04/chip")
PIN_FILE = ".chip-version"
INSTALL_DIR = ".chip"
ADDON_DIR = Path("addons/chip_import")


def repo_root() -> Path:
    """The git repository holding this script (or the current directory's), else the current directory."""
    for start in (Path(__file__).resolve().parent, Path.cwd()):
        for d in (start, *start.parents):
            if (d / ".git").exists():
                return d
    return Path.cwd()


def token():
    t = os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN")
    if t:
        return t
    try:
        out = subprocess.run(["gh", "auth", "token"], capture_output=True, text=True, timeout=10)
        if out.returncode == 0 and out.stdout.strip():
            return out.stdout.strip()
        return None
    except (OSError, subprocess.SubprocessError):
        return None


def request(url, accept="application/vnd.github+json"):
    headers = {"Accept": accept, "User-Agent": "fetch-chip", "X-GitHub-Api-Version": "2022-11-28"}
    t = token()
    if t:
        headers["Authorization"] = f"Bearer {t}"
    try:
        return urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=60)
    except urllib.error.HTTPError as e:
        if e.code == 404:
            hint = "" if t else " If the repository is private, set GITHUB_TOKEN or run `gh auth login`."
            sys.exit(f"Not found: {url}.{hint}")
        sys.exit(f"GitHub returned {e.code} for {url}: {e.read().decode(errors='replace')[:300]}")
    except urllib.error.URLError as e:
        sys.exit(f"Could not reach GitHub: {e.reason}")


def release(tag):
    path = "latest" if tag == "latest" else f"tags/{tag}"
    with request(f"https://api.github.com/repos/{REPO}/releases/{path}") as r:
        return json.load(r)


def download(asset, dest: Path):
    with request(asset["url"], accept="application/octet-stream") as r, open(dest, "wb") as f:
        shutil.copyfileobj(r, f)


def read_pin(root: Path) -> str:
    pin = root / PIN_FILE
    if not pin.is_file():
        sys.exit(f"No {PIN_FILE} at {root}. Run with --pin latest (or --pin vX.Y.Z) to create it.")
    tag = pin.read_text(encoding="utf-8").strip()
    if not tag:
        sys.exit(f"{pin} is empty.")
    return tag


def installed(install: Path):
    v = install / "VERSION"
    return v.read_text(encoding="utf-8").strip() if v.is_file() else None


def install_release(rel, install: Path):
    tag = rel["tag_name"]
    name = f"chip-{tag}-win-x64.zip"
    assets = {a["name"]: a for a in rel.get("assets", [])}
    if name not in assets:
        sys.exit(f"Release {tag} has no {name} (assets: {', '.join(assets) or 'none'}).")

    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)
        archive = tmp / name
        print(f"Downloading Chip {tag}...")
        download(assets[name], archive)

        if name + ".sha256" in assets:
            download(assets[name + ".sha256"], tmp / "sum")
            expected = (tmp / "sum").read_text(encoding="ascii").split()[0].lower()
            actual = hashlib.sha256(archive.read_bytes()).hexdigest()
            if actual != expected:
                sys.exit(f"Checksum mismatch for {name}: expected {expected}, got {actual}.")
        else:
            print(f"warning: {tag} has no checksum file; skipping verification")

        staged = tmp / "chip"
        with zipfile.ZipFile(archive) as z:
            for member in z.namelist():                  # refuse entries that would land outside the folder
                if member.startswith(("/", "\\")) or ".." in Path(member).parts:
                    sys.exit(f"Refusing unsafe path in {name}: {member}")
            z.extractall(staged)
        (staged / "VERSION").write_text(tag, encoding="ascii")

        if install.exists():
            try:
                shutil.rmtree(install)
            except PermissionError:
                sys.exit(f"Could not replace {install}: close Chip and try again.")
        shutil.move(str(staged), str(install))
    print(f"Installed Chip {tag} to {install / 'Chip.exe'}")


def sync_addon(root: Path, install: Path):
    src = install / "godot-addon" / "chip_import"
    if not src.is_dir():
        sys.exit(f"{install} has no Godot addon to sync.")
    dest = root / ADDON_DIR
    if dest.exists():
        shutil.rmtree(dest)
    shutil.copytree(src, dest)
    print(f"Synced the Godot addon to {dest}")


def main():
    ap = argparse.ArgumentParser(description="Download the Chip release this repository is pinned to.")
    ap.add_argument("--pin", metavar="TAG", help="write TAG (or 'latest') to the pin file, then install it")
    ap.add_argument("--check", action="store_true", help="exit 1 if the installed version differs from the pin")
    ap.add_argument("--force", action="store_true", help="reinstall even if the pinned version is installed")
    ap.add_argument("--sync-addon", action="store_true", help=f"copy the release's Godot addon into {ADDON_DIR.as_posix()}")
    args = ap.parse_args()

    root = repo_root()
    install = root / INSTALL_DIR

    if args.pin:
        rel = release(args.pin)
        (root / PIN_FILE).write_text(rel["tag_name"] + "\n", encoding="utf-8")
        print(f"Pinned Chip {rel['tag_name']} in {root / PIN_FILE}")
        tag = rel["tag_name"]
    else:
        rel = None
        tag = read_pin(root)

    have = installed(install)
    if args.check:
        if have == tag:
            print(f"Chip {tag} is installed.")
            return 0
        print(f"Chip {tag} is pinned, but {'nothing' if have is None else have} is installed. Run {Path(__file__).name}.")
        return 1

    if have == tag and not args.force:
        print(f"Chip {tag} is already installed at {install / 'Chip.exe'}")
    else:
        install_release(rel or release(tag), install)
    if args.sync_addon:
        sync_addon(root, install)
    return 0


if __name__ == "__main__":
    sys.exit(main())
