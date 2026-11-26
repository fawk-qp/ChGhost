# ChGhost v2.2.1

ChGhost is a Windows x64 WPF chess overlay trainer. It captures the board region, recognizes pieces with ONNX computer vision, validates the resulting FEN, and queries Stockfish over UCI for position analysis, lesson plans, and alternative ideas.

## Features

- **ONNX vision**: `TinyChessPieceNet` 48x48 (~0.46 MB) with Dirty-Square Gating. Tile size is read from the model input, so `pieces_64x64.onnx` remains a safe fallback.
- **Board detection**: `BoardPresenceChecker` occlusion guard, `AutoBoardDetector`, and live `playAsBlack` auto-detection using pawn/weighted-row rules with cache reprojection (no extra ONNX pass).
- **Engine**: asynchronous Stockfish UCI in a Job Object, MultiPV candidate collection, and idle auto-unload.
- **Overlay trainer**: `PerMonitorV2` DPI, click-through layered window, `WDA_EXCLUDEFROMCAPTURE`, `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, hidden from the taskbar.
- **Coach/advisor mode**: `StealthHumanize` (`Alt+H`) selects a candidate move via `HumanMoveSelector` (MultiPV, temperature, max CP loss), contrast advisor arrows, and adaptive pacing described as a pause for independent thinking before the hint arrow appears.

## Hotkeys

| Hotkey | Action |
| --- | --- |
| `Alt + 1` | Set board upper-left corner (A8 / H1) |
| `Alt + 2` | Set board lower-right corner (H1 / A8) |
| `Alt + 3` | Smart action: auto-calibrate start position or sync current position |
| `Alt + 4` | Manual piece fix at cursor (Template recognizer; ONNX uses `Alt + F`) |
| `Alt + 5` | Reset all calibration |
| `Alt + F` | Auto-detect board / manual piece fix depending on recognizer mode |
| `Alt + S` | Toggle engine start/pause |
| `Alt + R` | Force rescan (reset move tracking) |
| `Alt + C` | Toggle side (play as Black) |
| `Alt + H` | Toggle Coach / Advisor Mode |
| `Alt + B` | Blitz / Rapid preset |
| `Alt + E` | Toggle full engine strength / target Elo |
| `Alt + P` | Switch profile 1-3 |
| `Alt + G` | Hide / show overlay |
| `Alt + D` | Toggle debug overlay |
| `Alt + Q` | Quit |

## Key `settings.json` parameters

| Parameter | Default | Description |
| --- | --- | --- |
| `RecognizerMode` | `Auto` | Template / ONNX / auto recognizer selection |
| `OnnxModelPath` | `models/pieces_48x48_tiny.onnx` | ONNX piece model path |
| `EngineExecutablePath` | `stockfish.exe` | Stockfish UCI executable |
| `EngineThreads` | `4` | Stockfish threads |
| `EngineHashMb` | `64` | Stockfish hash size |
| `TargetElo` / `FullStrength` | `2600` / `true` | Engine strength limiting |
| `MoveTimeBlitzMs` / `MoveTimeRapidMs` | `180` / `550` | Engine move time per preset |
| `ScanIntervalBlitzMs` / `ScanIntervalRapidMs` | `120` / `450` | Board scan interval per preset |
| `CommandTimeoutMs` | `3500` | UCI command timeout |
| `ExcludeFromCapture` | `true` | Hide overlay from capture APIs |
| `StealthHumanize` | `true` | Coach / Advisor mode candidate move selection |
| `MultiPvCount` | `4` | MultiPV lines collected for humanizer/advisor |
| `HumanMaxCpLoss` / `HumanTemperature` | `45` / `32` | Coach quality loss / randomness |
| `ShowAdvisorArrows` / `AdvisorMaxGapCp` | `true` / `65` | Advisor arrows and max CP gap |
| `AdvisorArrowColor1` / `AdvisorArrowColor2` | `#00E5FF` / `#E040FB` | Advisor arrow colors |
| `AdvisorArrowOpacity` | `0.82` | Advisor arrow opacity |
| `HumanPacingEnabled` / `HumanDelay*Ms` | `true` / `120-950` blitz, `400-2600` rapid | Pause for independent thinking before hint arrows |
| `EngineIdleUnloadSeconds` | `90` | Unload Stockfish after idle period |

## Build and test

```powershell
dotnet test -c Release
dotnet publish .\ChGhost.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:SatelliteResourceLanguages=ru -o .\Release_Build
```

`models\pieces_48x48_tiny.onnx` is copied to the output/publish directory by the project. Keep `stockfish.exe` next to `ChGhost.exe` or set `EngineExecutablePath`. `Release_Build/` is gitignored.

## ML pipeline (Colab)

Training is intended for Google Colab GPU, not local CPU:

```bash
python generate_dataset.py --tile-size 48   # writes data/synth_48
python train_onnx.py --arch tiny --tile-size 48
```
