#!/usr/bin/env /usr/bin/python3
"""Render numbered interaction callouts over the raw SyncFactors UI screenshots."""

from __future__ import annotations

from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent
RAW_DIR = ROOT / "images" / "raw"
OUTPUT_DIR = ROOT / "images" / "annotated"

# Coordinates are x, y, width, height in each raw screenshot.
CALLOUTS = {
    "00-login.png": [
        (487, 332, 225, 45),
        (728, 332, 225, 45),
        (487, 392, 225, 38),
    ],
    "01-dashboard.png": [
        (308, 130, 155, 27),
        (75, 175, 388, 210),
        (75, 402, 388, 133),
        (508, 102, 884, 798),
    ],
    "02-navigation-tools.png": [(1058, 26, 112, 150)],
    "03-navigation-admin.png": [(1141, 26, 110, 180)],
    "04-account-menu.png": [(1208, 26, 176, 218)],
    "05-sync.png": [
        (481, 691, 382, 41),
        (481, 746, 382, 38),
        (935, 898, 382, 187),
    ],
    "06-exceptions.png": [
        (48, 278, 1344, 102),
        (75, 542, 421, 41),
        (510, 538, 421, 45),
        (944, 545, 421, 38),
    ],
    "07-worker-360.png": [
        (75, 332, 480, 45),
        (569, 343, 796, 37),
        (75, 390, 480, 38),
    ],
    "08-lookup.png": [
        (75, 332, 560, 45),
        (649, 338, 716, 38),
    ],
    "09-admin-users.png": [
        (48, 379, 862, 419),
        (486, 575, 397, 41),
        (75, 630, 397, 38),
    ],
    "09b-admin-local-users.png": [(0, 39, 1445, 65)],
    "10-admin-deletions.png": [
        (75, 396, 638, 45),
        (727, 355, 638, 38),
        (48, 466, 1344, 221),
        (48, 687, 1344, 213),
    ],
    "11-admin-config.png": [
        (48, 360, 1344, 62),
        (48, 440, 1344, 460),
    ],
    "11b-admin-config-mappings.png": [(27, 240, 1290, 1100)],
}


def load_number_font(size: int) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    candidates = [
        "/System/Library/Fonts/SFNS.ttf",
        "/System/Library/Fonts/Helvetica.ttc",
        "/Library/Fonts/Arial.ttf",
    ]
    for candidate in candidates:
        try:
            return ImageFont.truetype(candidate, size=size)
        except OSError:
            continue
    return ImageFont.load_default()


def render_callouts(source: Path, destination: Path, boxes: list[tuple[int, int, int, int]]) -> None:
    image = Image.open(source).convert("RGBA")
    overlay = Image.new("RGBA", image.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(overlay)
    font = load_number_font(24)
    accent = (255, 92, 55, 255)
    accent_fill = (255, 92, 55, 34)
    white = (255, 255, 255, 255)

    for index, (x, y, width, height) in enumerate(boxes, start=1):
        x1 = max(2, x - 5)
        y1 = max(2, y - 5)
        x2 = min(image.width - 3, x + width + 5)
        y2 = min(image.height - 3, y + height + 5)
        draw.rounded_rectangle((x1, y1, x2, y2), radius=10, fill=accent_fill, outline=accent, width=5)

        radius = 18
        cx = min(max(x1 + 4, radius + 2), image.width - radius - 2)
        cy = min(max(y1 + 4, radius + 2), image.height - radius - 2)
        draw.ellipse((cx - radius, cy - radius, cx + radius, cy + radius), fill=accent, outline=white, width=2)
        text = str(index)
        bbox = draw.textbbox((0, 0), text, font=font)
        text_width = bbox[2] - bbox[0]
        text_height = bbox[3] - bbox[1]
        draw.text((cx - text_width / 2, cy - text_height / 2 - 2), text, fill=white, font=font)

    destination.parent.mkdir(parents=True, exist_ok=True)
    Image.alpha_composite(image, overlay).convert("RGB").save(destination, quality=94)


def main() -> None:
    missing = [name for name in CALLOUTS if not (RAW_DIR / name).exists()]
    if missing:
        raise SystemExit(f"Missing raw screenshots: {', '.join(missing)}")

    for name, boxes in CALLOUTS.items():
        render_callouts(RAW_DIR / name, OUTPUT_DIR / name, boxes)
        print(OUTPUT_DIR / name)


if __name__ == "__main__":
    main()
