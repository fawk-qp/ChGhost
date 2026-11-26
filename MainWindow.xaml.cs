using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ChGhost.Core.Configuration;
using ChGhost.Core.Contracts;
using ChGhost.Core.Diagnostics;
using ChGhost.Core.Engine;
using ChGhost.Core.Models;
using ChGhost.Core.Native;
using ChGhost.Core.Tracking;
using ChGhost.Core.Vision;
using Microsoft.ML.OnnxRuntime;
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;

namespace ChGhost
{
    public partial class MainWindow : Window
    {
        private const int PROFILE_COUNT = 3;

        private readonly IAppLogger _logger;
        private readonly SettingsStore _settingsStore;
        private readonly AppSettings _settings;
        private readonly string _enginePath;
        private readonly IChessEngine _engine;
        private readonly CancellationTokenSource _appLifetimeCts = new();
        private readonly Random _rng = new();
        private int _activeProfile = 1;
        private readonly IBoardRecognizer _recognizer;
        private readonly IBoardDetector _boardDetector = new ManualBoardDetector();
        private readonly AutoBoardDetector _autoBoardDetector;
        private readonly GameStateTracker _gameStateTracker = new();

        private readonly (char fen, string name)[] _manualFixOrder =
        {
            ('P', "Белая ПЕШКА"), ('N', "Белый КОНЬ"), ('B', "Белый СЛОН"),
            ('R', "Белая ЛАДЬЯ"), ('Q', "Белый ФЕРЗЬ"), ('K', "Белый КОРОЛЬ"),
            ('p', "Чёрная ПЕШКА"), ('n', "Чёрный КОНЬ"), ('b', "Чёрный СЛОН"),
            ('r', "Чёрная ЛАДЬЯ"), ('q', "Чёрный ФЕРЗЬ"), ('k', "Чёрный КОРОЛЬ")
        };
        private int _fixIndex = 0;

        private Point _topLeft
        {
            get => _boardDetector.TopLeft ?? Point.Empty;
            set => _boardDetector.SetTopLeft(value.X, value.Y);
        }

        private Point _bottomRight
        {
            get => _boardDetector.BottomRight ?? Point.Empty;
            set => _boardDetector.SetBottomRight(value.X, value.Y);
        }

        private bool _isRunning = false;
        private bool _playAsBlack = false;
        private bool _isHidden = false;
        private bool _showDebugOverlay = true;
        private bool _isBlitzTurboMode = true;
        private string _startupWarning = "";
        private readonly HashSet<int> _registeredHotkeys = new();
        private long _stateVersion;
        private bool _shutdownStarted;
        private bool _readyToClose;
        private Task? _activeScanTask;
        private CancellationTokenSource? _scanCts;
        private int _rescanRequested;
        private int _rescanForce;
        private System.Drawing.Rectangle _overlayMonitorBounds = System.Drawing.Rectangle.Empty;
        private bool _isAutoDetecting;

        private readonly DispatcherTimer _scanTimer = new();
        private string _lastBestMove = "";
        private string _lastMoveRussianText = "";
        private bool _lastMoveIsCastling = false;
        private string _debugInfo = "";
        private bool _lastBoardVisible = true;
        private string _lastIllegalFen = "";
        private string? _activeFen = null;

        private char[,]? _currentGrid = null;
        private bool _isScanning = false;
        private Bitmap? _captureBitmap;
        private CancellationTokenSource? _humanDelayCts;
        private readonly List<string> _advisorMoves = new();

        public MainWindow()
        {
            _logger = new FileAppLogger();
            _autoBoardDetector = new AutoBoardDetector(_logger, CaptureVirtualScreen);
            _settingsStore = new SettingsStore(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json"), _logger);
            _settings = _settingsStore.Load();
            _recognizer = CreateRecognizer();
            _enginePath = ResolveEnginePath(_settings.EngineExecutablePath);
            _engine = new StockfishEngine(_enginePath, _settings, _logger);
            InitializeComponent();
            LoadConfig();
            UpdateUI();

            ApplySpeedPreset();
            _scanTimer.Tick += (_, _) => _ = RunScanAndSuggestAsync();
        }

        private IBoardRecognizer CreateRecognizer()
        {
            if (_settings.RecognizerMode == "Template")
                return new TemplateRecognizer();

            try
            {
                string modelPath = _settings.OnnxModelPath;
                if (!System.IO.Path.IsPathRooted(modelPath))
                    modelPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, modelPath);
                if (!File.Exists(modelPath))
                {
                    string fallbackPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", "pieces_64x64.onnx");
                    if (File.Exists(fallbackPath) && !string.Equals(modelPath, fallbackPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.Warn($"ONNX model not found: {modelPath}. Falling back to {fallbackPath}.");
                        modelPath = fallbackPath;
                    }
                }
                return new OnnxBoardRecognizer(modelPath, _logger);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException or OnnxRuntimeException)
            {
                _logger.Warn($"ONNX recognizer could not be initialized: {ex.Message}. Falling back to TemplateRecognizer.");
                return new TemplateRecognizer();
            }
        }

        private ICalibratableRecognizer? CalibratableRecognizer => _recognizer as ICalibratableRecognizer;

        private void InvalidateOnnxCache()
        {
            if (_recognizer is OnnxBoardRecognizer onnxRecognizer)
                onnxRecognizer.InvalidateCache();
        }

        private string GetActiveDbFileName() => $"board_auto_v13_p{_activeProfile}.json";

        private string ResolveEnginePath(string configured)
        {
            if (System.IO.Path.IsPathRooted(configured))
                return configured;
            return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configured);
        }

        private void ApplySpeedPreset()
        {
            _scanTimer.Interval = _isBlitzTurboMode
                ? TimeSpan.FromMilliseconds(_settings.ScanIntervalBlitzMs)
                : TimeSpan.FromMilliseconds(_settings.ScanIntervalRapidMs);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr hwnd = new WindowInteropHelper(this).Handle;

            int exStyle = Win32Interop.GetWindowLong(hwnd, Win32Interop.GwlExStyle);
            if (!Win32Interop.TrySetWindowLong(
                    hwnd,
                    Win32Interop.GwlExStyle,
                    exStyle | Win32Interop.WsExLayered | Win32Interop.WsExTransparent,
                    out int styleError))
                _logger.Warn($"SetWindowLong failed with Win32 error {styleError}.");
            Win32Interop.ApplyNoActivateToolStyle(hwnd);

            if (_settings.ExcludeFromCapture)
            {
                if (!Win32Interop.TrySetExcludeFromCapture(hwnd, true))
                    _logger.Warn("SetWindowDisplayAffinity exclude-from-capture failed.");
            }
            else
            {
                _logger.Warn("ExcludeFromCapture disabled in settings; overlay may appear in screenshots.");
                Win32Interop.TrySetExcludeFromCapture(hwnd, false);
            }

            RegisterHotKeys(hwnd);

            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            ApplyOverlayMonitorForBoard();
            UpdateUI();
        }

        private void RegisterHotKeys(IntPtr hwnd)
        {
            (int Id, uint Key, string Name)[] hotkeys =
            {
                (1, 0x31, "Alt+1"), (2, 0x32, "Alt+2"), (3, 0x33, "Alt+3"),
                (4, 0x34, "Alt+4"), (5, 0x35, "Alt+5"), (6, 0x53, "Alt+S"),
                (7, 0x43, "Alt+C"), (8, 0x48, "Alt+H"), (9, 0x51, "Alt+Q"),
                (10, 0x52, "Alt+R"), (11, 0x44, "Alt+D"), (12, 0x46, "Alt+F"),
                (13, 0x47, "Alt+G"), (14, 0x42, "Alt+B"), (15, 0x45, "Alt+E"), (16, 0x50, "Alt+P")
            };
            var failed = new List<string>();
            foreach (var hotkey in hotkeys)
            {
                if (Win32Interop.TryRegisterHotKey(
                        hwnd,
                        hotkey.Id,
                        Win32Interop.ModAlt | Win32Interop.ModNoRepeat,
                        hotkey.Key,
                        out int errorCode))
                {
                    _registeredHotkeys.Add(hotkey.Id);
                    continue;
                }

                failed.Add(hotkey.Name);
                _logger.Warn($"RegisterHotKey failed for {hotkey.Name}; Win32 error {errorCode}.");
            }

            if (failed.Count > 0)
                _startupWarning = $"Не зарегистрированы горячие клавиши: {string.Join(", ", failed)}.";
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Win32Interop.WmDpiChanged)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    ApplyOverlayMonitorForBoard();
                    RedrawOverlay();
                });
                return IntPtr.Zero;
            }

            if (msg == Win32Interop.WmHotkey)
            {
                int id = wParam.ToInt32();
                Point cursor;
                try
                {
                    cursor = Win32Interop.GetCursorPosition();
                }
                catch (Exception ex)
                {
                    _logger.Error("Unable to read cursor position for hotkey.", ex);
                    UpdateUI("Не удалось получить позицию курсора.");
                    handled = true;
                    return IntPtr.Zero;
                }

                switch (id)
                {
                    case 1:
                        _topLeft = cursor;
                        SaveConfig();
                        ApplyOverlayMonitorBounds(new System.Drawing.Rectangle(cursor.X, cursor.Y, 1, 1));
                        DrawCalibrationGrid();
                        InvalidateOnnxCache();
                        UpdateUI($"[П{_activeProfile}] Угол A8 задан ({_topLeft.X}, {_topLeft.Y}) -> теперь Alt+2 на H1");
                        InvalidateScanState(requestRescan: _isRunning);
                        break;

                    case 2:
                        _bottomRight = cursor;
                        SaveConfig();
                        BoardRect? bounds = _boardDetector.Detect();
                        if (bounds.HasValue)
                            ApplyOverlayMonitorBounds(new System.Drawing.Rectangle(bounds.Value.Left, bounds.Value.Top, bounds.Value.Width, bounds.Value.Height));
                        DrawCalibrationGrid();
                        InvalidateOnnxCache();
                        UpdateUI($"[П{_activeProfile}] Углы заданы! Нажмите Alt+3 на старте партии.");
                        InvalidateScanState(requestRescan: _isRunning);
                        break;

                    case 3:
                        SmartAlt3Action();
                        break;

                    case 4:
                        if (CalibratableRecognizer == null)
                        {
                            UpdateUI("[AI] Ручная калибровка не нужна; авто-поиск доски: Alt+F");
                        }
                        else
                        {
                            _fixIndex = (_fixIndex + 1) % _manualFixOrder.Length;
                            UpdateUI($"Ручная правка ({_fixIndex + 1}/12): [{_manualFixOrder[_fixIndex].name}] -> наведите и Alt+F");
                        }
                        break;

                    case 5:
                        ResetAllCalibration();
                        break;

                    case 6:
                        ToggleEngine();
                        break;

                    case 7:
                        _playAsBlack = !_playAsBlack;
                        InvalidateOnnxCache();
                        ResetBoardTracking();
                        RedrawOverlay();
                        UpdateUI();
                        InvalidateScanState(requestRescan: _isRunning, forceCalculate: true);
                        break;

                    case 8:
                        _settings.StealthHumanize = !_settings.StealthHumanize;
                        try
                        {
                            _settingsStore.Save(_settings);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                        {
                            _logger.Error("Unable to save settings after changing coach mode.", ex);
                            _startupWarning = "Не удалось сохранить настройки.";
                        }
                        _logger.Info($"StealthHumanize={_settings.StealthHumanize}");
                        UpdateUI($"Режим тренера (Coach/Advisor): {(_settings.StealthHumanize ? "ON" : "OFF")}");
                        InvalidateScanState(requestRescan: _isRunning, forceCalculate: true);
                        break;
                    case 9:
                        _settings.HumanPacingEnabled = !_settings.HumanPacingEnabled;
                        try
                        {
                            _settingsStore.Save(_settings);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                        {
                            _logger.Error("Unable to save settings after changing human pacing.", ex);
                            _startupWarning = "Не удалось сохранить настройки.";
                        }
                        UpdateUI($"Пауза на обдумывание: {(_settings.HumanPacingEnabled ? "ON" : "OFF")}");
                        InvalidateScanState(requestRescan: _isRunning, forceCalculate: true);
                        break;

                    case 10:
                        InvalidateScanState(requestRescan: _isScanning, forceCalculate: _isScanning);
                        ResetBoardTracking();
                        RedrawOverlay();
                        _ = RunScanAndSuggestAsync(forceCalculate: true);
                        break;

                    case 11:
                        _showDebugOverlay = !_showDebugOverlay;
                        RedrawOverlay();
                        UpdateUI();
                        break;

                    case 12:
                        if (CalibratableRecognizer == null || !HasValidBoardBounds())
                            _ = TryAutoDetectBoardAsync();
                        else
                            ManualFixPieceAtCursor(cursor);
                        break;

                    case 13:
                        _isHidden = !_isHidden;
                        OverlayCanvas.Visibility = _isHidden ? Visibility.Hidden : Visibility.Visible;
                        break;

                    case 14:
                        _isBlitzTurboMode = !_isBlitzTurboMode;
                        ApplySpeedPreset();
                        UpdateUI();
                        break;

                    case 15:
                        _settings.FullStrength = !_settings.FullStrength;
                        try
                        {
                            _settingsStore.Save(_settings);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            _logger.Error("Unable to save settings after changing engine strength.", ex);
                            _startupWarning = "Не удалось сохранить настройки движка.";
                        }
                        _ = RestartEngineAsync();
                        UpdateUI();
                        if (_isRunning) _ = RunScanAndSuggestAsync(forceCalculate: true);
                        break;

                    case 16:
                        SwitchProfile();
                        break;
                }
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void SwitchProfile()
        {
            InvalidateScanState(requestRescan: _isRunning, forceCalculate: true);
            _activeProfile = (_activeProfile % PROFILE_COUNT) + 1;
            _boardDetector.Reset();
            CalibratableRecognizer?.ResetCalibration();
            ResetBoardTracking();
            ClearGraphics();
            LoadConfig();
            ApplyOverlayMonitorForBoard();

            if (HasValidBoardBounds() && HasValidSignatures())
            {
                UpdateUI($"Включён Профиль {_activeProfile} -> синхронизирую доску...");
                if (_isRunning)
                    _ = RunScanAndSuggestAsync(forceCalculate: true);
            }
            else if (HasValidBoardBounds())
            {
                _isRunning = false;
                _scanTimer.Stop();
                UpdateUI($"Профиль {_activeProfile}: углы есть, нажмите 1 раз Alt+3 на старте партии!");
            }
            else
            {
                _isRunning = false;
                _scanTimer.Stop();
                UpdateUI($"Профиль {_activeProfile}: задайте углы (Alt+1, Alt+2) и нажмите Alt+3 на старте!");
            }
        }

        private bool HasValidBoardBounds()
        {
            BoardRect? bounds = _boardDetector.Detect();
            return _topLeft != Point.Empty
                && _bottomRight != Point.Empty
                && bounds.HasValue
                && bounds.Value.IsValid;
        }

        private bool HasValidSignatures() =>
            CalibratableRecognizer?.IsReady ?? _recognizer.IsReady;

        private bool TryAutoDetectBoard(bool triggerScanAfter = true)
        {
            BoardDetectionDiagnostics diag = _autoBoardDetector.DetectWithDiagnostics();
            if (diag.Rect is not { } rect)
            {
                _logger.Warn("Auto board detection did not find a chessboard.");
                UpdateUI("Доска не найдена на экране (Alt+1 / Alt+2 вручную)");
                return false;
            }

            _topLeft = new Point(rect.Left, rect.Top);
            _bottomRight = new Point(rect.Right, rect.Bottom);
            ApplyOverlayMonitorBounds(new System.Drawing.Rectangle(rect.Left, rect.Top, rect.Width, rect.Height));
            SaveConfig();

            if (_recognizer is OnnxBoardRecognizer onnxRec)
            {
                try
                {
                    onnxRec.InvalidateCache();
                    using Bitmap boardBmp = CaptureBoardSnapshot();
                    BoardRecognitionResult recognition = onnxRec.Recognize(boardBmp, _playAsBlack);
                    bool? detected = OnnxBoardRecognizer.TryDetectPlayAsBlackStrict(recognition.RawGrid, _playAsBlack);
                    if (detected.HasValue)
                        _playAsBlack = detected.Value;
                }
                catch (Exception ex)
                {
                    _logger.Error("ONNX side auto-detection after board autodetect failed.", ex);
                }
            }

            InvalidateScanState(requestRescan: _isScanning, forceCalculate: _isScanning);
            ResetBoardTracking();
            DrawCalibrationGrid();

            _logger.Info($"[AUTODETECT] Board found: {rect.Left},{rect.Top} -> {rect.Right},{rect.Bottom} ({rect.Width}x{rect.Height}) | corners={diag.MatchedCorners}/49 | parity={diag.ParitySquares}/64 | ms={diag.ElapsedMs:F1}");
            UpdateUI($"Доска найдена автоматически ({rect.Width}x{rect.Height})");
            if (triggerScanAfter)
                _ = RunScanAndSuggestAsync(forceCalculate: true);
            return true;
        }

        private async Task TryAutoDetectBoardAsync(bool triggerScanAfter = true)
        {
            if (_isAutoDetecting || _shutdownStarted)
                return;
            _isAutoDetecting = true;
            try
            {
                long versionSnap = Interlocked.Read(ref _stateVersion);
                int profileSnap = _activeProfile;
                BoardDetectionDiagnostics diag = await Task.Run(() => _autoBoardDetector.DetectWithDiagnostics());
                if (versionSnap != Interlocked.Read(ref _stateVersion)
                    || profileSnap != _activeProfile
                    || _shutdownStarted)
                {
                    _logger.Info("Auto board detection result discarded: application state changed during scan.");
                    return;
                }
                if (diag.Rect is not { } rect)
                {
                    _logger.Warn("Auto board detection did not find a chessboard.");
                    UpdateUI("Доска не найдена на экране (Alt+1 / Alt+2 вручную)");
                    return;
                }

                _topLeft = new Point(rect.Left, rect.Top);
                _bottomRight = new Point(rect.Right, rect.Bottom);
                ApplyOverlayMonitorBounds(new System.Drawing.Rectangle(rect.Left, rect.Top, rect.Width, rect.Height));
                SaveConfig();

                if (_recognizer is OnnxBoardRecognizer onnxRec)
                {
                    try
                    {
                        onnxRec.InvalidateCache();
                        using Bitmap boardBmp = CaptureBoardSnapshot();
                        BoardRecognitionResult recognition = onnxRec.Recognize(boardBmp, _playAsBlack);
                        bool? detected = OnnxBoardRecognizer.TryDetectPlayAsBlackStrict(recognition.RawGrid, _playAsBlack);
                        if (detected.HasValue)
                            _playAsBlack = detected.Value;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("ONNX side auto-detection after board autodetect failed.", ex);
                    }
                }

                InvalidateScanState(requestRescan: _isScanning, forceCalculate: _isScanning);
                ResetBoardTracking();
                DrawCalibrationGrid();
                _logger.Info($"[AUTODETECT] Board found: {rect.Left},{rect.Top} -> {rect.Right},{rect.Bottom} ({rect.Width}x{rect.Height}) | corners={diag.MatchedCorners}/49 | parity={diag.ParitySquares}/64 | ms={diag.ElapsedMs:F1}");
                UpdateUI($"Доска найдена автоматически ({rect.Width}x{rect.Height})");
                if (triggerScanAfter)
                    _ = RunScanAndSuggestAsync(forceCalculate: true);
            }
            catch (Exception ex)
            {
                _logger.Error("Auto board detection failed.", ex);
                UpdateUI($"Автопоиск доски не удался: {ex.Message}");
            }
            finally
            {
                _isAutoDetecting = false;
            }
        }

        private void SmartAlt3Action()
        {
            bool boardOccluded = _recognizer is OnnxBoardRecognizer onnx && !onnx.LastBoardVisible;
            if (!HasValidBoardBounds() || boardOccluded)
            {
                if (TryAutoDetectBoard(triggerScanAfter: true))
                    return;
                if (!HasValidBoardBounds())
                {
                    UpdateUI("Сначала задайте углы доски: Alt+1 (верх-лево) и Alt+2 (низ-право)!");
                    return;
                }
            }

            if (CalibratableRecognizer == null)
            {
                InvalidateScanState(requestRescan: _isScanning, forceCalculate: _isScanning);
                if (_recognizer is OnnxBoardRecognizer onnxRecognizer)
                    TryDetectOnnxPlayAsBlack(onnxRecognizer);

                ResetBoardTracking();
                ClearGraphics();
                if (!_isRunning)
                {
                    _isRunning = true;
                    _scanTimer.Start();
                }
                UpdateUI("[AI] Полный пересчёт позиции с чистого листа -> считаю ход...");
                _ = RunScanAndSuggestAsync(forceCalculate: true);
                return;
            }

            if (!HasValidSignatures())
                LoadConfig();

            using Bitmap boardBmp = CaptureBoardSnapshot();
            ICalibratableRecognizer calibratable = CalibratableRecognizer
                ?? throw new InvalidOperationException("Template calibratable recognizer is unavailable.");
            CalibrationOutcome calibration = calibratable.CalibrateStartingPosition(boardBmp, _playAsBlack);
            if (calibration.IsStartingPosition)
            {
                _playAsBlack = calibration.PlayAsBlack;
                SaveConfig();
                InvalidateScanState(requestRescan: _isScanning, forceCalculate: _isScanning);
                ResetBoardTracking();
                ClearGraphics();

                if (!_isRunning)
                {
                    _isRunning = true;
                    _scanTimer.Start();
                }

                string sideDetected = _playAsBlack ? "ЧЁРНЫЕ" : "БЕЛЫЕ";
                UpdateUI($"[П{_activeProfile}] Старт v13 снят ({sideDetected}, Б={calibration.WhiteLuma:F0}/Ч={calibration.BlackLuma:F0}) -> Считаю ход...");
                _ = StartNewGameAndScanAsync();
                return;
            }

            if (!HasValidSignatures())
            {
                UpdateUI($"[П{_activeProfile}] Нет эталонов v13 (края={calibration.BackRanksOccupied}, центр={calibration.CenterOccupied}). Нажмите 1 раз Alt+3 на стартовой доске!");
                return;
            }

            InvalidateScanState(requestRescan: _isScanning, forceCalculate: _isScanning);
            ResetBoardTracking();
            ClearGraphics();

            if (!_isRunning)
            {
                _isRunning = true;
                _scanTimer.Start();
            }

            UpdateUI("Полный пересчёт позиции с чистого листа -> считаю ход...");
            _ = RunScanAndSuggestAsync(forceCalculate: true);
        }

        private void TryDetectOnnxPlayAsBlack(OnnxBoardRecognizer onnxRecognizer)
        {
            try
            {
                onnxRecognizer.InvalidateCache();
                using Bitmap boardBmp = CaptureBoardSnapshot();
                BoardRecognitionResult recognition = onnxRecognizer.Recognize(boardBmp, _playAsBlack);
                bool? detected = OnnxBoardRecognizer.TryDetectPlayAsBlackStrict(recognition.RawGrid, _playAsBlack);
                if (!detected.HasValue)
                    return;

                bool previous = _playAsBlack;
                _playAsBlack = detected.Value;
                if (_playAsBlack != previous)
                {
                    ResetBoardTracking();
                    ClearGraphics();
                    RedrawOverlay();
                    UpdateUI($"[AI] Авто-цвет: {(_playAsBlack ? "ЧЁРНЫЕ" : "БЕЛЫЕ")}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("ONNX side auto-detection failed.", ex);
            }
        }

        private void ManualFixPieceAtCursor(Point cursor)
        {
            ICalibratableRecognizer? calibratable = CalibratableRecognizer;
            if (calibratable == null)
            {
                UpdateUI("[AI] Ручная запись эталонов не требуется; распознавание выполняется ONNX-моделью.");
                return;
            }

            if (!HasValidBoardBounds())
            {
                UpdateUI("Сначала задайте углы доски (Alt+1 и Alt+2)!");
                return;
            }

            BoardRect bounds = _boardDetector.Detect()!.Value;
            double cellW = bounds.Width / 8.0;
            double cellH = bounds.Height / 8.0;

            int col = (int)((cursor.X - bounds.Left) / cellW);
            int row = (int)((cursor.Y - bounds.Top) / cellH);

            if (col < 0 || col > 7 || row < 0 || row > 7)
            {
                UpdateUI("Курсор вне доски! Наведите на фигуру и нажмите Alt+F.");
                return;
            }

            char targetFen = _manualFixOrder[_fixIndex].fen;
            string savedName = _manualFixOrder[_fixIndex].name;

            using Bitmap boardBmp = CaptureBoardSnapshot();
            calibratable.CalibratePiece(boardBmp, new Square(row, col), ToPieceClass(targetFen));
            SaveConfig();

            _fixIndex = (_fixIndex + 1) % _manualFixOrder.Length;

            if (!HasValidSignatures())
            {
                UpdateUI($"Записано: [{savedName}] ({calibratable.CalibrationClassCount}/12). Далее: [{_manualFixOrder[_fixIndex].name}] -> Alt+F");
            }
            else
            {
                ResetBoardTracking();
                UpdateUI($"Обновлено: [{savedName}]! Далее: [{_manualFixOrder[_fixIndex].name}]");
                if (_isRunning)
                    _ = RunScanAndSuggestAsync(forceCalculate: true);
            }
        }

        private static PieceClass ToPieceClass(char piece) => piece switch
        {
            'P' => PieceClass.WhitePawn,
            'N' => PieceClass.WhiteKnight,
            'B' => PieceClass.WhiteBishop,
            'R' => PieceClass.WhiteRook,
            'Q' => PieceClass.WhiteQueen,
            'K' => PieceClass.WhiteKing,
            'p' => PieceClass.BlackPawn,
            'n' => PieceClass.BlackKnight,
            'b' => PieceClass.BlackBishop,
            'r' => PieceClass.BlackRook,
            'q' => PieceClass.BlackQueen,
            'k' => PieceClass.BlackKing,
            _ => PieceClass.Empty
        };

        private void ResetBoardTracking()
        {
            CancelHumanDelay();
            _gameStateTracker.Reset();
            _lastBestMove = "";
            _lastMoveRussianText = "";
            _lastMoveIsCastling = false;
            _debugInfo = "";
            _lastIllegalFen = "";
            _activeFen = null;
            _currentGrid = null;
            _advisorMoves.Clear();
        }

        private void InvalidateScanState(bool requestRescan = false, bool forceCalculate = false)
        {
            Interlocked.Increment(ref _stateVersion);
            try { _scanCts?.Cancel(); } catch { }

            if (requestRescan && !_shutdownStarted)
            {
                Interlocked.Exchange(ref _rescanRequested, 1);
                if (forceCalculate) Interlocked.Exchange(ref _rescanForce, 1);
            }
        }

        private void CancelHumanDelay()
        {
            try
            {
                _humanDelayCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task StartNewGameAndScanAsync()
        {
            try
            {
                await _engine.NewGameAsync(_appLifetimeCts.Token);
                await RunScanAndSuggestAsync(forceCalculate: true);
            }
            catch (OperationCanceledException) when (_appLifetimeCts.IsCancellationRequested)
            {
                _logger.Info("New-game request cancelled during application shutdown.");
            }
            catch (Exception ex)
            {
                _logger.Error("Unable to start a new Stockfish game.", ex);
                UpdateUI($"Ошибка Stockfish: {ex.Message}");
            }
        }

        private async Task RestartEngineAsync()
        {
            try
            {
                await _engine.RestartAsync(_appLifetimeCts.Token);
                if (_isRunning)
                    await RunScanAndSuggestAsync(forceCalculate: true);
            }
            catch (OperationCanceledException) when (_appLifetimeCts.IsCancellationRequested)
            {
                _logger.Info("Engine restart cancelled during application shutdown.");
            }
            catch (Exception ex)
            {
                _logger.Error("Unable to restart Stockfish.", ex);
                UpdateUI($"Ошибка перезапуска Stockfish: {ex.Message}");
            }
        }

        private static string GetRussianPieceShort(char fenPiece) => char.ToUpper(fenPiece) switch
        {
            'P' => "П",
            'N' => "К",
            'B' => "С",
            'R' => "Л",
            'Q' => "Ф",
            'K' => "Кр",
            _ => ""
        };

        private static string GetRussianPieceFull(char fenPiece) => char.ToUpper(fenPiece) switch
        {
            'P' => "Пешка",
            'N' => "Конь",
            'B' => "Слон",
            'R' => "Ладья",
            'Q' => "Ферзь",
            'K' => "Король",
            _ => "Фигура"
        };

        private void UpdateUI(string? customStatus = null)
        {
            string sideText = _playAsBlack ? "ЧЁРНЫЕ" : "БЕЛЫЕ";
            string stateText = _isRunning ? "РАБОТАЕТ" : "ПАУЗА";
            string speedText = _isBlitzTurboMode ? "БЛИЦ" : "РАПИД";
            string powerText = _settings.FullStrength ? $"MAX • {_settings.EffectiveEngineThreads}T" : _settings.TargetElo.ToString();

            string recognizerText = CalibratableRecognizer == null
                ? (_settings.StealthHumanize ? "[AI·C]" : "[AI]")
                : $"[П{_activeProfile}]";
            HeaderText.Text = $"CHESS GHOST {recognizerText} • {sideText} • {stateText} • {speedText} ({powerText})";
            HeaderText.Foreground = _isRunning
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 136))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 200, 60));

            if (customStatus != null)
            {
                StatusText.Text = customStatus;
                return;
            }

            if (!string.IsNullOrEmpty(_startupWarning))
            {
                StatusText.Text = _startupWarning;
                return;
            }

            if (!HasValidBoardBounds())
            {
                StatusText.Text = $"[П{_activeProfile}] 1. Alt+F: авто | 2. Угол A8 -> Alt+1 | Угол H1 -> Alt+2 | Alt+P: профиль";
                return;
            }

            if (!HasValidSignatures())
            {
                StatusText.Text = $"[П{_activeProfile}] Откройте старт партии и нажмите 1 раз Alt+3 | Alt+P: профиль";
                return;
            }

            if (_isRunning)
            {
                if (!string.IsNullOrEmpty(_lastMoveRussianText))
                    StatusText.Text = $"ТВОЙ ХОД: {_lastMoveRussianText} | Alt+3: обновить | Alt+P: профиль";
                else if (!string.IsNullOrEmpty(_debugInfo))
                    StatusText.Text = _debugInfo;
                else
                    StatusText.Text = "Жду хода соперника... (Alt+3: пересчитать | Alt+P: профиль)";
            }
            else
            {
                StatusText.Text = $"Готово [П{_activeProfile}]! Alt+3 или Alt+S: старт | Alt+P: профиль | Alt+B: {speedText}";
            }
        }

        private void ResetAllCalibration()
        {
            _isRunning = false;
            _scanTimer.Stop();
            CalibratableRecognizer?.ResetCalibration();
            _fixIndex = 0;
            _boardDetector.Reset();
            ResetBoardTracking();

            string cfgPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, GetActiveDbFileName());
            if (File.Exists(cfgPath)) File.Delete(cfgPath);
            ClearGraphics();
            UpdateUI($"[П{_activeProfile}] Сброшено! Задайте углы доски: Alt+1 и Alt+2.");
        }

        private void SaveConfig()
        {
            ICalibratableRecognizer? calibratable = CalibratableRecognizer;
            if (calibratable == null)
            {
                SaveBoardBounds();
                return;
            }
            string cfgPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, GetActiveDbFileName());
            try
            {
                SavedConfig cfg = calibratable.ExportCalibration();
                cfg.TopLeftX = _topLeft.X;
                cfg.TopLeftY = _topLeft.Y;
                cfg.BottomRightX = _bottomRight.X;
                cfg.BottomRightY = _bottomRight.Y;
                File.WriteAllText(cfgPath, JsonSerializer.Serialize(cfg));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.Error($"Unable to save profile '{cfgPath}'.", ex);
                _startupWarning = "Не удалось сохранить профиль. Проверьте доступ к папке приложения.";
                UpdateUI();
            }
        }

        private void SaveBoardBounds()
        {
            string cfgPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, GetActiveDbFileName());
            try
            {
                SavedConfig cfg = new SavedConfig
                {
                    TopLeftX = _topLeft.X,
                    TopLeftY = _topLeft.Y,
                    BottomRightX = _bottomRight.X,
                    BottomRightY = _bottomRight.Y
                };
                File.WriteAllText(cfgPath, JsonSerializer.Serialize(cfg));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.Error($"Unable to save board bounds '{cfgPath}'.", ex);
                _startupWarning = "Не удалось сохранить границы доски. Проверьте доступ к папке приложения.";
                UpdateUI();
            }
        }

        private void LoadConfig()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string curDir = Directory.GetCurrentDirectory();
            string dbFile = GetActiveDbFileName();

            string[] candidates =
            {
                System.IO.Path.Combine(baseDir, dbFile),
                System.IO.Path.Combine(curDir, dbFile),
                System.IO.Path.Combine(curDir, "Release_Build", dbFile)
            };

            string? path = candidates.FirstOrDefault(File.Exists);
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    var cfg = JsonSerializer.Deserialize<SavedConfig>(File.ReadAllText(path));
                    if (cfg != null)
                    {
                        if (CalibratableRecognizer is { } calibratable)
                            calibratable.ImportCalibration(cfg);
                        _topLeft = new Point(cfg.TopLeftX, cfg.TopLeftY);
                        _bottomRight = new Point(cfg.BottomRightX, cfg.BottomRightY);
                        if (HasValidSignatures())
                            return;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                {
                    _logger.Error($"Unable to load profile '{path}'.", ex);
                    _startupWarning = "Файл профиля повреждён; используются настройки по умолчанию.";
                }
            }

            string[] legacyNames = _activeProfile switch
            {
                1 => new[] { "board_auto_v12.json", "board_auto_v11.json", "board_auto_v10.json", "board_auto_v9.json" },
                2 => new[] { "board_auto_v12_p2.json", "board_auto_v11_p2.json", "board_auto_v10_p2.json" },
                _ => Array.Empty<string>()
            };

            foreach (string legacyName in legacyNames)
            {
                string[] legacyPaths =
                {
                    System.IO.Path.Combine(baseDir, legacyName),
                    System.IO.Path.Combine(curDir, legacyName),
                    System.IO.Path.Combine(curDir, "Release_Build", legacyName)
                };

                string? legacyPath = legacyPaths.FirstOrDefault(File.Exists);
                if (string.IsNullOrEmpty(legacyPath)) continue;

                try
                {
                    var legacyCfg = JsonSerializer.Deserialize<SavedConfig>(File.ReadAllText(legacyPath));
                    if (legacyCfg != null && _topLeft == Point.Empty && _bottomRight == Point.Empty)
                    {
                        _topLeft = new Point(legacyCfg.TopLeftX, legacyCfg.TopLeftY);
                        _bottomRight = new Point(legacyCfg.BottomRightX, legacyCfg.BottomRightY);
                        break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                {
                    _logger.Error($"Unable to load legacy profile '{legacyPath}'.", ex);
                    _startupWarning = "Файл конфигурации повреждён; используются настройки профиля по умолчанию.";
                }
            }
        }

        private void ToggleEngine()
        {
            if (!HasValidBoardBounds() || !HasValidSignatures())
            {
                UpdateUI(CalibratableRecognizer == null
                    ? "[AI] Сначала найдите доску (Alt+F) или задайте углы (Alt+1 и Alt+2), затем Alt+3."
                    : $"[П{_activeProfile}] Сначала найдите доску (Alt+F) или задайте углы (Alt+1, Alt+2) и нажмите Alt+3 на старте партии!");
                return;
            }

            _isRunning = !_isRunning;
            if (_isRunning)
            {
                InvalidateScanState();
                ResetBoardTracking();
                ClearGraphics();
                _scanTimer.Start();
                _ = RunScanAndSuggestAsync(forceCalculate: true);
            }
            else
            {
                InvalidateScanState();
                _scanTimer.Stop();
                CancelHumanDelay();
                ClearGraphics();
            }
            UpdateUI();
        }

        private void DrawCalibrationGrid()
        {
            ClearGraphics();
            if (!HasValidBoardBounds()) return;

            System.Windows.Rect board = GetBoardDipRect();
            double x = board.X;
            double y = board.Y;
            double w = board.Width;
            double h = board.Height;

            var gridBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(140, 0, 255, 136));

            var border = new System.Windows.Shapes.Rectangle
            {
                Width = w,
                Height = h,
                Stroke = gridBrush,
                StrokeThickness = 2
            };
            Canvas.SetLeft(border, x);
            Canvas.SetTop(border, y);
            OverlayCanvas.Children.Add(border);

            for (int i = 1; i < 8; i++)
            {
                OverlayCanvas.Children.Add(new Line
                {
                    X1 = x + (w / 8.0) * i, Y1 = y,
                    X2 = x + (w / 8.0) * i, Y2 = y + h,
                    Stroke = gridBrush, StrokeThickness = 1
                });
                OverlayCanvas.Children.Add(new Line
                {
                    X1 = x, Y1 = y + (h / 8.0) * i,
                    X2 = x + w, Y2 = y + (h / 8.0) * i,
                    Stroke = gridBrush, StrokeThickness = 1
                });
            }
        }

        private System.Windows.Rect GetBoardDipRect()
        {
            System.Windows.Point topLeft = ScreenToOverlayDip(_topLeft);
            System.Windows.Point bottomRight = ScreenToOverlayDip(_bottomRight);
            return new System.Windows.Rect(topLeft, bottomRight);
        }

        private void ApplyOverlayMonitorForBoard()
        {
            if (!HasValidBoardBounds())
                return;
            BoardRect? bounds = _boardDetector.Detect();
            if (bounds.HasValue)
                ApplyOverlayMonitorBounds(new System.Drawing.Rectangle(bounds.Value.Left, bounds.Value.Top, bounds.Value.Width, bounds.Value.Height));
        }

        private void ApplyOverlayMonitorBounds(System.Drawing.Rectangle boardRect)
        {
            if (!Win32Interop.TryGetMonitorBoundsForRect(boardRect, out var monBounds, out bool fullyContained))
                return;
            if (!fullyContained)
                _logger.Warn($"Board rect crosses monitor bounds: board={boardRect}, monitor={monBounds}");
            _overlayMonitorBounds = monBounds;
            try
            {
                WindowState = WindowState.Normal;
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                Win32Interop.SetOverlayBoundsNoActivate(hwnd, monBounds);
            }
            catch (Exception ex)
            {
                _logger.Warn($"SetOverlayBoundsNoActivate failed: {ex.Message}");
            }
        }

        private System.Windows.Point ScreenToOverlayDip(Point screenPoint)
        {
            PresentationSource? source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget == null)
                return new System.Windows.Point(screenPoint.X, screenPoint.Y);

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            System.Drawing.Rectangle windowBounds = Win32Interop.GetWindowRectangle(hwnd);
            int offsetX = !_overlayMonitorBounds.IsEmpty ? _overlayMonitorBounds.Left : windowBounds.Left;
            int offsetY = !_overlayMonitorBounds.IsEmpty ? _overlayMonitorBounds.Top : windowBounds.Top;
            var clientPoint = new System.Windows.Point(
                screenPoint.X - offsetX,
                screenPoint.Y - offsetY);
            return source.CompositionTarget.TransformFromDevice.Transform(clientPoint);
        }

        private (Bitmap Screen, int OffsetX, int OffsetY) CaptureVirtualScreen()
        {
            System.Drawing.Rectangle bounds = Win32Interop.GetVirtualScreenBounds();
            if (bounds.IsEmpty)
                bounds = new System.Drawing.Rectangle(0, 0, 1, 1);

            var bmp = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using Graphics g = Graphics.FromImage(bmp);
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
            return (bmp, bounds.X, bounds.Y);
        }

        private Bitmap CaptureBoard()
        {
            BoardRect bounds = _boardDetector.Detect() ?? throw new InvalidOperationException("Board bounds are not configured.");
            int width = bounds.Width;
            int height = bounds.Height;
            if (_captureBitmap == null || _captureBitmap.Width != width || _captureBitmap.Height != height)
            {
                _captureBitmap?.Dispose();
                _captureBitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            }
            using Graphics g = Graphics.FromImage(_captureBitmap);
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new System.Drawing.Size(width, height));
            return _captureBitmap;
        }

        private Bitmap CaptureBoardSnapshot()
        {
            BoardRect bounds = _boardDetector.Detect() ?? throw new InvalidOperationException("Board bounds are not configured.");
            Bitmap bmp = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using Graphics g = Graphics.FromImage(bmp);
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new System.Drawing.Size(bounds.Width, bounds.Height));
            return bmp;
        }

        private async Task MaybeUnloadIdleEngineAsync()
        {
            if (_settings.EngineIdleUnloadSeconds <= 0)
                return;
            try
            {
                await _engine.UnloadIfIdleAsync(TimeSpan.FromSeconds(_settings.EngineIdleUnloadSeconds));
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                _logger.Warn($"Idle engine unload failed: {ex.Message}");
            }
        }

        private async Task ScanAndSuggestAsync(bool forceCalculate = false)
        {
            if (_isScanning || !HasValidBoardBounds() || !HasValidSignatures()) return;

            if (!forceCalculate && Win32Interop.IsLeftMouseButtonDown())
                return;

            _isScanning = true;
            _scanCts = CancellationTokenSource.CreateLinkedTokenSource(_appLifetimeCts.Token);
            CancellationToken scanToken = _scanCts.Token;
            long versionSnap = Interlocked.Read(ref _stateVersion);
            bool IsStale() => _shutdownStarted || versionSnap != Interlocked.Read(ref _stateVersion);
            bool playAsBlackSnap = _playAsBlack;
            _activeFen = null;
            double inferMs = 0;
            int dirtyCount = 0;
            try
            {
                BoardRecognitionResult recognition = await Task.Run(() =>
                {
                    Bitmap bmp = CaptureBoard();
                    return _recognizer.Recognize(bmp, playAsBlackSnap);
                });
                if (IsStale()) return;

                if (_recognizer is OnnxBoardRecognizer onnxRecognizer)
                {
                    inferMs = onnxRecognizer.LastInferenceMs;
                    dirtyCount = onnxRecognizer.LastDirtySquareCount;
                    if (!onnxRecognizer.LastBoardVisible)
                    {
                        if (_lastBoardVisible)
                        {
                            _logger.Info("Board is occluded; pausing board scans.");
                            _lastBoardVisible = false;
                        }
                        UpdateUI("Доска перекрыта / не видна — сканирование на паузе");
                        await MaybeUnloadIdleEngineAsync();
                        return;
                    }
                    _lastBoardVisible = true;

                    bool? strictSide = OnnxBoardRecognizer.TryDetectPlayAsBlackStrict(recognition.RawGrid, _playAsBlack);
                    if (strictSide.HasValue && strictSide.Value != _playAsBlack)
                    {
                        _playAsBlack = strictSide.Value;
                        recognition = onnxRecognizer.ReprojectCachedResult(_playAsBlack);
                        playAsBlackSnap = _playAsBlack;
                        ResetBoardTracking();
                        SaveConfig();
                        UpdateUI();
                        _logger.Info($"[ORIENT] Auto-flipped playAsBlack={_playAsBlack} from live board.");
                    }
                }

                scanToken.ThrowIfCancellationRequested();
                if (IsStale()) return;
                TrackingUpdate tracking = _gameStateTracker.Update(recognition, forceCalculate, playAsBlackSnap);
                if (tracking.IsDebounced)
                {
                    await MaybeUnloadIdleEngineAsync();
                    return;
                }

                if (!tracking.IsValid)
                {
                    _debugInfo = $"Не вижу королей (Б={tracking.WhiteKings}, Ч={tracking.BlackKings}). Нажмите Alt+3 для пересчёта.";
                    UpdateUI();
                    return;
                }

                char[,] stableGrid = tracking.Grid;
                _currentGrid = stableGrid;
                _debugInfo = "";

                if (tracking.IsUnchanged)
                {
                    await MaybeUnloadIdleEngineAsync();
                    RedrawOverlay();
                    return;
                }

                if (tracking.IsWaitingForOpponent)
                {
                    _lastBestMove = "";
                    _lastMoveRussianText = "";
                    _lastMoveIsCastling = false;
                    _advisorMoves.Clear();
                    await MaybeUnloadIdleEngineAsync();
                    RedrawOverlay();
                    UpdateUI("Жду хода соперника... (Alt+3: если соперник уже сходил)");
                    return;
                }

                if (!tracking.ShouldCalculateMove)
                {
                    await MaybeUnloadIdleEngineAsync();
                    RedrawOverlay();
                    return;
                }

                char turn = playAsBlackSnap ? 'b' : 'w';
                string epTarget = tracking.EnPassantTarget;
                bool epMatchesTurn = epTarget != "-" && epTarget.Length == 2
                    && ((turn == 'w' && epTarget[1] == '6') || (turn == 'b' && epTarget[1] == '3'));
                string fullFen = $"{tracking.StableFen} {turn} {tracking.CastlingRights} {(epMatchesTurn ? epTarget : "-")} {tracking.HalfmoveClock} {tracking.FullmoveNumber}";
                _activeFen = fullFen;
                int moveTimeMs = _isBlitzTurboMode ? _settings.MoveTimeBlitzMs : _settings.MoveTimeRapidMs;

                var (isLegal, illegalReason) = FenLegalityValidator.Validate(stableGrid, whiteToMove: !playAsBlackSnap);
                if (!isLegal)
                {
                    if (fullFen != _lastIllegalFen)
                    {
                        _logger.Info($"[SKIP] Illegal FEN: {illegalReason} | fen={fullFen}");
                        _lastIllegalFen = fullFen;
                    }
                    _debugInfo = $"Нелегальная позиция: {illegalReason}";
                    await MaybeUnloadIdleEngineAsync();
                    UpdateUI(_debugInfo);
                    return;
                }

                _advisorMoves.Clear();
                bool useHumanizer = _settings.StealthHumanize && _settings.MultiPvCount > 1;
                int requestedMultiPv = Math.Clamp(_settings.MultiPvCount, 1, 6);
                IReadOnlyList<CandidateMove> candidates = Array.Empty<CandidateMove>();
                string bestMove = "(none)";
                int appliedDelayMs = 0;
                int pvCount = 1;
                int pickIndex = 1;
                var sfStopwatch = System.Diagnostics.Stopwatch.StartNew();
                if (useHumanizer)
                {
                    candidates = await _engine.GetTopMovesAsync(fullFen, moveTimeMs, requestedMultiPv, scanToken);
                    pvCount = candidates.Count;
                }
                else
                {
                    bestMove = await _engine.GetBestMoveAsync(fullFen, moveTimeMs, scanToken);
                }
                sfStopwatch.Stop();
                if (IsStale() || !_isRunning || scanToken.IsCancellationRequested)
                    return;

                if (useHumanizer)
                {
                    if (candidates.Count == 0)
                    {
                        bestMove = "(none)";
                    }
                    else
                    {
                        var humanizerOptions = new HumanizerOptions(
                            MaxCpLoss: _settings.HumanMaxCpLoss,
                            Temperature: _settings.HumanTemperature,
                            AdvisorMaxGapCp: _settings.AdvisorMaxGapCp,
                            PacingEnabled: _settings.HumanPacingEnabled,
                            MinDelayMs: _isBlitzTurboMode ? _settings.HumanDelayBlitzMinMs : _settings.HumanDelayRapidMinMs,
                            MaxDelayMs: _isBlitzTurboMode ? _settings.HumanDelayBlitzMaxMs : _settings.HumanDelayRapidMaxMs);
                        MoveRecommendation rec = HumanMoveSelector.Select(candidates, stableGrid, humanizerOptions, _rng);
                        bestMove = rec.Primary.Uci;
                        pickIndex = rec.Primary.MultiPvIndex;
                        if (_settings.ShowAdvisorArrows)
                        {
                            foreach (CandidateMove alt in rec.Alternatives.Take(2))
                                _advisorMoves.Add(alt.Uci);
                        }

                        if (_settings.HumanPacingEnabled && rec.RecommendedDelayMs > 0 && !forceCalculate)
                        {
                            var delayCts = CancellationTokenSource.CreateLinkedTokenSource(scanToken);
                            _humanDelayCts = delayCts;
                            try
                            {
                                await Task.Delay(rec.RecommendedDelayMs, delayCts.Token);
                            }
                            catch (OperationCanceledException) when (!_appLifetimeCts.IsCancellationRequested)
                            {
                                return;
                            }
                            finally
                            {
                                if (ReferenceEquals(_humanDelayCts, delayCts))
                                    _humanDelayCts = null;
                                delayCts.Dispose();
                            }
                            if (IsStale() || _activeFen != fullFen || !_isRunning || scanToken.IsCancellationRequested)
                                return;
                            appliedDelayMs = rec.RecommendedDelayMs;
                        }
                    }
                }

                _logger.Info($"[MOVE] black={playAsBlackSnap} | inferMs={inferMs:F1} | dirty={dirtyCount} | sfMs={sfStopwatch.Elapsed.TotalMilliseconds:F1} | delayMs={appliedDelayMs} | pv={pvCount} | pick=#{pickIndex} | fen={fullFen} | best={bestMove}");

                if (IsStale() || !_isRunning || scanToken.IsCancellationRequested)
                    return;

                if (bestMove.Length >= 4 && bestMove != "(none)")
                {
                    _lastBestMove = bestMove;

                    int fromCol = bestMove[0] - 'a';
                    int fromRow = 8 - (bestMove[1] - '0');
                    char movingPiece = (fromRow >= 0 && fromRow < 8 && fromCol >= 0 && fromCol < 8)
                        ? stableGrid[fromRow, fromCol]
                        : 'P';

                    bool isKing = char.ToUpper(movingPiece) == 'K';
                    bool isKingSideCastle = isKing && (bestMove == "e1g1" || bestMove == "e8g8");
                    bool isQueenSideCastle = isKing && (bestMove == "e1c1" || bestMove == "e8c8");
                    _lastMoveIsCastling = isKingSideCastle || isQueenSideCastle;

                    if (isKingSideCastle)
                    {
                        _lastMoveRussianText = "Рокировка 0-0 (короткая)";
                    }
                    else if (isQueenSideCastle)
                    {
                        _lastMoveRussianText = "Рокировка 0-0-0 (длинная)";
                    }
                    else
                    {
                        string pieceRus = GetRussianPieceFull(movingPiece);
                        string fromSq = bestMove.Substring(0, 2);
                        string toSq = bestMove.Substring(2, 2);
                        string promoSuffix = "";
                        if (bestMove.Length >= 5)
                            promoSuffix = $"={GetRussianPieceShort(bestMove[4])}";
                        _lastMoveRussianText = $"{pieceRus} {fromSq} → {toSq}{promoSuffix}";
                    }

                    RedrawOverlay();
                    UpdateUI();
                }
            }
            catch (OperationCanceledException) when (_appLifetimeCts.IsCancellationRequested || (_scanCts?.IsCancellationRequested ?? false))
            {
                _logger.Info("Board scan cancelled.");
            }
            catch (Exception ex)
            {
                _logger.Error($"Board scan or Stockfish operation failed. fen={_activeFen ?? "-"}", ex);
                _debugInfo = File.Exists(_enginePath)
                    ? $"Ошибка движка: {ex.Message}"
                    : $"Не найден {_settings.EngineExecutablePath} рядом с ChGhost.exe.";
                UpdateUI(_debugInfo);
            }
            finally
            {
                _isScanning = false;
                CancellationTokenSource? scanCtsLocal = _scanCts;
                _scanCts = null;
                try { scanCtsLocal?.Dispose(); } catch { }
                if (!_shutdownStarted && Interlocked.Exchange(ref _rescanRequested, 0) == 1)
                {
                    bool force = Interlocked.Exchange(ref _rescanForce, 0) == 1;
                    _ = Dispatcher.BeginInvoke(async delegate
                    {
                        await RunScanAndSuggestAsync(forceCalculate: force).ConfigureAwait(false);
                    });
                }
            }
        }
        private void ScheduleRescanSync(bool forceCalculate)
        {
            if (_shutdownStarted)
                return;
            _ = RunScanAndSuggestAsync(forceCalculate);
        }

        private Task RunScanAndSuggestAsync(bool forceCalculate = false)
        {
            if (_shutdownStarted)
                return Task.CompletedTask;
            Task task = ScanAndSuggestAsync(forceCalculate);
            _activeScanTask = task;
            task.ContinueWith(
                _ =>
                {
                    if (ReferenceEquals(_activeScanTask, task))
                        _activeScanTask = null;
                },
                TaskScheduler.Default);
            return task;
        }

        private void ClearGraphics()
        {
            for (int i = OverlayCanvas.Children.Count - 1; i >= 0; i--)
            {
                if (OverlayCanvas.Children[i] != StatusBox)
                    OverlayCanvas.Children.RemoveAt(i);
            }
        }

        private void RedrawOverlay()
        {
            ClearGraphics();
            if (_showDebugOverlay && _currentGrid != null)
            {
                DrawXRayLabels(_currentGrid);
            }
            if (!string.IsNullOrEmpty(_lastBestMove) && _lastBestMove.Length >= 4)
            {
                if (_settings.ShowAdvisorArrows)
                {
                    double advisorOpacity = Math.Clamp(_settings.AdvisorArrowOpacity, 0.2, 1.0);
                    System.Windows.Media.Color advisorColor1 = ParseAdvisorColor(_settings.AdvisorArrowColor1, System.Windows.Media.Color.FromRgb(0, 229, 255));
                    System.Windows.Media.Color advisorColor2 = ParseAdvisorColor(_settings.AdvisorArrowColor2, System.Windows.Media.Color.FromRgb(224, 64, 251));
                    int advisorIndex = 0;
                    foreach (string altMove in _advisorMoves)
                    {
                        if (advisorIndex >= 2)
                            break;
                        DrawSingleArrow(
                            altMove,
                            advisorIndex == 0 ? advisorColor1 : advisorColor2,
                            thickness: 6.4,
                            offsetY: 16 + advisorIndex * 16,
                            opacity: advisorOpacity,
                            highContrastOutline: true);
                        advisorIndex++;
                    }
                }

                if (_lastMoveIsCastling)
                    DrawCastlingArrows(_lastBestMove);
                else
                    DrawSingleArrow(_lastBestMove, System.Windows.Media.Color.FromArgb(215, 0, 255, 136), thickness: 8);
            }
        }

        private void DrawXRayLabels(char[,] grid)
        {
            if (!HasValidBoardBounds()) return;

            System.Windows.Rect board = GetBoardDipRect();
            double cellW = board.Width / 8.0;
            double cellH = board.Height / 8.0;
            double startX = board.X;
            double startY = board.Y;

            for (int r = 0; r < 8; r++)
            {
                for (int c = 0; c < 8; c++)
                {
                    int boardR = _playAsBlack ? 7 - r : r;
                    int boardC = _playAsBlack ? 7 - c : c;
                    char p = grid[boardR, boardC];
                    if (p == '1') continue;

                    bool isWhite = char.IsUpper(p);
                    var badge = new Border
                    {
                        Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(200, 15, 20, 25)),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(3, 0, 3, 0),
                        Child = new TextBlock
                        {
                            Text = GetRussianPieceShort(p),
                            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                            FontWeight = FontWeights.Bold,
                            FontSize = 11,
                            Foreground = isWhite
                                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 136))
                                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 140, 50))
                        }
                    };
                    Canvas.SetLeft(badge, startX + c * cellW + 3);
                    Canvas.SetTop(badge, startY + r * cellH + 3);
                    OverlayCanvas.Children.Add(badge);
                }
            }
        }

        private void DrawCastlingArrows(string kingUciMove)
        {
            DrawSingleArrow(kingUciMove, System.Windows.Media.Color.FromArgb(225, 0, 255, 136), thickness: 8);

            string rookMove = kingUciMove switch
            {
                "e1g1" => "h1f1",
                "e1c1" => "a1d1",
                "e8g8" => "h8f8",
                "e8c8" => "a8d8",
                _ => ""
            };

            if (!string.IsNullOrEmpty(rookMove))
            {
                DrawSingleArrow(rookMove, System.Windows.Media.Color.FromArgb(205, 0, 200, 255), thickness: 5, offsetY: -10);
            }
        }

        private static System.Windows.Media.Color ParseAdvisorColor(string? value, System.Windows.Media.Color fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;
            try
            {
                return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value.Trim());
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException or InvalidCastException)
            {
                return fallback;
            }
        }

        private void DrawSingleArrow(string uciMove, System.Windows.Media.Color argbColor, double thickness, double offsetY = 0, double opacity = 1.0, bool highContrastOutline = false)
        {
            int c1 = uciMove[0] - 'a';
            int r1 = 8 - (uciMove[1] - '0');
            int c2 = uciMove[2] - 'a';
            int r2 = 8 - (uciMove[3] - '0');

            if (_playAsBlack)
            {
                c1 = 7 - c1; r1 = 7 - r1;
                c2 = 7 - c2; r2 = 7 - r2;
            }

            System.Windows.Rect board = GetBoardDipRect();
            double cellW = board.Width / 8.0;
            double cellH = board.Height / 8.0;
            double startX = board.X;
            double startY = board.Y;

            double x1 = startX + (c1 + 0.5) * cellW;
            double y1 = startY + (r1 + 0.5) * cellH + offsetY;
            double x2 = startX + (c2 + 0.5) * cellW;
            double y2 = startY + (r2 + 0.5) * cellH + offsetY;

            var brush = new SolidColorBrush(argbColor);
            SolidColorBrush? outlineBrush = highContrastOutline
                ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(204, 0, 0, 0))
                : null;

            if (outlineBrush != null)
            {
                OverlayCanvas.Children.Add(new Line
                {
                    X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                    Stroke = outlineBrush,
                    StrokeThickness = thickness + 3.0,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Opacity = opacity
                });
            }

            var line = new Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Stroke = brush,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Opacity = opacity
            };

            double angle = Math.Atan2(y2 - y1, x2 - x1);
            double headLen = thickness * 2.6;
            var arrowHead = new Polygon
            {
                Fill = brush,
                Stroke = outlineBrush,
                StrokeThickness = highContrastOutline ? 1.5 : 0,
                Points = new PointCollection
                {
                    new System.Windows.Point(x2, y2),
                    new System.Windows.Point(x2 - headLen * Math.Cos(angle - Math.PI / 6), y2 - headLen * Math.Sin(angle - Math.PI / 6)),
                    new System.Windows.Point(x2 - headLen * Math.Cos(angle + Math.PI / 6), y2 - headLen * Math.Sin(angle + Math.PI / 6))
                },
                Opacity = opacity
            };

            OverlayCanvas.Children.Add(line);
            OverlayCanvas.Children.Add(arrowHead);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_readyToClose)
            {
                base.OnClosing(e);
                return;
            }

            e.Cancel = true;
            if (_shutdownStarted) return;
            _shutdownStarted = true;

            try { _scanTimer.Stop(); } catch { }
            try { UnregisterAllHotkeys(); } catch { }
            try { _scanCts?.Cancel(); } catch { }
            try { _appLifetimeCts.Cancel(); } catch { }

            _ = ShutdownAndCloseAsync();
        }

        private async Task ShutdownAndCloseAsync()
        {
            try
            {
                var scanTask = _activeScanTask;
                bool scanCompleted = scanTask is null;
                if (scanTask is not null)
                {
                    try
                    {
                        scanCompleted = await Task.WhenAny(scanTask, Task.Delay(1500)) == scanTask;
                    }
                    catch
                    {
                        scanCompleted = false;
                    }
                }
                if (!scanCompleted)
                    _logger.Warn("Active board scan did not finish within the shutdown timeout; shared capture resources are left for the OS to reclaim on exit.");

                try { if (scanCompleted) { _captureBitmap?.Dispose(); _captureBitmap = null; } } catch { }
                try { if (scanCompleted) (_recognizer as IDisposable)?.Dispose(); } catch { }
                try
                {
                    if (_engine is IAsyncDisposable asyncEng)
                        await Task.WhenAny(asyncEng.DisposeAsync().AsTask(), Task.Delay(1500));
                    else
                        (_engine as IDisposable)?.Dispose();
                }
                catch { }

                try { _scanCts?.Dispose(); } catch { }
                try { _appLifetimeCts.Dispose(); } catch { }
            }
            finally
            {
                _readyToClose = true;
                Close();
            }
        }

        private void UnregisterAllHotkeys() => UnregisterHotKeys();

        private void UnregisterHotKeys()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            foreach (int id in _registeredHotkeys)
            {
                if (Win32Interop.TryUnregisterHotKey(hwnd, id, out int errorCode))
                    continue;
                if (errorCode != 0)
                    _logger.Warn($"UnregisterHotKey failed for id {id}; Win32 error {errorCode}.");
            }
            _registeredHotkeys.Clear();
        }
    }
}