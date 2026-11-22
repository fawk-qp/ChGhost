# ChGhost

ChGhost is a Windows x64 WPF chess overlay with board calibration, piece recognition, move tracking, and Stockfish analysis.

## Features

- Transparent, always-on-top WPF overlay. The window uses `WDA_EXCLUDEFROMCAPTURE` to exclude it from screen capture.
- Board calibration by selecting opposite corners, with piece recognition based on 32x32 image patches and Sobel gradient features.
- Move tracking and position synchronization during a game.
- Castling recognition, including the `0-0` and `0-0-0` notations.
- Blitz (180 ms) and Rapid (550 ms) move-time presets.
- Stockfish strength toggle between MAX and 2600 Elo.
- Optional X-Ray piece markers and manual piece calibration.

## Run a GitHub Release build

1. Download and extract `ChGhost_v1.0_Win64.zip` from the project's GitHub Releases page.
2. Make sure `stockfish.exe` is in the same folder as `ChGhost.exe`.
3. Run `ChGhost.exe`.

The application is published as a self-contained Windows x64 single-file executable; a separate .NET runtime installation is not required. Stockfish remains a separate executable and must be next to `ChGhost.exe`.

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
