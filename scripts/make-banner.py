# Draws docs/images/banner.png (the README banner) in the editor's theme (src/Chip.Editor/UI/Theme.cs): the app icon,
# the name in IBM Plex Mono, and a small isometric blockout room in a viewport that fades into the chrome.
# Usage: python scripts/make-banner.py   (needs Pillow)
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "docs/images/banner.png"
S = 3                       # supersampling; the result is saved at 2x for sharp text on high-DPI screens
W, H = 1280, 256            # layout size; every number below is in these 1x pixels
w, h = W * S, H * S

BG = (0x1E, 0x1E, 0x1E)     # Theme.Bg
VIEWPORT = (0x2E, 0x2E, 0x2E)
TEXT = (0xE0, 0xE0, 0xE0)
TEXT_DIM = (0x9C, 0x9C, 0x9C)
BORDER = (0x14, 0x14, 0x14)
GRID = (0x36, 0x36, 0x36)
TILE = (0x50, 0x50, 0x50)
GREY = ((0x4C, 0x4C, 0x4C), (0x3C, 0x3C, 0x3C), (0x33, 0x33, 0x33))    # top, +x side, +z side
WALL = ((0x5A, 0x5A, 0x5A), (0x48, 0x48, 0x48), (0x3E, 0x3E, 0x3E))
ORANGE = ((0xFF, 0xA0, 0x4A), (0xE6, 0x7A, 0x18), (0xB3, 0x5E, 0x12))

MARGIN = 64
ICON = 104
ICON_GAP = 28
NAME_PX = 80
SUB_PX = 26
SUB_GAP = 14                # between the name's baseline area and the subtitle
VIEWPORT_X = 0.50           # where the viewport starts, as a share of the width
SCENE_K = 22               # pixels per metre on the isometric axes

# The room, roughly the dressing sample's, in draw order (back to front): (box, colours).
ROOM = [
    ((0, -0.25, 0, 8, 0, 6), GREY),                     # floor
    ((0, 0, 0, 8, 2.6, 0.3), WALL),                     # walls
    ((0, 0, 0.3, 0.3, 2.6, 6), WALL),
    ((5.5, 0, 0.3, 8, 1.0, 2.2), ORANGE),               # platform
    *[((5.5, 0, 2.2 + s * 0.45, 8, 1.0 - s * 0.25, 2.65 + s * 0.45), GREY) for s in range(4)],   # its stairs
    *[((5.7 + i * 0.55, 1.0, 0.35, 5.76 + i * 0.55, 1.9, 0.41), GREY) for i in range(5)],         # railing
    ((5.55, 1.85, 0.33, 7.95, 1.95, 0.43), GREY),
    ((1.25, 0, 2.5, 3.25, 0.5, 4.5), ORANGE),           # plinth
    ((1.75, 0.5, 3.0, 2.75, 0.9, 4.0), ORANGE),
    ((4.5, 0, 4.0, 5.0, 2.6, 4.5), ORANGE),             # pillar
]

k = SCENE_K * S


def iso(x, y, z):
    return ((x - z) * k * 0.866, (x + z) * k * 0.5 - y * k)


# Centre the room in the viewport.
corners = [iso(x, y, z) for (b, _) in ROOM[:3] for x in (b[0], b[3]) for y in (b[1], b[4]) for z in (b[2], b[5])]
vx0 = int(w * VIEWPORT_X)
ox = vx0 + (w - vx0) * 0.50 - (min(c[0] for c in corners) + max(c[0] for c in corners)) / 2
oy = h / 2 - (min(c[1] for c in corners) + max(c[1] for c in corners)) / 2


def p(x, y, z):
    sx, sy = iso(x, y, z)
    return (ox + sx, oy + sy)


viewport = Image.new("RGB", (w, h), VIEWPORT)
d = ImageDraw.Draw(viewport)
line = S // 2 + 1
for i in range(-16, 17):
    d.line([p(i, 0, -16), p(i, 0, 16)], fill=GRID, width=line)
    d.line([p(-16, 0, i), p(16, 0, i)], fill=GRID, width=line)
d.line([p(-16, 0, 0), p(16, 0, 0)], fill=(0x5A, 0x2A, 0x2C), width=S)   # dimmed x axis
d.line([p(0, 0, -16), p(0, 0, 16)], fill=(0x24, 0x3C, 0x5E), width=S)   # dimmed z axis

for n, ((x0, y0, z0, x1, y1, z1), (top, side_x, side_z)) in enumerate(ROOM):
    faces = (
        ([p(x1, y0, z0), p(x1, y1, z0), p(x1, y1, z1), p(x1, y0, z1)], side_x),
        ([p(x0, y0, z1), p(x1, y0, z1), p(x1, y1, z1), p(x0, y1, z1)], side_z),
        ([p(x0, y1, z0), p(x1, y1, z0), p(x1, y1, z1), p(x0, y1, z1)], top),
    )
    for poly, colour in faces:
        d.polygon(poly, fill=colour, outline=BORDER, width=max(1, S // 2))
    if n == 0:                                          # floor tiles, before anything stands on them
        for i in range(1, 8):
            d.line([p(i, 0, 0), p(i, 0, 6)], fill=TILE, width=line)
        for j in range(1, 6):
            d.line([p(0, 0, j), p(8, 0, j)], fill=TILE, width=line)

# The viewport fades in from the chrome on its left edge.
img = Image.new("RGB", (w, h), BG)
span = int(w * 0.12)
mask = Image.new("L", (w, h), 0)
md = ImageDraw.Draw(mask)
for i in range(w - vx0):
    md.line([vx0 + i, 0, vx0 + i, h], fill=255 if i >= span else int(255 * (i / span) ** 1.4))
img.paste(viewport, (0, 0), mask)
d = ImageDraw.Draw(img)

icon = Image.open(ROOT / "assets/icon/chip-256.png").convert("RGBA").resize((ICON * S, ICON * S), Image.LANCZOS)
ix, iy = MARGIN * S, (h - ICON * S) // 2
img.paste(icon, (ix, iy), icon)

# Name and subtitle as one block, centred on the icon, measured by their ink.
name_font = ImageFont.truetype(str(ROOT / "assets/fonts/IBMPlexMono-SemiBold.ttf"), NAME_PX * S)
sub_font = ImageFont.truetype(str(ROOT / "assets/fonts/IBMPlexMono-Regular.ttf"), SUB_PX * S)
name, sub = "Chip", "level blockout editor"
nb = d.textbbox((0, 0), name, font=name_font)
sb = d.textbbox((0, 0), sub, font=sub_font)
block = (nb[3] - nb[1]) + SUB_GAP * S + (sb[3] - sb[1])    # from the C's top to the subtitle's bottom, past the p
top = h / 2 - block / 2
tx = ix + ICON * S + ICON_GAP * S
d.text((tx - nb[0], top - nb[1]), name, font=name_font, fill=TEXT)
d.text((tx - sb[0] + 3 * S, top + (nb[3] - nb[1]) + SUB_GAP * S - sb[1]), sub, font=sub_font, fill=TEXT_DIM)

OUT.parent.mkdir(parents=True, exist_ok=True)
img.resize((W * 2, H * 2), Image.LANCZOS).save(OUT)
print(f"wrote {OUT}")
