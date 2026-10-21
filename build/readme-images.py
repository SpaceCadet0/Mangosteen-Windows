#!/usr/bin/env python3
"""Turns the CI's readme-screenshots artifact into docs/images/{en,ru}/*.png for the READMEs.

Each window is taken from the screen capture (name.screen.png: the real window with its title bar) when CI made
one, else from the rendered content; Windows 11's rounded corners are cut out so the desktop behind them does not show.
Screenshots shown side by side are scaled to the same height, so a row has no staircase; the script prints the
<img width> percentages that keep the rendered heights equal (each image's share of the row follows its aspect ratio).

    gh run download <run id> -n readme-screenshots -D <folder>
    python3 build/readme-images.py <folder>
"""
import sys
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
PAIRS = [("annotate", "frame"), ("edit", "compress"), ("trimMedia", "bleep")]
SINGLES = {"welcome": 1440}  # widest size kept, in pixels
ROW = 98  # per cent of the README width a row of two takes (a little room for the gap)
CORNER = 10  # a little more than the 8 px Windows 11 window corner, so no desktop shows in the corners


def window(source: Path, name: str) -> Image.Image:
    screen = source / f"{name}.screen.png"
    if not screen.exists():
        return Image.open(source / f"{name}.png").convert("RGBA")
    # The outer pixel is the window border, lit unevenly by the desktop behind it; it is cut off.
    image = Image.open(screen).convert("RGBA")
    image = image.crop((1, 1, image.width - 1, image.height - 1))
    mask = Image.new("L", image.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, image.width - 1, image.height - 1), CORNER, fill=255)
    image.putalpha(mask)
    return image


def save(image: Image.Image, path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    image.save(path, optimize=True)


def main(folder: Path) -> None:
    for language in ("en", "ru"):
        source = folder / language
        target = ROOT / "docs" / "images" / language
        for left, right in PAIRS:
            images = [window(source, name) for name in (left, right)]
            height = min(image.height for image in images)
            scaled = [image.resize((round(image.width * height / image.height), height), Image.LANCZOS) for image in images]
            total = sum(image.width for image in scaled)
            widths = []
            for name, image in zip((left, right), scaled):
                save(image, target / f"{name}.png")
                widths.append(f"{name} {image.width * ROW / total:.1f}%")
            print(f"{language}: {', '.join(widths)} (height {height}px)")
        for name, widest in SINGLES.items():
            image = window(source, name)
            if image.width > widest:
                image = image.resize((widest, round(image.height * widest / image.width)), Image.LANCZOS)
            save(image, target / f"{name}.png")
            print(f"{language}: {name} {image.width}x{image.height}")


if __name__ == "__main__":
    main(Path(sys.argv[1]))
