#!/usr/bin/env python3
"""Render the raster brand assets: the social card and the installable icons.

Everything here is generated rather than drawn by hand so that it stays in step
with the identity in docs/banner.svg - the same gradient, the same mark, the
same accent - and so that changing the accent is one edit rather than five
exported files nobody can reproduce. Re-run it after touching either and commit
the result.

    python tools/brand-assets/render.py

Writes into src/WorkPlanStudio/wwwroot/:

    og-image.png            1200x630, the og:image and the GitHub preview
    icon-192.png            the manifest's small icon
    icon-512.png            the manifest's large icon
    icon-maskable-512.png   the same mark inside the maskable safe zone
    apple-touch-icon.png    180x180, iOS home screen

The PNGs are committed because nothing in CI produces them, and because a social
card that only exists after a build is a social card that is missing exactly
when a crawler asks for it.

Pillow is the only dependency (`pip install pillow`). Fonts are resolved from a
candidate list so the script produces the same layout on Windows, macOS and a
Linux runner; if none is found it falls back to Pillow's bitmap font and says
so, which is a warning rather than a failure.
"""

from __future__ import annotations

import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

W, H = 1200, 630

# The palette is docs/banner.svg's, so the card, the banner and the running app
# are recognisably the same product.
BG_TOP = (29, 33, 48)
BG_BOTTOM = (19, 21, 31)
MARK_FROM = (79, 70, 229)
MARK_TO = (124, 58, 237)
WHITE = (255, 255, 255)
MUTED = (170, 177, 192)
FAINT = (124, 132, 150)

# The Gantt motif. Indigo for running work, amber for the one late job, and a
# hatched stretch for closed time - the three things the Schedule page shows.
BAR_COLORS = [(99, 102, 241), (139, 92, 246), (45, 212, 191), (251, 191, 36)]

FONT_CANDIDATES = {
    "bold": [
        "C:/Windows/Fonts/segoeuib.ttf",
        "/System/Library/Fonts/Supplemental/Arial Bold.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
        "C:/Windows/Fonts/arialbd.ttf",
    ],
    "regular": [
        "C:/Windows/Fonts/segoeui.ttf",
        "/System/Library/Fonts/Supplemental/Arial.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
        "C:/Windows/Fonts/arial.ttf",
    ],
}


def font(weight: str, size: int) -> ImageFont.ImageFont:
    for path in FONT_CANDIDATES[weight]:
        if Path(path).exists():
            return ImageFont.truetype(path, size)
    print(f"warning: no {weight} TrueType font found; falling back to the bitmap font", file=sys.stderr)
    return ImageFont.load_default()


def vertical_gradient(size: tuple[int, int], top: tuple[int, int, int], bottom: tuple[int, int, int]) -> Image.Image:
    """A one-pixel-wide gradient stretched to size - cheaper than drawing H lines."""
    strip = Image.new("RGB", (1, size[1]))
    pixels = strip.load()
    for y in range(size[1]):
        t = y / max(1, size[1] - 1)
        pixels[0, y] = tuple(round(a + (b - a) * t) for a, b in zip(top, bottom))
    return strip.resize(size, Image.Resampling.BILINEAR)


def rounded_gradient(size: tuple[int, int], radius: int, start, end) -> Image.Image:
    """The WP mark: a diagonal gradient clipped to a rounded square."""
    w, h = size
    tile = Image.new("RGB", size)
    pixels = tile.load()
    for y in range(h):
        for x in range(w):
            t = (x / max(1, w - 1) + y / max(1, h - 1)) / 2
            pixels[x, y] = tuple(round(a + (b - a) * t) for a, b in zip(start, end))
    mask = Image.new("L", size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, w - 1, h - 1), radius=radius, fill=255)
    tile.putalpha(mask)
    return tile


def hatch(draw: ImageDraw.ImageDraw, box: tuple[int, int, int, int], color, step: int = 7) -> None:
    """Diagonal shading, the way the app marks a work centre's closed time."""
    x0, y0, x1, y1 = box
    for x in range(x0 - (y1 - y0), x1, step):
        draw.line([(x, y1), (x + (y1 - y0), y0)], fill=color, width=1)


def draw_gantt(canvas: Image.Image, top: int) -> None:
    """Four lanes of a schedule, faint enough to read as texture rather than data."""
    layer = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)

    x0, x1 = 80, W - 80
    lane_h, gap = 22, 14

    # (start fraction, width fraction, colour index) per lane - a plausible
    # finite-capacity plan rather than random blocks.
    lanes = [
        [(0.00, 0.17, 0), (0.22, 0.13, 1), (0.47, 0.21, 0), (0.74, 0.11, 2)],
        [(0.05, 0.12, 2), (0.26, 0.19, 0), (0.58, 0.14, 1), (0.80, 0.16, 0)],
        [(0.00, 0.09, 1), (0.14, 0.22, 2), (0.44, 0.12, 0), (0.63, 0.24, 1)],
        [(0.09, 0.15, 0), (0.33, 0.11, 1), (0.52, 0.17, 2), (0.78, 0.18, 3)],
    ]
    closed = [(0.35, 0.10), (0.42, 0.14), (0.28, 0.14), (0.22, 0.09)]

    for i, (bars, (cs, cw)) in enumerate(zip(lanes, closed)):
        y0 = top + i * (lane_h + gap)
        y1 = y0 + lane_h
        draw.rounded_rectangle((x0, y0, x1, y1), radius=5, fill=(255, 255, 255, 12))

        span = x1 - x0
        hx0 = x0 + round(cs * span)
        hx1 = hx0 + round(cw * span)
        hatch(draw, (hx0, y0 + 2, hx1, y1 - 2), (255, 255, 255, 26))

        for start, width, colour in bars:
            bx0 = x0 + round(start * span)
            bx1 = bx0 + round(width * span)
            draw.rounded_rectangle((bx0, y0 + 3, bx1, y1 - 3), radius=4, fill=(*BAR_COLORS[colour], 150))

    canvas.alpha_composite(layer)


def pill(draw: ImageDraw.ImageDraw, x: int, y: int, text: str, f: ImageFont.ImageFont) -> int:
    """A tech badge; returns the x where the next one starts."""
    pad_x, pad_y = 18, 9
    w = round(draw.textlength(text, font=f))
    h = f.size + pad_y * 2
    draw.rounded_rectangle((x, y, x + w + pad_x * 2, y + h), radius=h // 2,
                           fill=(255, 255, 255, 14), outline=(255, 255, 255, 40), width=1)
    draw.text((x + pad_x, y + pad_y), text, font=f, fill=(199, 205, 217))
    return x + w + pad_x * 2 + 12


def icon(size: int, *, maskable: bool) -> Image.Image:
    """The WP mark as an app icon.

    Two shapes, because the platforms ask for two different things. An `any`
    icon is shown as drawn, so it rounds its own corners. A `maskable` icon is
    clipped by the platform to whatever shape it likes - a circle on one
    launcher, a squircle on the next - and only the central 80 % is guaranteed
    to survive, so it bleeds to the edge and keeps the mark well inside that.
    Shipping one icon for both is how a logo ends up with its corners shaved off.
    """
    radius = 0 if maskable else round(size * 0.22)
    tile = rounded_gradient((size, size), radius, MARK_FROM, MARK_TO)

    draw = ImageDraw.Draw(tile)
    scale = 0.30 if maskable else 0.38
    draw.text((size / 2, size / 2), "WP", font=font("bold", round(size * scale)),
              fill=WHITE, anchor="mm")
    return tile


def main() -> int:
    wwwroot = Path(__file__).resolve().parents[2] / "src" / "WorkPlanStudio" / "wwwroot"
    out = wwwroot / "og-image.png"

    canvas = vertical_gradient((W, H), BG_TOP, BG_BOTTOM).convert("RGBA")

    draw_gantt(canvas, top=330)

    draw = ImageDraw.Draw(canvas)

    mark = rounded_gradient((112, 112), 26, MARK_FROM, MARK_TO)
    canvas.alpha_composite(mark, (80, 84))
    f_mark = font("bold", 42)
    draw.text((136, 140), "WP", font=f_mark, fill=WHITE, anchor="mm")

    draw.text((224, 94), "WorkPlan Studio", font=font("bold", 62), fill=WHITE)
    draw.text((227, 170), "Finite-capacity production scheduling for manufacturing routings.",
              font=font("regular", 27), fill=MUTED)

    draw.text((80, 244), "The whole application \u2014 relational database included \u2014 runs in the browser.",
              font=font("regular", 27), fill=MUTED)
    draw.text((80, 283), "A branch-and-bound solver says how far from optimal the plan is.",
              font=font("regular", 27), fill=FAINT)

    f_pill = font("regular", 19)
    x = 80
    for label in (".NET 10", "Blazor WebAssembly", "EF Core + SQLite", "WCAG 2.2 AA", "EN / DE"):
        x = pill(draw, x, 470, label, f_pill)

    draw.text((80, 556), "aco993.github.io/WorkPlanStudio", font=font("regular", 21), fill=FAINT)

    out.parent.mkdir(parents=True, exist_ok=True)
    canvas.convert("RGB").save(out, "PNG", optimize=True)
    written = [out]

    # iOS applies its own mask to the home-screen icon and ignores an alpha
    # channel, so it gets the full-bleed shape rather than the rounded one.
    for name, size, maskable in (
        ("icon-192.png", 192, False),
        ("icon-512.png", 512, False),
        ("icon-maskable-512.png", 512, True),
        ("apple-touch-icon.png", 180, True),
    ):
        path = wwwroot / name
        icon(size, maskable=maskable).save(path, "PNG", optimize=True)
        written.append(path)

    for path in written:
        print(f"wrote {path.name} ({path.stat().st_size // 1024} KB)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
