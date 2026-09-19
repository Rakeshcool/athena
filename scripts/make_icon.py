# Generates src/Athena.App/Assets/Athena.ico from the repo-root logo.png.
# A Windows app icon needs multiple sizes: Explorer/the shell scales the 256
# down as needed, but pre-scaled frames look far crisper at 16/32px than
# runtime scaling. Pillow's ICO plugin resizes the source into every listed
# size itself (LANCZOS) and stores large frames PNG-compressed.

from pathlib import Path
from PIL import Image

root = Path(__file__).resolve().parents[1]
src = root / "logo.png"
out = root / "src" / "Athena.App" / "Assets" / "Athena.ico"

sizes = [(s, s) for s in (16, 24, 32, 48, 64, 128, 256)]

img = Image.open(src).convert("RGBA")
if img.width != img.height:
    # The shipped logo is square; guard anyway so a future logo doesn't squash.
    side = min(img.size)
    img = img.crop(((img.width - side) // 2, (img.height - side) // 2,
                    (img.width + side) // 2, (img.height + side) // 2))

out.parent.mkdir(parents=True, exist_ok=True)
img.save(out, format="ICO", sizes=sizes)
print(f"wrote {out} ({out.stat().st_size} bytes)")
