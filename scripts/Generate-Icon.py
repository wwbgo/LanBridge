"""Generate LanBridge's editable SVG and antialiased Windows icon family.

Usage: python scripts/Generate-Icon.py [--output DIRECTORY]
Requires Pillow (python -m pip install Pillow). No fonts, network or SVG renderer.
Geometry uses a 256-unit canvas: a bridge arch/deck joins two teal network sockets.
All raster frames are drawn independently at 4x their target size before filtering.
"""
import argparse
from pathlib import Path

from PIL import Image, ImageDraw

SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
NAVY = (18, 57, 86)
BLUE = (18, 98, 160)
BRIDGE = (237, 251, 255)
TEAL = (94, 240, 208)
SOCKET = (18, 70, 93)
SVG = '''<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256" role="img" aria-labelledby="title desc">
  <title id="title">LanBridge</title>
  <desc id="desc">A pale bridge links two teal network sockets on a rounded blue tile.</desc>
  <defs>
    <linearGradient id="tile" x1="0" y1="0" x2="1" y2="1" gradientUnits="objectBoundingBox">
      <stop stop-color="#123956"/><stop offset="1" stop-color="#1262a0"/>
    </linearGradient>
  </defs>
  <rect x="8" y="8" width="240" height="240" rx="56" fill="url(#tile)"/>
  <g fill="none" stroke="#edfbff" stroke-linecap="round" stroke-linejoin="round">
    <path d="M 64,144 C 82,66 174,66 192,144" stroke-width="20"/>
    <path d="M 64,144 H 192 M 80,139 V 176 M 176,139 V 176" stroke-width="16"/>
  </g>
  <g fill="#5ef0d0">
    <circle cx="56" cy="144" r="20"/><circle cx="200" cy="144" r="20"/>
  </g>
  <g fill="#12465d">
    <circle cx="56" cy="144" r="7"/><circle cx="200" cy="144" r="7"/>
  </g>
</svg>
'''


def render(size):
    scale = size * 4 / 256
    edge = size * 4
    image = Image.new("RGBA", (edge, edge))
    mask = Image.new("L", (edge, edge))
    ImageDraw.Draw(mask).rounded_rectangle(
        tuple(round(c * scale) for c in (8, 8, 248, 248)), radius=round(56 * scale), fill=255)
    # Same diagonal linear gradient as the editable master, clipped to the tile.
    pixels = image.load()
    for y in range(edge):
        for x in range(edge):
            t = min(1.0, max(0.0, (x / scale + y / scale - 16) / 480))
            pixels[x, y] = tuple(round(a + (b - a) * t) for a, b in zip(NAVY, BLUE)) + (255,)
    image.putalpha(mask)
    draw = ImageDraw.Draw(image)

    def stroke(points, width):
        points = [(round(x * scale), round(y * scale)) for x, y in points]
        thickness = max(1, round(width * scale))
        draw.line(points, fill=BRIDGE, width=thickness, joint="curve")
        r = thickness / 2
        for x, y in (points[0], points[-1]):
            draw.ellipse((round(x - r), round(y - r), round(x + r), round(y + r)), fill=BRIDGE)

    arch = []
    for i in range(97):
        t = i / 96
        u = 1 - t
        arch.append((u**3 * 64 + 3 * u**2 * t * 82 + 3 * u * t**2 * 174 + t**3 * 192,
                     u**3 * 144 + 3 * u**2 * t * 66 + 3 * u * t**2 * 66 + t**3 * 144))
    stroke(arch, 20)
    stroke([(64, 144), (192, 144)], 16)
    stroke([(80, 139), (80, 176)], 16)
    stroke([(176, 139), (176, 176)], 16)
    for x in (56, 200):
        for radius, color in ((20, TEAL), (7, SOCKET)):
            draw.ellipse(tuple(round(v * scale) for v in
                               (x - radius, 144 - radius, x + radius, 144 + radius)), fill=color)
    image = image.resize((size, size), Image.Resampling.LANCZOS)
    # Remove Lanczos ringing outside the silhouette, not a near-transparent square.
    image.putalpha(image.getchannel("A").point(lambda alpha: 0 if alpha <= 4 else alpha))
    return image


def generate(output):
    output.mkdir(parents=True, exist_ok=True)
    frames = [render(size) for size in SIZES]
    frames[-1].save(output / "LanBridge-256.png")
    frames[-1].save(output / "LanBridge.ico", format="ICO", sizes=[(s, s) for s in SIZES],
                    append_images=frames[:-1])
    (output / "LanBridge.svg").write_text(SVG, encoding="utf-8", newline="\n")
    print(f"Generated {output / 'LanBridge.ico'} ({', '.join(map(str, SIZES))} px), PNG256 and SVG")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path,
                        default=Path(__file__).resolve().parents[1] / "src/LanBridge.App/Assets")
    generate(parser.parse_args().output)
