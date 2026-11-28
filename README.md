# ChGhost v2.2.1

ChGhost is a Windows x64 WPF educational chess overlay trainer for position analysis. It captures the board region, recognizes pieces with ONNX computer vision, validates the resulting FEN, and queries Stockfish over UCI.

## Features

- **ONNX vision**: `TinyChessPieceNet` 48x48 (~0.46 MB) with dirty-square gating and cached unchanged-frame results (`pieces_64x64.onnx` fallback supported, max tile size `256`).
- **Board detection**: `BoardPresenceChecker` occlusion guard, `AutoBoardDetector`, and `playAsBlack` auto-detection with cache reprojection.
- **Engine**: asynchronous Stockfish UCI in a Job Object, FEN boundary validation, and idle auto-unload.
- **Overlay**: `PerMonitorV2` DPI, click-through layered window, `WDA_EXCLUDEFROMCAPTURE`, `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, hidden from the taskbar.
- **Analysis modes**:
  - **Precision mode** (`MultiPV = 1` via `GetBestMoveAsync`, returning `PV #1`): active whenever `StealthHumanize` (`Alt+H`) or `HumanPacingEnabled` (`Alt+T`) is disabled, or `MultiPvCount <= 1`.
  - **Advisor mode** (`StealthHumanize = true`, `HumanPacingEnabled = true`, `MultiPvCount > 1`): collects MultiPV lines, selects a candidate via `HumanMoveSelector` (`HumanMaxCpLoss` clamped to `0–300`), optionally renders alternative arrows, and applies a pre-hint thinking delay.

## Hotkeys

| Hotkey | Action |
| --- | --- |
| `Alt + F` | Auto-detect board on screen (or save template patch in `Template` mode) |
| `Alt + 1` | Set upper-left board corner |
| `Alt + 2` | Set lower-right board corner |
| `Alt + 3` | Async smart start / full position rescan |
| `Alt + S` | Start / pause continuous analysis |
| `Alt + R` | Force immediate rescan and move calculation |
| `Alt + H` | Toggle advisor mode (`StealthHumanize`) |
| `Alt + T` | Toggle pre-hint delay (`HumanPacingEnabled`) |
| `Alt + D` | Toggle X-Ray piece labels overlay (off by default) |
| `Alt + C` | Toggle side (White / Black) |
| `Alt + B` | Toggle Blitz / Rapid timing preset |
| `Alt + E` | Toggle full strength / `TargetElo` |
| `Alt + P` | Switch profile (1–3) |
| `Alt + G` | Hide / show overlay graphics |
| `Alt + 4` | Select piece class for manual template fix (`Template` mode) |
| `Alt + 5` | Reset calibration for active profile |
| `Alt + Q` | Quit |

## Key `settings.json` parameters

| Parameter | Default | Description |
| --- | --- | --- |
| `RecognizerMode` | `Auto` | `Auto`, `Onnx`, or `Template` |
| `OnnxModelPath` | `models/pieces_48x48_tiny.onnx` | ONNX piece model path |
| `EngineExecutablePath` | `stockfish.exe` | Stockfish UCI executable |
| `EngineThreads` | `4` | Stockfish threads |
| `EngineHashMb` | `64` | Stockfish hash size (MB) |
| `TargetElo` / `FullStrength` | `2600` / `true` | Engine strength limiting |
| `MoveTimeBlitzMs` / `MoveTimeRapidMs` | `180` / `550` | Engine move time per preset |
| `ScanIntervalBlitzMs` / `ScanIntervalRapidMs` | `120` / `450` | Board scan interval per preset |
| `CommandTimeoutMs` | `3500` | UCI command timeout |
| `ExcludeFromCapture` | `true` | Exclude overlay from screen capture |
| `StealthHumanize` | `true` | Enable advisor mode (`MultiPV` candidate selection) |
| `MultiPvCount` | `4` | MultiPV lines collected in advisor mode |
| `HumanMaxCpLoss` / `HumanTemperature` | `45` / `32` | Max CP loss (`0–300`) / selection temperature |
| `ShowAdvisorArrows` / `AdvisorMaxGapCp` | `true` / `65` | Alternative advisor arrows and max CP gap |
| `AdvisorArrowColor1` / `AdvisorArrowColor2` | `#00E5FF` / `#E040FB` | Advisor arrow colors |
| `AdvisorArrowOpacity` | `0.82` | Advisor arrow opacity |
| `HumanPacingEnabled` / `HumanDelay*Ms` | `true` / `120-950` blitz, `400-2600` rapid | Pre-hint delay before displaying hint arrows |
| `EngineIdleUnloadSeconds` | `90` | Unload Stockfish after idle timeout |

## Build and test

```powershell
dotnet test -c Release
dotnet publish .\ChGhost.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:SatelliteResourceLanguages=ru -o .\Release_Build
```

`models\pieces_48x48_tiny.onnx` is copied to the output/publish directory by the project. Keep `stockfish.exe` next to `ChGhost.exe` or configure `EngineExecutablePath`. `Release_Build/` is gitignored.

## ML pipeline (Colab)

Training is intended for Google Colab GPU:

```bash
python generate_dataset.py --tile-size 48
python train_onnx.py --arch tiny --tile-size 48
```
