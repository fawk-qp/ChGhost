# ChGhost v1.1

ChGhost is a Windows x64 WPF chess overlay with board calibration, move tracking, and Stockfish analysis.

## Recognition v10

- Calibration is stored in `board_auto_v10.json`.
- Recognition combines RGB Sobel edges, flood-fill background masking, and an adaptive light/dark threshold.
- `LockBits` speeds up pixel access; shift matching tolerates offsets of up to ±1 px.
- King and pawn constraints help stabilize the detected position, including after captures.

## Download and run

Download [`ChGhost_v1.1_Win64.zip`](https://github.com/fawk-ux/ChGhost/releases/download/v1.1/ChGhost_v1.1_Win64.zip) from GitHub Releases and extract it. Keep `stockfish.exe` next to `ChGhost.exe`, then launch `ChGhost.exe`.

The app is self-contained; a separate .NET runtime installation is not required.

## Hotkeys

| Hotkey | Action |
| --- | --- |
| `Alt + 1` | Set the board's upper-left corner (A8 / H1) |
| `Alt + 2` | Set the board's lower-right corner (H1 / A8) |
| `Alt + 3` | Smart action: auto-calibrate the 32 starting pieces, or synchronize the current position mid-game |
| `Alt + 5` | Reset board calibration |
| `Alt + R` | Force a recalculation |
| `Alt + C` | Switch the playing side (White / Black) |
| `Alt + B` | Switch speed preset (Blitz 180 ms / Rapid 550 ms) |
| `Alt + E` | Switch engine strength (MAX / 2600 Elo) |
| `Alt + D` | Toggle X-Ray piece markers |
| `Alt + 4` / `Alt + F` | Select the piece to correct, then manually calibrate the piece under the cursor |
| `Alt + S` | Start / pause |
| `Alt + H` | Hide / show the overlay |
| `Alt + Q` | Exit |
