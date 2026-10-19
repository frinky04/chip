"""Scrape the Scythe Editor guide (VitePress) into Markdown.

Usage: python scripts/scrape-scythe-docs.py [out_dir]
Requires pandoc on PATH. Pages are discovered from the site's __VP_HASH_MAP__.
"""
import json
import re
import subprocess
import sys
import urllib.request
from html.parser import HTMLParser
from pathlib import Path

BASE = "https://scytheeditor.com/guide/"
OUT = Path(sys.argv[1] if len(sys.argv) > 1 else "docs/reference/scythe")


def fetch(url):
    req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
    with urllib.request.urlopen(req) as r:
        return r.read().decode("utf-8")


class DocExtractor(HTMLParser):
    """Collects the raw HTML of the first div whose class includes 'vp-doc'."""

    VOID = {"area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"}

    def __init__(self):
        super().__init__(convert_charrefs=False)
        self.depth = 0
        self.parts = []
        self.done = False

    def handle_starttag(self, tag, attrs):
        if self.done:
            return
        if self.depth == 0:
            cls = dict(attrs).get("class") or ""
            if tag == "div" and "vp-doc" in cls.split():
                self.depth = 1
                self.parts.append(self.get_starttag_text())
            return
        self.parts.append(self.get_starttag_text())
        if tag not in self.VOID:
            self.depth += 1

    def handle_endtag(self, tag):
        if self.done or self.depth == 0 or tag in self.VOID:
            return
        self.parts.append(f"</{tag}>")
        self.depth -= 1
        if self.depth == 0:
            self.done = True

    def handle_data(self, data):
        if self.depth:
            self.parts.append(data)

    def handle_entityref(self, name):
        if self.depth:
            self.parts.append(f"&{name};")

    def handle_charref(self, name):
        if self.depth:
            self.parts.append(f"&#{name};")


def to_markdown(html):
    # Drop VitePress header anchors ("#" permalinks) before converting.
    html = re.sub(r'<a class="header-anchor"[^>]*>.*?</a>', "", html, flags=re.S)
    # Hotkey widgets (icon + label + <kbd>) -> "**Label** (`Keys`)".
    html = re.sub(r'<img[^>]*class="hotkey-icon"[^>]*>', "", html)
    html = re.sub(
        r'<span class="hotkey"[^>]*>\s*<span class="hotkey-label"[^>]*>(.*?)</span>\s*<kbd[^>]*>(.*?)</kbd>\s*</span>',
        r"<strong>\1</strong> (<code>\2</code>)", html, flags=re.S,
    )
    # Make site-relative links/images absolute.
    html = re.sub(r'(href|src)="/', r'\1="https://scytheeditor.com/', html)
    return subprocess.run(
        ["pandoc", "-f", "html", "-t", "gfm-raw_html", "--wrap=none"],
        input=html, capture_output=True, text=True, encoding="utf-8", check=True,
    ).stdout


def main():
    index = fetch(BASE)
    hash_map = json.loads(json.loads('"' + re.search(r'__VP_HASH_MAP__=JSON\.parse\("(.*?)"\)', index).group(1) + '"'))
    OUT.mkdir(parents=True, exist_ok=True)
    for key in sorted(hash_map):
        rel = key[:-3].replace("_", "/")  # "getting-started_hotkeys.md" -> "getting-started/hotkeys"
        url = BASE if rel == "index" else f"{BASE}{rel}.html"
        page = DocExtractor()
        page.feed(fetch(url))
        if not page.parts:
            print(f"skip (no content): {url}")
            continue
        dest = OUT / f"{rel}.md"
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_text(f"<!-- Source: {url} -->\n\n" + to_markdown("".join(page.parts)), encoding="utf-8")
        print(f"{url} -> {dest}")


if __name__ == "__main__":
    main()
