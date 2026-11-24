"""Convert a square master icon PNG into a clean transparent rounded-square PNG and multi-size Windows .ico."""
from pathlib import Path
import sys
from PIL import Image, ImageDraw

src = Path(sys.argv[1])
out_dir = Path(__file__).resolve().parent / 'Assets'
out_dir.mkdir(parents=True, exist_ok=True)

img = Image.open(src).convert('RGBA')
w, h = img.size
side = min(w, h)
left = (w - side) // 2
top = (h - side) // 2
img = img.crop((left, top, left + side, top + side))

# Trim pure-black outer margin if present so the rounded square fills the icon nicely
bbox = img.convert('L').point(lambda p: 255 if p > 12 else 0).getbbox()
if bbox:
    bx0, by0, bx1, by1 = bbox
    bw, bh = bx1 - bx0, by1 - by0
    if bw > side * 0.55 and bh > side * 0.55:
        cx, cy = (bx0 + bx1) / 2, (by0 + by1) / 2
        half = max(bw, bh) / 2
        pad = half * 0.04
        x0 = max(0, int(cx - half - pad))
        y0 = max(0, int(cy - half - pad))
        x1 = min(side, int(cx + half + pad))
        y1 = min(side, int(cy + half + pad))
        img = img.crop((x0, y0, x1, y1))

# Supersampled antialiased rounded-square mask (Windows 11 squircle feel)
size = 512
img = img.resize((size, size), Image.Resampling.LANCZOS)
mask_hi = Image.new('L', (size * 4, size * 4), 0)
draw = ImageDraw.Draw(mask_hi)
margin = int(size * 4 * 0.04)
radius = int(size * 4 * 0.22)
draw.rounded_rectangle(
    [margin, margin, size * 4 - margin, size * 4 - margin],
    radius=radius,
    fill=255,
)
mask = mask_hi.resize((size, size), Image.Resampling.LANCZOS)
img.putalpha(mask)

png_out = out_dir / 'app.png'
ico_out = out_dir / 'app.ico'
img.resize((256, 256), Image.Resampling.LANCZOS).save(png_out, format='PNG')
img.save(
    ico_out,
    format='ICO',
    sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
)
print('Generated:', png_out, ico_out)
