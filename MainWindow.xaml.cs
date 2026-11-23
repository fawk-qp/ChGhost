using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ChGhost.Core.Contracts;
using ChGhost.Core.Models;
using ChGhost.Core.Tracking;
using ChGhost.Core.Vision;
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;

namespace ChGhost
{
    public partial class MainWindow : Window
    {
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_NOREPEAT = 0x4000;
        private const int VK_LBUTTON = 0x01;

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Point lpPoint);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private bool _fullStrength = true;
        private const int TARGET_ELO = 2600;
        private static readonly int ENGINE_THREADS = Math.Max(4, Environment.ProcessorCount - 2);
        private const int ENGINE_HASH_MB = 512;

        private const int PROFILE_COUNT = 3;

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

        private Process? _stockfish;
        private readonly object _sfLock = new();
        private TaskCompletionSource<string>? _sfWaiter;
        private string _sfExpectedPrefix = "";

        private readonly DispatcherTimer _scanTimer = new();
        private string _lastBestMove = "";
        private string _lastMoveRussianText = "";
        private bool _lastMoveIsCastling = false;
        private string _debugInfo = "";

        private char[,]? _currentGrid = null;
        private bool _isScanning = false;

        public MainWindow()
        {
            SetProcessDPIAware();
            InitializeComponent();
            LoadConfig();

            ApplySpeedPreset();
            _scanTimer.Tick += async (_, _) => await ScanAndSuggestAsync();
        }

        private string GetActiveDbFileName() => $"board_auto_v13_p{_activeProfile}.json";

        private void ApplySpeedPreset()
        {
            _scanTimer.Interval = _isBlitzTurboMode
                ? TimeSpan.FromMilliseconds(220)
                : TimeSpan.FromMilliseconds(450);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr hwnd = new WindowInteropHelper(this).Handle;

            SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);

            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED | WS_EX_TRANSPARENT);

            uint mods = MOD_ALT | MOD_NOREPEAT;
            RegisterHotKey(hwnd, 1, mods, 0x31);
            RegisterHotKey(hwnd, 2, mods, 0x32);
            RegisterHotKey(hwnd, 3, mods, 0x33);
            RegisterHotKey(hwnd, 4, mods, 0x34);
            RegisterHotKey(hwnd, 5, mods, 0x35);
            RegisterHotKey(hwnd, 6, mods, 0x53);
            RegisterHotKey(hwnd, 7, mods, 0x43);
            RegisterHotKey(hwnd, 8, mods, 0x48);
            RegisterHotKey(hwnd, 9, mods, 0x51);
            RegisterHotKey(hwnd, 10, mods, 0x52);
            RegisterHotKey(hwnd, 11, mods, 0x44);
            RegisterHotKey(hwnd, 12, mods, 0x46);
            RegisterHotKey(hwnd, 14, mods, 0x42);
            RegisterHotKey(hwnd, 15, mods, 0x45);
            RegisterHotKey(hwnd, 16, mods, 0x50);

            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            UpdateUI();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                GetCursorPos(out Point cursor);

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
                        _fullStrength = !_fullStrength;
                        KillStockfish();
                        UpdateUI();
                        if (_isRunning)
                            _ = ScanAndSuggestAsync(forceCalculate: true);
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
                SendUciNewGame();
                ClearGraphics();

                if (!_isRunning)
                {
                    _isRunning = true;
                    _scanTimer.Start();
                }

                string sideDetected = _playAsBlack ? "ЧЁРНЫЕ" : "БЕЛЫЕ";
                UpdateUI($"[П{_activeProfile}] Старт v13 снят ({sideDetected}, Б={calibration.WhiteLuma:F0}/Ч={calibration.BlackLuma:F0}) -> Считаю ход...");
                _ = ScanAndSuggestAsync(forceCalculate: true);
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

        private bool EnsureStockfishRunning()
        {
            lock (_sfLock)
            {
                if (_stockfish != null && !_stockfish.HasExited) return true;

                KillStockfish();

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string exePath = System.IO.Path.Combine(baseDir, "stockfish.exe");

                if (!File.Exists(exePath))
                {
                    _debugInfo = $"Не найден файл: {exePath}";
                    return false;
                }

                Process? candidate = null;
                try
                {
                    candidate = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = exePath,
                            WorkingDirectory = baseDir,
                            UseShellExecute = false,
                            RedirectStandardInput = true,
                            RedirectStandardOutput = true,
                            CreateNoWindow = true
                        }
                    };

                    candidate.OutputDataReceived += (_, args) =>
                    {
                        if (string.IsNullOrEmpty(args.Data)) return;
                        var waiter = _sfWaiter;
                        if (waiter != null && !string.IsNullOrEmpty(_sfExpectedPrefix) && args.Data.StartsWith(_sfExpectedPrefix))
                        {
                            waiter.TrySetResult(args.Data);
                        }
                    };

                    candidate.Start();
                    candidate.BeginOutputReadLine();
                    candidate.StandardInput.AutoFlush = true;

                    _sfExpectedPrefix = "readyok";
                    _sfWaiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

                    candidate.StandardInput.WriteLine("uci");
                    candidate.StandardInput.WriteLine($"setoption name Threads value {ENGINE_THREADS}");
                    candidate.StandardInput.WriteLine($"setoption name Hash value {ENGINE_HASH_MB}");
                    candidate.StandardInput.WriteLine("setoption name MultiPV value 1");
                    candidate.StandardInput.WriteLine("setoption name Move Overhead value 15");

                    if (_fullStrength)
                    {
                        candidate.StandardInput.WriteLine("setoption name UCI_LimitStrength value false");
                    }
                    else
                    {
                        candidate.StandardInput.WriteLine("setoption name UCI_LimitStrength value true");
                        candidate.StandardInput.WriteLine($"setoption name UCI_Elo value {TARGET_ELO}");
                    }

                    candidate.StandardInput.WriteLine("ucinewgame");
                    candidate.StandardInput.WriteLine("isready");

                    if (_sfWaiter.Task.Wait(3500))
                    {
                        _sfExpectedPrefix = "";
                        _stockfish = candidate;
                        return true;
                    }

                    _debugInfo = "Stockfish не ответил readyok.";
                    DisposeProcessSafely(candidate);
                    return false;
                }
                catch (Exception ex)
                {
                    _debugInfo = $"Ошибка запуска: {ex.Message}";
                    DisposeProcessSafely(candidate);
                    return false;
                }
            }
        }

        private void SendUciNewGame()
        {
            lock (_sfLock)
            {
                if (_stockfish != null && !_stockfish.HasExited)
                {
                    try
                    {
                        _stockfish.StandardInput.WriteLine("ucinewgame");
                    }
                    catch { }
                }
            }
        }

        private void KillStockfish()
        {
            lock (_sfLock)
            {
                _sfExpectedPrefix = "";
                _sfWaiter?.TrySetCanceled();
                _sfWaiter = null;
                DisposeProcessSafely(_stockfish);
                _stockfish = null;
            }
        }

        private static void DisposeProcessSafely(Process? proc)
        {
            if (proc == null) return;
            try
            {
                if (!proc.HasExited)
                {
                    try { proc.StandardInput.Close(); } catch { }
                    proc.Kill();
                    proc.WaitForExit(1000);
                }
            }
            catch { }
            finally
            {
                try { proc.Dispose(); } catch { }
            }
        }

        private void UpdateUI(string? customStatus = null)
        {
            string sideText = _playAsBlack ? "ЧЁРНЫЕ" : "БЕЛЫЕ";
            string stateText = _isRunning ? "РАБОТАЕТ" : "ПАУЗА";
            string speedText = _isBlitzTurboMode ? "БЛИЦ" : "РАПИД";
            string powerText = _fullStrength ? $"MAX • {ENGINE_THREADS}T" : TARGET_ELO.ToString();

            HeaderText.Text = $"CHESS GHOST [П{_activeProfile}] • {sideText} • {stateText} • {speedText} ({powerText})";
            HeaderText.Foreground = _isRunning
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 136))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 200, 60));

            if (customStatus != null)
            {
                StatusText.Text = customStatus;
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
            SavedConfig cfg = _recognizer.ExportCalibration();
            cfg.TopLeftX = _topLeft.X;
            cfg.TopLeftY = _topLeft.Y;
            cfg.BottomRightX = _bottomRight.X;
            cfg.BottomRightY = _bottomRight.Y;
            File.WriteAllText(cfgPath, JsonSerializer.Serialize(cfg));
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
                catch { }
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
                catch { }
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

            var source = PresentationSource.FromVisual(this);
            double dpiX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
            double dpiY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

            double x = _topLeft.X * dpiX;
            double y = _topLeft.Y * dpiY;
            double w = (_bottomRight.X - _topLeft.X) * dpiX;
            double h = (_bottomRight.Y - _topLeft.Y) * dpiY;

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

            if (!forceCalculate && (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0)
                return;

            _isScanning = true;
            bool playAsBlackSnap = _playAsBlack;
            try
            {
                bool ready = await Task.Run(EnsureStockfishRunning);
                if (!ready)
                {
                    UpdateUI(_debugInfo);
                    return;
                }

                BoardRecognitionResult recognition = await Task.Run(() =>
                {
                    using Bitmap bmp = CaptureBoard();
                    return _recognizer.Recognize(bmp, playAsBlackSnap);
                });

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
                int moveTimeMs = _isBlitzTurboMode ? 180 : 550;

                string bestMove = await Task.Run(() => QueryStockfish(fullFen, moveTimeMs));
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
                        _lastMoveRussianText = $"{pieceRus} {fromSq} ? {toSq}{promoSuffix}";
                    }

                    RedrawOverlay();
                    UpdateUI();
                }
            }
            finally
            {
                _isScanning = false;
            }
        }
        private string QueryStockfish(string fen, int moveTimeMs)
        {
            lock (_sfLock)
            {
                if (_stockfish == null || _stockfish.HasExited) return "";

                try
                {
                    _sfExpectedPrefix = "bestmove";
                    var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _sfWaiter = tcs;

                    _stockfish.StandardInput.WriteLine($"position fen {fen}");
                    _stockfish.StandardInput.WriteLine($"go movetime {moveTimeMs}");

                    if (tcs.Task.Wait(3500))
                    {
                        _sfExpectedPrefix = "";
                        var parts = tcs.Task.Result.Split(' ');
                        return parts.Length > 1 ? parts[1] : "";
                    }

                    KillStockfish();
                    return "";
                }
                catch
                {
                    KillStockfish();
                    return "";
                }
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

            var source = PresentationSource.FromVisual(this);
            double dpiX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
            double dpiY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

            double cellW = ((_bottomRight.X - _topLeft.X) / 8.0) * dpiX;
            double cellH = ((_bottomRight.Y - _topLeft.Y) / 8.0) * dpiY;
            double startX = _topLeft.X * dpiX;
            double startY = _topLeft.Y * dpiY;

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

            var source = PresentationSource.FromVisual(this);
            double dpiX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
            double dpiY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

            double cellW = ((_bottomRight.X - _topLeft.X) / 8.0) * dpiX;
            double cellH = ((_bottomRight.Y - _topLeft.Y) / 8.0) * dpiY;
            double startX = _topLeft.X * dpiX;
            double startY = _topLeft.Y * dpiY;

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

        protected override void OnClosed(EventArgs e)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            for (int i = 1; i <= 16; i++) UnregisterHotKey(hwnd, i);

            _scanTimer.Stop();
            KillStockfish();
            base.OnClosed(e);
        }
    }
}