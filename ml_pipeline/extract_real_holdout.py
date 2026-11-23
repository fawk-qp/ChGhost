import argparse
import json
from pathlib import Path

from PIL import Image

CLASS_MAP = {
    "1": (0, "00_empty"),
    "P": (1, "01_wP"), "N": (2, "02_wN"), "B": (3, "03_wB"),
    "R": (4, "04_wR"), "Q": (5, "05_wQ"), "K": (6, "06_wK"),
    "p": (7, "07_bP"), "n": (8, "08_bN"), "b": (9, "09_bB"),
    "r": (10, "10_bR"), "q": (11, "11_bQ"), "k": (12, "12_bK"),
}


def parse_fen_board(fen: str) -> list[list[str]]:
    placement = fen.strip().split()[0]
    rows = placement.split("/")
    if len(rows) != 8:
        raise ValueError(f"Invalid FEN (expected 8 ranks): {fen}")

    grid: list[list[str]] = []
    for row_str in rows:
        row: list[str] = []
        for ch in row_str:
            if ch.isdigit():
                row.extend(["1"] * int(ch))
            elif ch in CLASS_MAP:
                row.append(ch)
            else:
                raise ValueError(f"Unknown FEN symbol '{ch}' in {fen}")
        if len(row) != 8:
            raise ValueError(f"Rank length != 8 in {fen}: {row_str}")
        grid.append(row)
    return grid


def extract_board_crops(
    image_path: Path,
    fen: str,
    out_dir: Path,
    play_as_black: bool = False,
    bbox: tuple[int, int, int, int] | None = None,
    prefix: str = "board",
) -> int:
    grid = parse_fen_board(fen)
    img = Image.open(image_path).convert("RGB")
    if bbox is not None:
        img = img.crop(bbox)

    board_512 = img.resize((512, 512), Image.Resampling.BILINEAR)

    saved = 0
    for sr in range(8):
        for sc in range(8):
            br = 7 - sr if play_as_black else sr
            bc = 7 - sc if play_as_black else sc
            piece = grid[br][bc]
            _, folder = CLASS_MAP[piece]
            target_dir = out_dir / folder
            target_dir.mkdir(parents=True, exist_ok=True)

            tile = board_512.crop((sc * 64, sr * 64, (sc + 1) * 64, (sr + 1) * 64))
            tile.save(target_dir / f"{prefix}_r{br}c{bc}.png")
            saved += 1

    return saved


def main() -> None:
    parser = argparse.ArgumentParser(description="Slice real chess screenshots by FEN into holdout crops")
    parser.add_argument("--manifest", type=Path, help="JSON list: [{image, fen, play_as_black, bbox, tag}]")
    parser.add_argument("--image", type=Path, help="Single board screenshot path")
    parser.add_argument("--fen", type=str, help="FEN string for --image")
    parser.add_argument("--black", action="store_true", help="Board is flipped (Black at bottom)")
    parser.add_argument("--bbox", type=str, default=None, help="Optional crop x1,y1,x2,y2")
    parser.add_argument("--out-dir", type=Path, default=Path("data/real_holdout/crops"))
    args = parser.parse_args()

    total = 0
    if args.manifest:
        entries = json.loads(args.manifest.read_text(encoding="utf-8"))
        base = args.manifest.parent
        for i, item in enumerate(entries):
            img_path = Path(item["image"])
            if not img_path.is_absolute():
                img_path = base / img_path
            bbox = tuple(item["bbox"]) if item.get("bbox") else None
            tag = item.get("tag", f"m{i:03d}")
            total += extract_board_crops(
                img_path, item["fen"], args.out_dir,
                play_as_black=bool(item.get("play_as_black", False)),
                bbox=bbox,
                prefix=tag,
            )
    elif args.image and args.fen:
        bbox = tuple(int(v) for v in args.bbox.split(",")) if args.bbox else None
        total = extract_board_crops(
            args.image, args.fen, args.out_dir,
            play_as_black=args.black, bbox=bbox, prefix=args.image.stem
        )
    else:
        raise SystemExit("Provide either --manifest <file.json> or both --image and --fen.")

    print(f"Extracted {total} real holdout crops into {args.out_dir}")


if __name__ == "__main__":
    main()
