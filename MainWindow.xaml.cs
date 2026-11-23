using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
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
        private readonly IChessEngine _engine;
        private readonly CancellationTokenSource _appLifetime = new();
        private int _activeProfile = 1;
        private readonly ICalibratableRecognizer _recognizer = new TemplateRecognizer();
        private readonly IBoardDetector _boardDetector = new ManualBoardDetector();
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
        private bool _closing;
        private bool _allowClose;
        private string _startupWarning = "";
        private readonly HashSet<int> _registeredHotkeys = new();

        private readonly DispatcherTimer _scanTimer = new();
        private string _lastBestMove = "";
        private string _lastMoveRussianText = "";
        private bool _lastMoveIsCastling = false;
        private string _debugInfo = "";

        private char[,]? _currentGrid = null;
        private bool _isScanning = false;

        public MainWindow()
        {
            _logger = new FileAppLogger();
            _settingsStore = new SettingsStore(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json"), _logger);
            _settings = _settingsStore.Load();
            _engine = new StockfishEngine(
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "stockfish.exe"),
                _settings,
                _logger);
            InitializeComponent();
            LoadConfig();

            ApplySpeedPreset();
            _scanTimer.Tick += async (_, _) => await ScanAndSuggestAsync();
        }

        private string GetActiveDbFileName() => $"board_auto_v13_p{_activeProfile}.json";

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

            if (!Win32Interop.TrySetWindowDisplayAffinity(hwnd, Win32Interop.WdaExcludeFromCapture, out int affinityError))
                _logger.Warn($"SetWindowDisplayAffinity failed with Win32 error {affinityError}.");

            int exStyle = Win32Interop.GetWindowLong(hwnd, Win32Interop.GwlExStyle);
            if (!Win32Interop.TrySetWindowLong(
                    hwnd,
                    Win32Interop.GwlExStyle,
                    exStyle | Win32Interop.WsExLayered | Win32Interop.WsExTransparent,
                    out int styleError))
                _logger.Warn($"SetWindowLong failed with Win32 error {styleError}.");

            RegisterHotKeys(hwnd);

            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
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
                (14, 0x42, "Alt+B"), (15, 0x45, "Alt+E"), (16, 0x50, "Alt+P")
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
                        DrawCalibrationGrid();
                        UpdateUI($"[П{_activeProfile}] Угол A8 задан ({_topLeft.X}, {_topLeft.Y}) -> теперь Alt+2 на H1");
                        break;

                    case 2:
                        _bottomRight = cursor;
                        SaveConfig();
                        DrawCalibrationGrid();
                        UpdateUI($"[П{_activeProfile}] Углы заданы! Нажмите Alt+3 на старте партии.");
                        break;

                    case 3:
                        SmartAlt3Action();
                        break;

                    case 4:
                        _fixIndex = (_fixIndex + 1) % _manualFixOrder.Length;
                        UpdateUI($"Ручная правка ({_fixIndex + 1}/12): [{_manualFixOrder[_fixIndex].name}] -> наведите и Alt+F");
                        break;

                    case 5:
                        ResetAllCalibration();
                        break;

                    case 6:
                        ToggleEngine();
                        break;

                    case 7:
                        _playAsBlack = !_playAsBlack;
                        ResetBoardTracking();
                        RedrawOverlay();
                        UpdateUI();
                        break;

                    case 8:
                        _isHidden = !_isHidden;
                        OverlayCanvas.Visibility = _isHidden ? Visibility.Hidden : Visibility.Visible;
                        break;

                    case 9:
                        Close();
                        break;

                    case 10:
                        ResetBoardTracking();
                        RedrawOverlay();
                        _ = ScanAndSuggestAsync(forceCalculate: true);
                        break;

                    case 11:
                        _showDebugOverlay = !_showDebugOverlay;
                        RedrawOverlay();
                        UpdateUI();
                        break;

                    case 12:
                        ManualFixPieceAtCursor(cursor);
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
                        if (_isRunning) _ = ScanAndSuggestAsync(forceCalculate: true);
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
            _activeProfile = (_activeProfile % PROFILE_COUNT) + 1;
            _boardDetector.Reset();
            _recognizer.ResetCalibration();
            ResetBoardTracking();
            ClearGraphics();
            LoadConfig();

            if (HasValidBoardBounds() && HasValidSignatures())
            {
                UpdateUI($"Включён Профиль {_activeProfile} -> синхронизирую доску...");
                if (_isRunning)
                    _ = ScanAndSuggestAsync(forceCalculate: true);
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

        private bool HasValidSignatures() => _recognizer.IsReady;

        private void SmartAlt3Action()
        {
            if (!HasValidBoardBounds())
            {
                UpdateUI("Сначала задайте углы доски: Alt+1 (верх-лево) и Alt+2 (низ-право)!");
                return;
            }

            if (!HasValidSignatures())
                LoadConfig();

            using Bitmap boardBmp = CaptureBoard();
            CalibrationOutcome calibration = _recognizer.CalibrateStartingPosition(boardBmp, _playAsBlack);
            if (calibration.IsStartingPosition)
            {
                _playAsBlack = calibration.PlayAsBlack;
                SaveConfig();
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

            ResetBoardTracking();
            ClearGraphics();

            if (!_isRunning)
            {
                _isRunning = true;
                _scanTimer.Start();
            }

            UpdateUI("Полный пересчёт позиции с чистого листа -> считаю ход...");
            _ = ScanAndSuggestAsync(forceCalculate: true);
        }

        private void ManualFixPieceAtCursor(Point cursor)
        {
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

            using Bitmap boardBmp = CaptureBoard();
            _recognizer.CalibratePiece(boardBmp, new Square(row, col), ToPieceClass(targetFen));
            SaveConfig();

            _fixIndex = (_fixIndex + 1) % _manualFixOrder.Length;

            if (!HasValidSignatures())
            {
                UpdateUI($"Записано: [{savedName}] ({_recognizer.CalibrationClassCount}/12). Далее: [{_manualFixOrder[_fixIndex].name}] -> Alt+F");
            }
            else
            {
                ResetBoardTracking();
                UpdateUI($"Обновлено: [{savedName}]! Далее: [{_manualFixOrder[_fixIndex].name}]");
                if (_isRunning)
                    _ = ScanAndSuggestAsync(forceCalculate: true);
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
            _gameStateTracker.Reset();
            _lastBestMove = "";
            _lastMoveRussianText = "";
            _lastMoveIsCastling = false;
            _debugInfo = "";
            _currentGrid = null;
        }

        private async Task StartNewGameAndScanAsync()
        {
            try
            {
                await _engine.NewGameAsync(_appLifetime.Token);
                await ScanAndSuggestAsync(forceCalculate: true);
            }
            catch (OperationCanceledException) when (_appLifetime.IsCancellationRequested)
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
                await _engine.RestartAsync(_appLifetime.Token);
                if (_isRunning)
                    await ScanAndSuggestAsync(forceCalculate: true);
            }
            catch (OperationCanceledException) when (_appLifetime.IsCancellationRequested)
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

            HeaderText.Text = $"CHESS GHOST [П{_activeProfile}] • {sideText} • {stateText} • {speedText} ({powerText})";
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
                StatusText.Text = $"[П{_activeProfile}] 1. Угол A8 -> Alt+1 | 2. Угол H1 -> Alt+2 | Alt+P: профиль (1-3)";
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
            _recognizer.ResetCalibration();
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
            string cfgPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, GetActiveDbFileName());
            try
            {
                SavedConfig cfg = _recognizer.ExportCalibration();
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
                        _recognizer.ImportCalibration(cfg);
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
                UpdateUI($"[П{_activeProfile}] Сначала задайте углы (Alt+1, Alt+2) и нажмите Alt+3 на старте партии!");
                return;
            }

            _isRunning = !_isRunning;
            if (_isRunning)
            {
                ResetBoardTracking();
                ClearGraphics();
                _scanTimer.Start();
                _ = ScanAndSuggestAsync(forceCalculate: true);
            }
            else
            {
                _scanTimer.Stop();
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

        private System.Windows.Point ScreenToOverlayDip(Point screenPoint)
        {
            PresentationSource? source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget == null)
                return new System.Windows.Point(screenPoint.X, screenPoint.Y);

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            System.Drawing.Rectangle windowBounds = Win32Interop.GetWindowRectangle(hwnd);
            var clientPoint = new System.Windows.Point(
                screenPoint.X - windowBounds.Left,
                screenPoint.Y - windowBounds.Top);
            return source.CompositionTarget.TransformFromDevice.Transform(clientPoint);
        }

        private Bitmap CaptureBoard()
        {
            BoardRect bounds = _boardDetector.Detect() ?? throw new InvalidOperationException("Board bounds are not configured.");
            int width = bounds.Width;
            int height = bounds.Height;
            Bitmap bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using Graphics g = Graphics.FromImage(bmp);
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new System.Drawing.Size(width, height));
            return bmp;
        }

        private async Task ScanAndSuggestAsync(bool forceCalculate = false)
        {
            if (_isScanning || !HasValidBoardBounds() || !HasValidSignatures()) return;

            if (!forceCalculate && Win32Interop.IsLeftMouseButtonDown())
                return;

            _isScanning = true;
            bool playAsBlackSnap = _playAsBlack;
            try
            {
                BoardRecognitionResult recognition = await Task.Run(() =>
                {
                    using Bitmap bmp = CaptureBoard();
                    return _recognizer.Recognize(bmp, playAsBlackSnap);
                });

                _appLifetime.Token.ThrowIfCancellationRequested();
                await _engine.EnsureRunningAsync(_appLifetime.Token);
                TrackingUpdate tracking = _gameStateTracker.Update(recognition, forceCalculate, playAsBlackSnap);
                if (tracking.IsDebounced)
                    return;

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
                    RedrawOverlay();
                    return;
                }

                if (tracking.IsWaitingForOpponent)
                {
                    _lastBestMove = "";
                    _lastMoveRussianText = "";
                    _lastMoveIsCastling = false;
                    RedrawOverlay();
                    UpdateUI("Жду хода соперника... (Alt+3: если соперник уже сходил)");
                    return;
                }

                if (!tracking.ShouldCalculateMove)
                {
                    RedrawOverlay();
                    return;
                }

                char turn = playAsBlackSnap ? 'b' : 'w';
                string fullFen = $"{tracking.StableFen} {turn} {tracking.CastlingRights} - 0 1";
                int moveTimeMs = _isBlitzTurboMode ? _settings.MoveTimeBlitzMs : _settings.MoveTimeRapidMs;

                string bestMove = await _engine.GetBestMoveAsync(fullFen, moveTimeMs, _appLifetime.Token);
                if (bestMove.Length >= 4)
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
            catch (OperationCanceledException) when (_appLifetime.IsCancellationRequested)
            {
                _logger.Info("Board scan cancelled during application shutdown.");
            }
            catch (Exception ex)
            {
                _logger.Error("Board scan or Stockfish operation failed.", ex);
                _debugInfo = File.Exists(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "stockfish.exe"))
                    ? $"Ошибка движка: {ex.Message}"
                    : "Не найден stockfish.exe рядом с ChGhost.exe.";
                UpdateUI(_debugInfo);
            }
            finally
            {
                _isScanning = false;
            }
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

        private void DrawSingleArrow(string uciMove, System.Windows.Media.Color argbColor, double thickness, double offsetY = 0)
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

            var line = new Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Stroke = brush,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };

            double angle = Math.Atan2(y2 - y1, x2 - x1);
            double headLen = thickness * 2.6;
            var arrowHead = new Polygon
            {
                Fill = brush,
                Points = new PointCollection
                {
                    new System.Windows.Point(x2, y2),
                    new System.Windows.Point(x2 - headLen * Math.Cos(angle - Math.PI / 6), y2 - headLen * Math.Sin(angle - Math.PI / 6)),
                    new System.Windows.Point(x2 - headLen * Math.Cos(angle + Math.PI / 6), y2 - headLen * Math.Sin(angle + Math.PI / 6))
                }
            };

            OverlayCanvas.Children.Add(line);
            OverlayCanvas.Children.Add(arrowHead);
        }

        protected override async void OnClosing(CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                base.OnClosing(e);
                if (_closing)
                    return;

                _closing = true;
                _appLifetime.Cancel();
                _scanTimer.Stop();
                UnregisterHotKeys();
                try
                {
                    await _engine.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.Error("Error while disposing chess engine during shutdown.", ex);
                }
                _appLifetime.Dispose();
                _allowClose = true;
                Close();
                return;
            }

            base.OnClosing(e);
        }

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