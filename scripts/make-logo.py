# Draws docs/images/logo.png, the README logo: the app icon's orange outline cube (Tabler's "cube" glyph, as in
# scripts/make-icon.ps1) without its dark tile, on a transparent background so it sits on GitHub's light and dark themes.
# Usage: python scripts/make-logo.py   (needs Pillow)
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "docs/images/logo.png"
SIZE = 512                  # saved size; the README shows it at 128 px, so this is sharp up to 4x
S = 4                       # supersampling
ORANGE = (0xFF, 0x8A, 0x1F, 255)

n = SIZE * S
img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# Tabler's cube spans x 4..20 and y 3..21 of its 24-unit box; fit that span, plus the stroke, into the image.
stroke = n * 0.085
scale = (n - stroke * 1.2) / 18.0
ox = n / 2 - 12 * scale
oy = n / 2 - 12 * scale


def p(x, y):
    return (ox + x * scale, oy + y * scale)


def polyline(points):
    pts = [p(*q) for q in points]
    d.line(pts, fill=ORANGE, width=round(stroke), joint="curve")
    r = stroke / 2
    for x, y in pts:                                  # round caps and joins
        d.ellipse([x - r, y - r, x + r, y + r], fill=ORANGE)


polyline([(12, 3), (20, 7.5), (20, 16.5), (12, 21), (4, 16.5), (4, 7.5), (12, 3)])
polyline([(4, 7.5), (12, 12), (20, 7.5)])
polyline([(12, 12), (12, 21)])

OUT.parent.mkdir(parents=True, exist_ok=True)
img.resize((SIZE, SIZE), Image.LANCZOS).save(OUT, optimize=True)
print(f"wrote {OUT}")
