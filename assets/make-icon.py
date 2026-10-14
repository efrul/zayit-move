# The tool's icon: the olive branch of Zayit's logo on a cream tile, with a gold badge of two arrows (moving).
# python make-icon.py LOGO.png  ->  app.ico, app.png and branch.png next to this script
import sys, os
from PIL import Image, ImageDraw, ImageFilter

here = os.path.dirname(os.path.abspath(__file__))
logo = Image.open(sys.argv[1]).convert("RGBA")
branch = logo.crop((915, 0, logo.width, logo.height))
branch = branch.crop(branch.getbbox())
branch.copy().resize((120, int(120 * branch.height / branch.width)), Image.LANCZOS).save(os.path.join(here, "branch.png"))

S = 1024
tile = Image.new("RGBA", (S, S), (0, 0, 0, 0))
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle((24, 24, S - 24, S - 24), radius=220, fill=255)
grad = Image.new("RGBA", (S, S))
gd = ImageDraw.Draw(grad)
for y in range(S):
    t = y / S
    c = tuple(int(a * (1 - t) + b * t) for a, b in zip((252, 248, 236), (240, 226, 190)))
    gd.line((0, y, S, y), fill=c + (255,))
tile.paste(grad, (0, 0), mask)
ImageDraw.Draw(tile).rounded_rectangle((24, 24, S - 24, S - 24), radius=220, outline=(190, 145, 23, 255), width=14)

b = branch.copy()
h = 760
b = b.resize((int(b.width * h / b.height), h), Image.LANCZOS)
tile.alpha_composite(b, ((S - b.width) // 2 + 60, (S - h) // 2 - 40))

# the badge: a gold circle with two arrows, one each way
cx, cy, r = 265, 760, 205
badge = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(badge)
d.ellipse((cx - r - 14, cy - r - 14, cx + r + 14, cy + r + 14), fill=(255, 255, 255, 255))
d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=(190, 145, 23, 255))
w = 34
def arrow(y, left):
    x0, x1 = cx - 120, cx + 120
    d.line((x0 + (40 if left else 0), y, x1 - (0 if left else 40), y), fill="white", width=w)
    if left:
        d.polygon([(x0 - 10, y), (x0 + 80, y - 70), (x0 + 80, y + 70)], fill="white")
    else:
        d.polygon([(x1 + 10, y), (x1 - 80, y - 70), (x1 - 80, y + 70)], fill="white")
arrow(cy - 62, False)
arrow(cy + 62, True)
tile.alpha_composite(badge)

sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
imgs = [tile.resize((s, s), Image.LANCZOS) for s in sizes]
imgs[-1].save(os.path.join(here, "app.ico"), sizes=[(s, s) for s in sizes], append_images=imgs[:-1])
tile.resize((64, 64), Image.LANCZOS).save(os.path.join(here, "app.png"))
tile.resize((256, 256), Image.LANCZOS).save(os.path.join(here, "app-256.png"))
print("ok")
