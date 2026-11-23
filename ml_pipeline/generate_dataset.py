import argparse
import io
import random
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import fitz
import numpy as np
from PIL import Image, ImageDraw, ImageEnhance, ImageFilter, ImageFont
from tqdm import tqdm

CLASSES = [
    ("empty", "1"),
    ("wP", "P"), ("wN", "N"), ("wB", "B"), ("wR", "R"), ("wQ", "Q"), ("wK", "K"),
    ("bP", "p"), ("bN", "n"), ("bB", "b"), ("bR", "r"), ("bQ", "q"), ("bK", "k"),
]
PIECE_CODES = [c[0] for c in CLASSES[1:]]

LICHESS_SETS = [
    "cburnett", "merida", "alpha", "staunty", "california", "maestro",
    "cardinal", "companion", "gioco", "icpieces", "kosal", "leipzig",
    "mpchess", "pirouetti", "reillycraig", "tatiana",
]

BOARD_THEMES = [
    ((238, 238, 210), (118, 150, 86)),
    ((240, 217, 181), (181, 136, 99)),
    ((222, 227, 230), (140, 162, 173)),
    ((234, 233, 210), (75, 115, 153)),
    ((235, 236, 208), (115, 149, 82)),
    ((255, 255, 221), (134, 166, 102)),
    ((240, 241, 240), (196, 216, 228)),
    ((217, 228, 232), (123, 157, 178)),
    ((232, 235, 239), (125, 135, 150)),
    ((245, 245, 245), (168, 168, 168)),
    ((227, 193, 111), (184, 139, 74)),
    ((240, 218, 181), (169, 122, 87)),
    ((238, 238, 238), (105, 146, 62)),
    ((243, 243, 243), (119, 153, 84)),
    ((230, 213, 184), (138, 95, 64)),
    ((180, 180, 180), (92, 92, 92)),
    ((120, 132, 148), (64, 73, 86)),
    ((224, 208, 232), (136, 104, 156)),
    ((245, 236, 215), (194, 142, 92)),
    ((235, 220, 190), (150, 105, 75)),
]

HIGHLIGHT_COLORS = [
    (246, 246, 105, 155),
    (205, 210, 106, 160),
    (186, 202, 43, 145),
    (155, 199, 0, 110),
    (96, 175, 255, 110),
    (255, 170, 50, 115),
]


def download_and_render_set(set_name: str, out_dir: Path, render_size: int = 128) -> int:
    set_dir = out_dir / set_name
    set_dir.mkdir(parents=True, exist_ok=True)
    ok_count = 0

    for code in PIECE_CODES:
        png_path = set_dir / f"{code}.png"
        if png_path.exists():
            ok_count += 1
            continue

        url = f"https://raw.githubusercontent.com/lichess-org/lila/master/public/piece/{set_name}/{code}.svg"
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "ChGhost-DatasetGen/1.0"})
            with urllib.request.urlopen(req, timeout=15) as resp:
                svg_bytes = resp.read()

            doc = fitz.open(stream=svg_bytes, filetype="svg")
            page = doc[0]
            rect = page.rect
            scale = (render_size * 0.94) / max(rect.width, rect.height, 1.0)
            pix = page.get_pixmap(matrix=fitz.Matrix(scale, scale), alpha=True)
            img = Image.frombytes("RGBA", (pix.width, pix.height), pix.samples)

            canvas = Image.new("RGBA", (render_size, render_size), (0, 0, 0, 0))
            ox = (render_size - img.width) // 2
            oy = (render_size - img.height) // 2
            canvas.paste(img, (ox, oy), img)
            canvas.save(png_path)
            doc.close()
            ok_count += 1
        except Exception as exc:
            print(f"[WARN] Skip {set_name}/{code}: {exc}")

    return ok_count


def load_piece_sets(assets_dir: Path, render_size: int = 128) -> dict[str, dict[str, Image.Image]]:
    rendered_dir = assets_dir / "rendered"
    custom_dir = assets_dir / "custom"
    rendered_dir.mkdir(parents=True, exist_ok=True)
    custom_dir.mkdir(parents=True, exist_ok=True)

    print("Checking / downloading open-source piece sets...")
    with ThreadPoolExecutor(max_workers=6) as pool:
        futures = {pool.submit(download_and_render_set, s, rendered_dir, render_size): s for s in LICHESS_SETS}
        for fut in tqdm(futures, desc="Piece sets"):
            fut.result()

    sets: dict[str, dict[str, Image.Image]] = {}
    for root in (rendered_dir, custom_dir):
        for set_path in sorted(root.iterdir()):
            if not set_path.is_dir():
                continue
            pieces = {}
            for code in PIECE_CODES:
                p = set_path / f"{code}.png"
                if p.exists():
                    img = Image.open(p).convert("RGBA")
                    if img.size != (render_size, render_size):
                        img = img.resize((render_size, render_size), Image.Resampling.LANCZOS)
                    pieces[code] = img
            if len(pieces) == 12:
                sets[f"{root.name}/{set_path.name}"] = pieces

    if not sets:
        raise RuntimeError("No complete 12-piece sets available. Check internet connection or assets/custom/.")

    print(f"Loaded {len(sets)} complete piece sets.")
    return sets


def jitter_color(rgb: tuple[int, int, int], rng: random.Random, delta: int = 14) -> tuple[int, int, int]:
    return (
        max(0, min(255, rgb[0] + rng.randint(-delta, delta))),
        max(0, min(255, rgb[1] + rng.randint(-delta, delta))),
        max(0, min(255, rgb[2] + rng.randint(-delta, delta))),
    )


def load_fonts() -> list[ImageFont.FreeTypeFont | ImageFont.ImageFont]:
    candidates = [
        "C:/Windows/Fonts/segoeui.ttf",
        "C:/Windows/Fonts/segoeuib.ttf",
        "C:/Windows/Fonts/arial.ttf",
        "C:/Windows/Fonts/arialbd.ttf",
        "C:/Windows/Fonts/consola.ttf",
    ]
    fonts = []
    for path in candidates:
        if Path(path).exists():
            for size in (12, 14, 16, 18):
                try:
                    fonts.append(ImageFont.truetype(path, size))
                except OSError:
                    pass
    if not fonts:
        fonts.append(ImageFont.load_default())
    return fonts


def blend_rect(base: Image.Image, box: tuple[int, int, int, int], rgba: tuple[int, int, int, int]) -> None:
    overlay = Image.new("RGBA", base.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(overlay)
    draw.rectangle(box, fill=rgba)
    base.alpha_composite(overlay)


def draw_check_glow(base: Image.Image, cell_x: int, cell_y: int, cell_size: int) -> None:
    arr = np.zeros((cell_size, cell_size, 4), dtype=np.uint8)
    yy, xx = np.mgrid[0:cell_size, 0:cell_size]
    cx = cell_size / 2.0
    dist = np.sqrt((xx - cx) ** 2 + (yy - cx) ** 2) / (cell_size * 0.55)
    alpha = np.clip((1.0 - dist) * 210.0, 0, 210).astype(np.uint8)
    arr[..., 0] = 235
    arr[..., 1] = 30
    arr[..., 2] = 30
    arr[..., 3] = alpha
    glow = Image.fromarray(arr, mode="RGBA")
    base.alpha_composite(glow, (cell_x, cell_y))


def render_sample(
    class_idx: int,
    piece_sets: list[dict[str, Image.Image]],
    fonts: list[ImageFont.FreeTypeFont | ImageFont.ImageFont],
    rng: random.Random,
    out_size: int = 64,
) -> np.ndarray:
    cell_size = 96
    canvas_size = cell_size * 3
    light_base, dark_base = rng.choice(BOARD_THEMES)
    light_col = jitter_color(light_base, rng, 12)
    dark_col = jitter_color(dark_base, rng, 12)
    parity_offset = rng.randint(0, 1)

    board = Image.new("RGBA", (canvas_size, canvas_size), (0, 0, 0, 255))
    draw = ImageDraw.Draw(board)

    for gy in range(3):
        for gx in range(3):
            col = light_col if ((gx + gy + parity_offset) % 2 == 0) else dark_col
            x0, y0 = gx * cell_size, gy * cell_size
            draw.rectangle((x0, y0, x0 + cell_size - 1, y0 + cell_size - 1), fill=col + (255,))

    if rng.random() < 0.28:
        noise = np.random.default_rng(rng.randint(0, 2**31 - 1)).integers(-5, 6, size=(canvas_size, canvas_size, 3))
        arr = np.array(board, dtype=np.int16)
        arr[..., :3] = np.clip(arr[..., :3] + noise, 0, 255)
        board = Image.fromarray(arr.astype(np.uint8), mode="RGBA")

    cx0, cy0 = cell_size, cell_size

    if rng.random() < 0.32:
        hl = rng.choice(HIGHLIGHT_COLORS)
        blend_rect(board, (cx0, cy0, cx0 + cell_size - 1, cy0 + cell_size - 1), hl)

    if rng.random() < 0.18:
        nx, ny = rng.choice([(0, 1), (2, 1), (1, 0), (1, 2)])
        hl = rng.choice(HIGHLIGHT_COLORS)
        blend_rect(board, (nx * cell_size, ny * cell_size, (nx + 1) * cell_size - 1, (ny + 1) * cell_size - 1), hl)

    is_king = class_idx in (6, 12)
    if (is_king and rng.random() < 0.25) or (not is_king and class_idx > 0 and rng.random() < 0.02):
        draw_check_glow(board, cx0, cy0, cell_size)

    if rng.random() < 0.28:
        font = rng.choice(fonts)
        center_is_light = ((1 + 1 + parity_offset) % 2 == 0)
        coord_col = dark_col if center_is_light else light_col
        draw_coord = ImageDraw.Draw(board)
        if rng.random() < 0.5:
            ch = rng.choice("12345678")
            pos = (cx0 + rng.randint(3, 7), cy0 + rng.randint(2, 6))
        else:
            ch = rng.choice("abcdefgh")
            pos = (cx0 + cell_size - rng.randint(15, 22), cy0 + cell_size - rng.randint(18, 24))
        draw_coord.text(pos, ch, fill=coord_col + (220,), font=font)

    pset = rng.choice(piece_sets)

    for gy, gx in ((0, 1), (2, 1), (1, 0), (1, 2)):
        if rng.random() < 0.35:
            ncode = rng.choice(PIECE_CODES)
            nimg = pset[ncode].resize((int(cell_size * 0.92), int(cell_size * 0.92)), Image.Resampling.BILINEAR)
            nx0 = gx * cell_size + (cell_size - nimg.width) // 2
            ny0 = gy * cell_size + (cell_size - nimg.height) // 2
            board.alpha_composite(nimg, (nx0, ny0))

    if class_idx == 0 and rng.random() < 0.25:
        dot_overlay = Image.new("RGBA", board.size, (0, 0, 0, 0))
        dd = ImageDraw.Draw(dot_overlay)
        rad = rng.randint(10, 16)
        mx, my = cx0 + cell_size // 2, cy0 + cell_size // 2
        dot_col = rng.choice([(0, 0, 0, 55), (20, 85, 30, 95), (60, 60, 60, 75)])
        dd.ellipse((mx - rad, my - rad, mx + rad, my + rad), fill=dot_col)
        board.alpha_composite(dot_overlay)
    elif class_idx > 0:
        code = CLASSES[class_idx][0]
        piece_img = pset[code]
        scale = rng.uniform(0.84, 1.02)
        pw = max(16, int(cell_size * scale))
        ph = max(16, int(cell_size * scale))
        resized = piece_img.resize((pw, ph), Image.Resampling.LANCZOS)

        if rng.random() < 0.4:
            rgb_part = resized.convert("RGB")
            alpha_part = resized.split()[3]
            rgb_part = ImageEnhance.Brightness(rgb_part).enhance(rng.uniform(0.88, 1.08))
            rgb_part = ImageEnhance.Contrast(rgb_part).enhance(rng.uniform(0.88, 1.12))
            resized = Image.merge("RGBA", (*rgb_part.split(), alpha_part))

        px = cx0 + (cell_size - pw) // 2 + rng.randint(-3, 3)
        py = cy0 + (cell_size - ph) // 2 + rng.randint(-3, 3)

        if rng.random() < 0.14:
            ring_ov = Image.new("RGBA", board.size, (0, 0, 0, 0))
            rd = ImageDraw.Draw(ring_ov)
            pad = rng.randint(3, 7)
            rd.ellipse(
                (cx0 + pad, cy0 + pad, cx0 + cell_size - pad, cy0 + cell_size - pad),
                outline=rng.choice([(0, 0, 0, 65), (20, 85, 30, 100)]),
                width=rng.randint(5, 9),
            )
            board.alpha_composite(ring_ov)

        board.alpha_composite(resized, (px, py))

    if rng.random() < 0.08:
        arr_ov = Image.new("RGBA", board.size, (0, 0, 0, 0))
        ad = ImageDraw.Draw(arr_ov)
        p1 = (cx0 + rng.randint(-20, cell_size + 20), cy0 + rng.randint(-20, cell_size + 20))
        p2 = (cx0 + rng.randint(-20, cell_size + 20), cy0 + rng.randint(-20, cell_size + 20))
        acol = rng.choice([(255, 170, 0, 110), (0, 180, 90, 100), (220, 60, 60, 100)])
        ad.line((p1, p2), fill=acol, width=rng.randint(7, 13))
        board.alpha_composite(arr_ov)

    zoom = rng.uniform(0.92, 1.08)
    crop_span = cell_size * zoom
    jx = rng.uniform(-0.055, 0.055) * cell_size
    jy = rng.uniform(-0.055, 0.055) * cell_size
    center_x = cx0 + cell_size / 2.0 + jx
    center_y = cy0 + cell_size / 2.0 + jy
    crop_box = (
        center_x - crop_span / 2.0,
        center_y - crop_span / 2.0,
        center_x + crop_span / 2.0,
        center_y + crop_span / 2.0,
    )

    crop = board.convert("RGB").crop(crop_box)

    if rng.random() < 0.35:
        mid_sz = rng.choice([36, 44, 52, 76, 88])
        crop = crop.resize((mid_sz, mid_sz), Image.Resampling.BILINEAR)

    crop = crop.resize((out_size, out_size), Image.Resampling.BILINEAR)

    if rng.random() < 0.18:
        crop = crop.filter(ImageFilter.GaussianBlur(radius=rng.uniform(0.25, 0.65)))
    if rng.random() < 0.25:
        buf = io.BytesIO()
        crop.save(buf, format="JPEG", quality=rng.randint(72, 95))
        buf.seek(0)
        crop = Image.open(buf).convert("RGB")
    if rng.random() < 0.30:
        crop = ImageEnhance.Brightness(crop).enhance(rng.uniform(0.88, 1.12))
        crop = ImageEnhance.Contrast(crop).enhance(rng.uniform(0.88, 1.12))
        crop = ImageEnhance.Color(crop).enhance(rng.uniform(0.85, 1.15))

    return np.asarray(crop, dtype=np.uint8)


def generate_split(
    out_dir: Path,
    split_name: str,
    samples_per_piece: int,
    empty_multiplier: float,
    piece_sets: list[dict[str, Image.Image]],
    fonts: list[ImageFont.FreeTypeFont | ImageFont.ImageFont],
    seed: int,
    out_size: int = 64,
) -> None:
    rng = random.Random(seed)
    labels_plan: list[int] = [0] * int(samples_per_piece * empty_multiplier)
    for cls_idx in range(1, 13):
        labels_plan.extend([cls_idx] * samples_per_piece)
    rng.shuffle(labels_plan)

    total = len(labels_plan)
    images = np.empty((total, out_size, out_size, 3), dtype=np.uint8)
    labels = np.array(labels_plan, dtype=np.uint8)

    for i, cls_idx in enumerate(tqdm(labels_plan, desc=f"Generating {split_name}")):
        images[i] = render_sample(cls_idx, piece_sets, fonts, rng, out_size=out_size)

    np.save(out_dir / f"{split_name}_images.npy", images)
    np.save(out_dir / f"{split_name}_labels.npy", labels)

    if split_name == "val":
        cols, rows = 13, 4
        grid = Image.new("RGB", (cols * out_size, rows * out_size))
        for cls_idx in range(13):
            idxs = np.where(labels == cls_idx)[0][:rows]
            for r_i, sample_i in enumerate(idxs):
                grid.paste(Image.fromarray(images[sample_i]), (cls_idx * out_size, r_i * out_size))
        grid.save(out_dir / "preview_samples.png")


def main() -> None:
    parser = argparse.ArgumentParser(description="Synthetic chess square dataset generator for ChGhost v2.0-cv")
    parser.add_argument("--assets-dir", type=Path, default=Path("assets"))
    parser.add_argument("--out-dir", type=Path, default=Path("data/synth"))
    parser.add_argument("--train-per-piece", type=int, default=4200, help="Samples per piece class (~63k total)")
    parser.add_argument("--val-per-piece", type=int, default=450, help="Validation samples per piece class")
    parser.add_argument("--empty-mult", type=float, default=3.0, help="Empty class multiplier vs single piece class")
    parser.add_argument("--seed", type=int, default=42)
    args = parser.parse_args()

    args.out_dir.mkdir(parents=True, exist_ok=True)
    piece_sets_dict = load_piece_sets(args.assets_dir)
    piece_sets = list(piece_sets_dict.values())
    fonts = load_fonts()

    generate_split(args.out_dir, "train", args.train_per_piece, args.empty_mult, piece_sets, fonts, args.seed)
    generate_split(args.out_dir, "val", args.val_per_piece, args.empty_mult, piece_sets, fonts, args.seed + 1000)
    print(f"Done! Check visual sanity in {args.out_dir / 'preview_samples.png'}")


if __name__ == "__main__":
    main()
