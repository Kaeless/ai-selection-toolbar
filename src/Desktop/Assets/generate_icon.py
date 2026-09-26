"""Generate the Windows icon from the AppIcon.svg geometry (requires Pillow)."""

from pathlib import Path
from PIL import Image, ImageDraw

SIZE = 1024
SCALE = SIZE // 128
OUT = Path(__file__).with_name("AppIcon.ico")

canvas = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
draw = ImageDraw.Draw(canvas)

for y in range(4 * SCALE, 124 * SCALE):
    blend = (y - 4 * SCALE) / (120 * SCALE)
    color = tuple(round(a * (1 - blend) + b * blend) for a, b in
                  zip((25, 55, 93), (16, 38, 64)))
    draw.line((4 * SCALE, y, 124 * SCALE, y), fill=color, width=1)

mask = Image.new("L", (SIZE, SIZE))
ImageDraw.Draw(mask).rounded_rectangle((4 * SCALE, 4 * SCALE, 124 * SCALE,
                                         124 * SCALE), radius=28 * SCALE, fill=255)
canvas.putalpha(mask)
draw = ImageDraw.Draw(canvas)


def stroke(points, color, width):
    points = [(x * SCALE, y * SCALE) for x, y in points]
    draw.line(points, fill=color, width=width * SCALE, joint="curve")
    radius = width * SCALE // 2
    for x, y in (points[0], points[-1]):
        draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=color)


mint = (89, 220, 201, 255)
stroke([(43, 36), (33, 36), (33, 92), (43, 92)], mint, 8)
stroke([(85, 36), (95, 36), (95, 92), (85, 92)], mint, 8)
for y, end in ((51, 78), (65, 73), (79, 67)):
    stroke([(49, y), (end, y)], (255, 255, 255, 255), 7)
draw.polygon([(86 * SCALE, 18 * SCALE), (89 * SCALE, 25 * SCALE),
              (96 * SCALE, 28 * SCALE), (89 * SCALE, 31 * SCALE),
              (86 * SCALE, 38 * SCALE), (83 * SCALE, 31 * SCALE),
              (76 * SCALE, 28 * SCALE), (83 * SCALE, 25 * SCALE)],
             fill=(185, 243, 233, 255))

preview = canvas.resize((256, 256), Image.Resampling.LANCZOS)
preview.save(OUT, format="ICO", sizes=[(16, 16), (24, 24), (32, 32),
                                       (48, 48), (64, 64), (128, 128), (256, 256)])
