using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;

namespace ChGhost
{
    public class PieceTemplate
    {
        public double[] EdgePatch { get; set; } = Array.Empty<double>();
        public double PieceLuma { get; set; }
        public double RawContrast { get; set; }
        public bool IsWhite { get; set; }
    }

    public class SavedConfig
    {
        public int TopLeftX { get; set; }
        public int TopLeftY { get; set; }
        public int BottomRightX { get; set; }
        public int BottomRightY { get; set; }
        public Dictionary<char, List<PieceTemplate>> Signatures { get; set; } = new();
    }

    public partial class MainWindow : Window
    {
        // --- Win32 API ---
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

        // --- Настройки движка (максимальная мощность без потери стабильности) ---
        private bool _fullStrength = true;
        private const int TARGET_ELO = 2600;
        private static readonly int ENGINE_THREADS = Math.Max(4, Environment.ProcessorCount - 2);
        private const int ENGINE_HASH_MB = 512;

        private const string DB_FILE = "board_auto_v9.json";
        private const string LEGACY_DB_FILE = "board_auto_v8.json";
        private const int PATCH_SIZE = 32;

        private readonly (char fen, string name)[] _manualFixOrder =
        {
            ('P', "Белая ПЕШКА"), ('N', "Белый КОНЬ"), ('B', "Белый СЛОН"),
            ('R', "Белая ЛАДЬЯ"), ('Q', "Белый ФЕРЗЬ"), ('K', "Белый КОРОЛЬ"),
            ('p', "Чёрная ПЕШКА"), ('n', "Чёрный КОНЬ"), ('b', "Чёрный СЛОН"),
            ('r', "Чёрная ЛАДЬЯ"), ('q', "Чёрный ФЕРЗЬ"), ('k', "Чёрный КОРОЛЬ")
        };
        private int _fixIndex = 0;

        private Dictionary<char, List<PieceTemplate>> _pieceSignatures = new();

        private Point _topLeft = Point.Empty;
        private Point _bottomRight = Point.Empty;
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
        private string _lastStableFen = "";
        private string _lastRawFen = "";
        private string _lastBestMove = "";
        private string _lastMoveRussianText = "";
        private bool _lastMoveIsCastling = false;
        private string _debugInfo = "";

        // История позиций текущей партии для защиты от ничьей повторением
        private readonly HashSet<string> _seenPositions = new();

        private char[,]? _confirmedGrid = null;
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
            RegisterHotKey(hwnd, 1, mods, 0x31); // Alt+1 — угол A8
            RegisterHotKey(hwnd, 2, mods, 0x32); // Alt+2 — угол H1
            RegisterHotKey(hwnd, 3, mods, 0x33); // Alt+3 — умная авто-калибровка / синхронизация
            RegisterHotKey(hwnd, 4, mods, 0x34); // Alt+4 — выбор фигуры для ручной правки
            RegisterHotKey(hwnd, 5, mods, 0x35); // Alt+5 — полный сброс
            RegisterHotKey(hwnd, 6, mods, 0x53); // Alt+S — старт / пауза
            RegisterHotKey(hwnd, 7, mods, 0x43); // Alt+C — смена стороны (Белые / Чёрные)
            RegisterHotKey(hwnd, 8, mods, 0x48); // Alt+H — скрыть / показать оверлей
            RegisterHotKey(hwnd, 9, mods, 0x51); // Alt+Q — выход
            RegisterHotKey(hwnd, 10, mods, 0x52); // Alt+R — принудительный пересчёт
            RegisterHotKey(hwnd, 11, mods, 0x44); // Alt+D — X-Ray метки на клетках
            RegisterHotKey(hwnd, 12, mods, 0x46); // Alt+F — записать выбранную фигуру под курсором
            RegisterHotKey(hwnd, 14, mods, 0x42); // Alt+B — Блиц / Рапид
            RegisterHotKey(hwnd, 15, mods, 0x45); // Alt+E — сила движка MAX / 2600 Elo

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
                        UpdateUI($"Угол A8 задан ({_topLeft.X}, {_topLeft.Y}) -> теперь Alt+2 на H1");
                        break;

                    case 2:
                        _bottomRight = cursor;
                        SaveConfig();
                        DrawCalibrationGrid();
                        UpdateUI("Углы заданы! Нажмите Alt+3 (авто-определение позиции и старт).");
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
                        ResetBoardTracking(clearHistory: true);
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
                        ResetBoardTracking(clearHistory: false);
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
                }
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void SmartAlt3Action()
        {
            if (_topLeft == Point.Empty || _bottomRight == Point.Empty)
            {
                UpdateUI("Сначала задайте углы доски: Alt+1 (верх-лево) и Alt+2 (низ-право)!");
                return;
            }

            using Bitmap boardBmp = CaptureBoard();
            int backRanksOccupied = 0;
            int centerOccupied = 0;

            var templatesGrid = new PieceTemplate[8, 8];
            for (int r = 0; r < 8; r++)
            {
                for (int c = 0; c < 8; c++)
                {
                    var tpl = ExtractPieceTemplate32x32(boardBmp, r, c);
                    templatesGrid[r, c] = tpl;

                    bool hasPiece = tpl.RawContrast >= 18.0;
                    if (r == 0 || r == 1 || r == 6 || r == 7)
                    {
                        if (hasPiece) backRanksOccupied++;
                    }
                    else
                    {
                        if (hasPiece) centerOccupied++;
                    }
                }
            }

            bool isStartingLayout = (_pieceSignatures.Count < 12 && backRanksOccupied >= 20)
                                    || (backRanksOccupied >= 24 && centerOccupied <= 3);

            if (isStartingLayout)
            {
                string[] startRows =
                {
                    "rnbqkbnr",
                    "pppppppp",
                    "........",
                    "........",
                    "........",
                    "........",
                    "PPPPPPPP",
                    "RNBQKBNR"
                };

                _pieceSignatures.Clear();
                for (int r = 0; r < 8; r++)
                {
                    for (int c = 0; c < 8; c++)
                    {
                        int boardR = _playAsBlack ? 7 - r : r;
                        int boardC = _playAsBlack ? 7 - c : c;
                        char piece = startRows[boardR][boardC];
                        if (piece == '.') continue;

                        var tpl = templatesGrid[r, c];
                        tpl.IsWhite = char.IsUpper(piece);

                        if (!_pieceSignatures.ContainsKey(piece))
                            _pieceSignatures[piece] = new List<PieceTemplate>();

                        _pieceSignatures[piece].Add(tpl);
                    }
                }

                SaveConfig();
                ResetBoardTracking(clearHistory: true);
                SendUciNewGame();
                ClearGraphics();

                if (!_isRunning)
                {
                    _isRunning = true;
                    _scanTimer.Start();
                }

                UpdateUI($"СТАРТ распознан (края={backRanksOccupied}/32) -> Считаю ход...");
                _ = ScanAndSuggestAsync(forceCalculate: true);
            }
            else
            {
                if (_pieceSignatures.Count < 12)
                {
                    UpdateUI($"Не вижу стартовой расстановки (края={backRanksOccupied}, центр={centerOccupied}). Проверьте углы Alt+1/Alt+2!");
                    return;
                }

                ResetBoardTracking(clearHistory: false);
                if (!_isRunning)
                {
                    _isRunning = true;
                    _scanTimer.Start();
                }

                UpdateUI("Синхронизация позиции -> считаю твой ход...");
                _ = ScanAndSuggestAsync(forceCalculate: true);
            }
        }

        private void ManualFixPieceAtCursor(Point cursor)
        {
            if (_topLeft == Point.Empty || _bottomRight == Point.Empty)
            {
                UpdateUI("Сначала задайте углы доски (Alt+1 и Alt+2)!");
                return;
            }

            double cellW = (_bottomRight.X - _topLeft.X) / 8.0;
            double cellH = (_bottomRight.Y - _topLeft.Y) / 8.0;

            int col = (int)((cursor.X - _topLeft.X) / cellW);
            int row = (int)((cursor.Y - _topLeft.Y) / cellH);

            if (col < 0 || col > 7 || row < 0 || row > 7)
            {
                UpdateUI("Курсор вне доски! Наведите на фигуру и нажмите Alt+F.");
                return;
            }

            char targetFen = _manualFixOrder[_fixIndex].fen;
            string savedName = _manualFixOrder[_fixIndex].name;

            using Bitmap boardBmp = CaptureBoard();
            var tpl = ExtractPieceTemplate32x32(boardBmp, row, col);
            tpl.IsWhite = char.IsUpper(targetFen);

            if (!_pieceSignatures.ContainsKey(targetFen))
                _pieceSignatures[targetFen] = new List<PieceTemplate>();

            _pieceSignatures[targetFen].Add(tpl);
            if (_pieceSignatures[targetFen].Count > 2)
                _pieceSignatures[targetFen].RemoveAt(0);

            SaveConfig();

            _fixIndex = (_fixIndex + 1) % _manualFixOrder.Length;

            if (_pieceSignatures.Count < 12)
            {
                UpdateUI($"Записано: [{savedName}] ({_pieceSignatures.Count}/12). Далее: [{_manualFixOrder[_fixIndex].name}] -> Alt+F");
            }
            else
            {
                ResetBoardTracking(clearHistory: false);
                UpdateUI($"Обновлено: [{savedName}]! Далее: [{_manualFixOrder[_fixIndex].name}]");
                if (_isRunning)
                    _ = ScanAndSuggestAsync(forceCalculate: true);
            }
        }

        private void ResetBoardTracking(bool clearHistory = false)
        {
            _lastStableFen = "";
            _lastRawFen = "";
            _lastBestMove = "";
            _lastMoveRussianText = "";
            _lastMoveIsCastling = false;
            _debugInfo = "";
            _confirmedGrid = null;
            _currentGrid = null;
            if (clearHistory)
                _seenPositions.Clear();
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

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string exePath = System.IO.Path.Combine(baseDir, "stockfish.exe");

                if (!File.Exists(exePath))
                {
                    _debugInfo = $"Не найден файл: {exePath}";
                    return false;
                }

                try
                {
                    _stockfish = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = exePath,
                            WorkingDirectory = baseDir,
                            UseShellExecute = false,
                            RedirectStandardInput = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        }
                    };

                    _stockfish.OutputDataReceived += (_, args) =>
                    {
                        if (string.IsNullOrEmpty(args.Data)) return;
                        var waiter = _sfWaiter;
                        if (waiter != null && !string.IsNullOrEmpty(_sfExpectedPrefix) && args.Data.StartsWith(_sfExpectedPrefix))
                        {
                            waiter.TrySetResult(args.Data);
                        }
                    };

                    _stockfish.Start();
                    _stockfish.BeginOutputReadLine();
                    _stockfish.StandardInput.AutoFlush = true;

                    _sfExpectedPrefix = "readyok";
                    _sfWaiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

                    _stockfish.StandardInput.WriteLine("uci");
                    _stockfish.StandardInput.WriteLine($"setoption name Threads value {ENGINE_THREADS}");
                    _stockfish.StandardInput.WriteLine($"setoption name Hash value {ENGINE_HASH_MB}");
                    _stockfish.StandardInput.WriteLine("setoption name MultiPV value 1");
                    _stockfish.StandardInput.WriteLine("setoption name Move Overhead value 15");

                    if (_fullStrength)
                    {
                        _stockfish.StandardInput.WriteLine("setoption name UCI_LimitStrength value false");
                    }
                    else
                    {
                        _stockfish.StandardInput.WriteLine("setoption name UCI_LimitStrength value true");
                        _stockfish.StandardInput.WriteLine($"setoption name UCI_Elo value {TARGET_ELO}");
                    }

                    _stockfish.StandardInput.WriteLine("ucinewgame");
                    _stockfish.StandardInput.WriteLine("isready");

                    if (_sfWaiter.Task.Wait(3500))
                    {
                        _sfExpectedPrefix = "";
                        return true;
                    }

                    _debugInfo = "Stockfish не ответил readyok.";
                    KillStockfish();
                    return false;
                }
                catch (Exception ex)
                {
                    _debugInfo = $"Ошибка запуска: {ex.Message}";
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
                try
                {
                    if (_stockfish != null && !_stockfish.HasExited)
                        _stockfish.Kill();
                }
                catch { }
                _stockfish = null;
            }
        }

        private void UpdateUI(string? customStatus = null)
        {
            string sideText = _playAsBlack ? "ЧЁРНЫЕ" : "БЕЛЫЕ";
            string stateText = _isRunning ? "РАБОТАЕТ" : "ПАУЗА";
            string speedText = _isBlitzTurboMode ? "БЛИЦ" : "РАПИД";
            string powerText = _fullStrength ? $"MAX • {ENGINE_THREADS}T" : TARGET_ELO.ToString();

            HeaderText.Text = $"CHESS GHOST • {sideText} • {stateText} • {speedText} ({powerText})";
            HeaderText.Foreground = _isRunning
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 136))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 200, 60));

            if (customStatus != null)
            {
                StatusText.Text = customStatus;
                return;
            }

            if (_topLeft == Point.Empty || _bottomRight == Point.Empty)
            {
                StatusText.Text = "1. Угол A8 -> Alt+1 | 2. Угол H1 -> Alt+2";
                return;
            }

            if (_pieceSignatures.Count < 12)
            {
                StatusText.Text = $"Нажмите Alt+3 на старте (авто) ИЛИ вручную ({_pieceSignatures.Count}/12): [{_manualFixOrder[_fixIndex].name}] -> Alt+F";
                return;
            }

            if (_isRunning)
            {
                if (!string.IsNullOrEmpty(_lastMoveRussianText))
                    StatusText.Text = $"ТВОЙ ХОД: {_lastMoveRussianText} | Alt+B: режим скорости | Alt+3: обновить";
                else if (!string.IsNullOrEmpty(_debugInfo))
                    StatusText.Text = _debugInfo;
                else
                    StatusText.Text = "Жду хода соперника... (Alt+3: синхронизация | Alt+B: блиц/рапид)";
            }
            else
            {
                StatusText.Text = $"Готово! Alt+3 или Alt+S: старт | Режим: {speedText} (Alt+B)";
            }
        }

        private void ResetAllCalibration()
        {
            _isRunning = false;
            _scanTimer.Stop();
            _pieceSignatures.Clear();
            _fixIndex = 0;
            _topLeft = Point.Empty;
            _bottomRight = Point.Empty;
            ResetBoardTracking(clearHistory: true);

            string cfgPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DB_FILE);
            if (File.Exists(cfgPath)) File.Delete(cfgPath);
            ClearGraphics();
            UpdateUI("Сброшено! Задайте углы доски: Alt+1 и Alt+2.");
        }

        private void SaveConfig()
        {
            string cfgPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DB_FILE);
            var cfg = new SavedConfig
            {
                TopLeftX = _topLeft.X,
                TopLeftY = _topLeft.Y,
                BottomRightX = _bottomRight.X,
                BottomRightY = _bottomRight.Y,
                Signatures = _pieceSignatures
            };
            File.WriteAllText(cfgPath, JsonSerializer.Serialize(cfg));
        }

        private void LoadConfig()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string cfgPath = System.IO.Path.Combine(baseDir, DB_FILE);
            string legacyPath = System.IO.Path.Combine(baseDir, LEGACY_DB_FILE);

            string pathToRead = File.Exists(cfgPath) ? cfgPath : (File.Exists(legacyPath) ? legacyPath : "");
            if (string.IsNullOrEmpty(pathToRead)) return;

            try
            {
                var cfg = JsonSerializer.Deserialize<SavedConfig>(File.ReadAllText(pathToRead));
                if (cfg != null)
                {
                    _topLeft = new Point(cfg.TopLeftX, cfg.TopLeftY);
                    _bottomRight = new Point(cfg.BottomRightX, cfg.BottomRightY);
                    _pieceSignatures = cfg.Signatures ?? new();
                }
            }
            catch { }
        }

        private void ToggleEngine()
        {
            if (_topLeft == Point.Empty || _bottomRight == Point.Empty || _pieceSignatures.Count < 12)
            {
                UpdateUI("Сначала задайте углы (Alt+1, Alt+2) и нажмите Alt+3!");
                return;
            }

            _isRunning = !_isRunning;
            if (_isRunning)
            {
                ResetBoardTracking(clearHistory: false);
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
            if (_topLeft == Point.Empty || _bottomRight == Point.Empty) return;
            if (_bottomRight.X <= _topLeft.X || _bottomRight.Y <= _topLeft.Y) return;

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
            int width = _bottomRight.X - _topLeft.X;
            int height = _bottomRight.Y - _topLeft.Y;
            Bitmap bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using Graphics g = Graphics.FromImage(bmp);
            g.CopyFromScreen(_topLeft.X, _topLeft.Y, 0, 0, new System.Drawing.Size(width, height));
            return bmp;
        }

        private PieceTemplate ExtractPieceTemplate32x32(Bitmap board, int row, int col)
        {
            double cellW = board.Width / 8.0;
            double cellH = board.Height / 8.0;

            int x0 = (int)(col * cellW);
            int y0 = (int)(row * cellH);

            Color[] bgSamples =
            {
                board.GetPixel(x0 + (int)(cellW * 0.88), y0 + (int)(cellH * 0.12)),
                board.GetPixel(x0 + (int)(cellW * 0.88), y0 + (int)(cellH * 0.50)),
                board.GetPixel(x0 + (int)(cellW * 0.50), y0 + (int)(cellH * 0.08))
            };
            double bgLuma = bgSamples.Select(c => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B)
                                     .OrderBy(v => v).ElementAt(1);

            int srcX = (int)(x0 + cellW * 0.12);
            int srcY = (int)(y0 + cellH * 0.12);
            int srcW = Math.Max(PATCH_SIZE, (int)(cellW * 0.76));
            int srcH = Math.Max(PATCH_SIZE, (int)(cellH * 0.76));

            using Bitmap patchBmp = new Bitmap(PATCH_SIZE, PATCH_SIZE, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(patchBmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(board, new System.Drawing.Rectangle(0, 0, PATCH_SIZE, PATCH_SIZE),
                            new System.Drawing.Rectangle(srcX, srcY, srcW, srcH), GraphicsUnit.Pixel);
            }

            double[,] lumaGrid = new double[PATCH_SIZE, PATCH_SIZE];
            var pieceLumas = new List<double>();

            for (int y = 0; y < PATCH_SIZE; y++)
            {
                for (int x = 0; x < PATCH_SIZE; x++)
                {
                    Color c = patchBmp.GetPixel(x, y);
                    double luma = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                    lumaGrid[y, x] = luma;

                    if (Math.Abs(luma - bgLuma) > 22)
                        pieceLumas.Add(luma);
                }
            }

            int len = PATCH_SIZE * PATCH_SIZE;
            double[] edgeVec = new double[len];
            for (int y = 0; y < PATCH_SIZE; y++)
            {
                for (int x = 0; x < PATCH_SIZE; x++)
                {
                    double gx = (x < PATCH_SIZE - 1) ? (lumaGrid[y, x + 1] - lumaGrid[y, x]) : 0;
                    double gy = (y < PATCH_SIZE - 1) ? (lumaGrid[y + 1, x] - lumaGrid[y, x]) : 0;
                    double grad = Math.Sqrt(gx * gx + gy * gy);
                    double fgMask = Math.Abs(lumaGrid[y, x] - bgLuma) > 22 ? 60.0 : 0.0;
                    edgeVec[y * PATCH_SIZE + x] = grad + fgMask;
                }
            }

            double mean = edgeVec.Average();
            double std = Math.Sqrt(edgeVec.Sum(v => (v - mean) * (v - mean)) / len);

            double[] norm = new double[len];
            if (std > 1.0)
            {
                for (int i = 0; i < len; i++)
                    norm[i] = (edgeVec[i] - mean) / std;
            }

            pieceLumas.Sort();
            double coreLuma = pieceLumas.Count > 0
                ? pieceLumas[(int)(pieceLumas.Count * 0.85)]
                : 128.0;

            return new PieceTemplate
            {
                EdgePatch = norm,
                PieceLuma = coreLuma,
                RawContrast = std,
                IsWhite = coreLuma > 195.0
            };
        }

        private (char piece, double confidence, double wkSim, double bkSim) RecognizeCell(PieceTemplate tpl)
        {
            if (tpl.RawContrast < 16.0)
                return ('1', 0.0, -1.0, -1.0);

            char bestPiece = '1';
            double bestScore = -1.0;
            double wkSim = -1.0;
            double bkSim = -1.0;
            int len = PATCH_SIZE * PATCH_SIZE;

            foreach (var kv in _pieceSignatures)
            {
                bool targetIsWhite = char.IsUpper(kv.Key);
                if (targetIsWhite != tpl.IsWhite) continue;

                foreach (var sample in kv.Value)
                {
                    if (sample.EdgePatch.Length != len) continue;

                    double dot = 0;
                    for (int i = 0; i < len; i++)
                        dot += tpl.EdgePatch[i] * sample.EdgePatch[i];
                    double score = dot / len;

                    if (kv.Key == 'K' && score > wkSim) wkSim = score;
                    if (kv.Key == 'k' && score > bkSim) bkSim = score;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestPiece = kv.Key;
                    }
                }
            }

            if (bestScore < 0.20)
                return ('1', 0.0, -1.0, -1.0);

            return (bestPiece, bestScore, wkSim, bkSim);
        }

        private (char[,] grid, Point? lockedWhiteKing, Point? lockedBlackKing) StabilizeGridWithTracking(char[,] rawGrid)
        {
            if (_confirmedGrid == null)
                return ((char[,])rawGrid.Clone(), null, null);

            char[,] stabilized = (char[,])rawGrid.Clone();
            Point? lockedWK = null;
            Point? lockedBK = null;

            var departed = new List<(int r, int c, char piece)>();
            var arrived = new List<(int r, int c, char detectedPiece)>();

            for (int r = 0; r < 8; r++)
            {
                for (int c = 0; c < 8; c++)
                {
                    char oldP = _confirmedGrid[r, c];
                    char newP = rawGrid[r, c];

                    if (oldP == newP)
                    {
                        if (oldP == 'K') lockedWK = new Point(c, r);
                        if (oldP == 'k') lockedBK = new Point(c, r);
                        continue;
                    }

                    if (oldP != '1' && newP == '1')
                    {
                        departed.Add((r, c, oldP));
                    }
                    else if (oldP == '1' && newP != '1')
                    {
                        arrived.Add((r, c, newP));
                    }
                    else if (oldP != '1' && newP != '1')
                    {
                        if (oldP == 'K' || oldP == 'k')
                        {
                            stabilized[r, c] = oldP;
                            if (oldP == 'K') lockedWK = new Point(c, r);
                            if (oldP == 'k') lockedBK = new Point(c, r);
                        }
                        else if (char.IsUpper(oldP) == char.IsUpper(newP))
                        {
                            stabilized[r, c] = oldP;
                        }
                        else
                        {
                            arrived.Add((r, c, newP));
                        }
                    }
                }
            }

            if (departed.Count == 1 && arrived.Count == 1)
            {
                char movedPiece = departed[0].piece;
                char detectedAtDest = arrived[0].detectedPiece;

                if (movedPiece == 'K' || movedPiece == 'k')
                {
                    stabilized[arrived[0].r, arrived[0].c] = movedPiece;
                    if (movedPiece == 'K') lockedWK = new Point(arrived[0].c, arrived[0].r);
                    if (movedPiece == 'k') lockedBK = new Point(arrived[0].c, arrived[0].r);
                }
                else
                {
                    bool isPromotion = char.ToUpper(movedPiece) == 'P' && (arrived[0].r == 0 || arrived[0].r == 7);
                    if (!isPromotion && char.IsUpper(movedPiece) == char.IsUpper(detectedAtDest))
                    {
                        stabilized[arrived[0].r, arrived[0].c] = movedPiece;
                    }
                }
            }
            else if (departed.Count == 2 && arrived.Count == 1)
            {
                var attackingPawn = departed.FirstOrDefault(d => char.IsUpper(d.piece) == char.IsUpper(arrived[0].detectedPiece));
                if (attackingPawn.piece != '\0' && char.ToUpper(attackingPawn.piece) == 'P')
                {
                    stabilized[arrived[0].r, arrived[0].c] = attackingPawn.piece;
                }
            }
            else if (departed.Count == 2 && arrived.Count == 2)
            {
                var kingDep = departed.FirstOrDefault(d => char.ToUpper(d.piece) == 'K');
                var rookDep = departed.FirstOrDefault(d => char.ToUpper(d.piece) == 'R');
                if (kingDep.piece != '\0' && rookDep.piece != '\0')
                {
                    foreach (var arr in arrived)
                    {
                        if (arr.c == 2 || arr.c == 6)
                        {
                            stabilized[arr.r, arr.c] = kingDep.piece;
                            if (kingDep.piece == 'K') lockedWK = new Point(arr.c, arr.r);
                            if (kingDep.piece == 'k') lockedBK = new Point(arr.c, arr.r);
                        }
                        else if (arr.c == 3 || arr.c == 5)
                        {
                            stabilized[arr.r, arr.c] = rookDep.piece;
                        }
                    }
                }
            }

            return (stabilized, lockedWK, lockedBK);
        }

        private static string GridToFen(char[,] grid)
        {
            StringBuilder sb = new();
            for (int r = 0; r < 8; r++)
            {
                int emptyCount = 0;
                for (int c = 0; c < 8; c++)
                {
                    if (grid[r, c] == '1') emptyCount++;
                    else
                    {
                        if (emptyCount > 0) { sb.Append(emptyCount); emptyCount = 0; }
                        sb.Append(grid[r, c]);
                    }
                }
                if (emptyCount > 0) sb.Append(emptyCount);
                if (r < 7) sb.Append('/');
            }
            return sb.ToString();
        }

        private async Task ScanAndSuggestAsync(bool forceCalculate = false)
        {
            if (_isScanning) return;

            if (!forceCalculate && (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0)
                return;

            _isScanning = true;

            try
            {
                bool ready = await Task.Run(EnsureStockfishRunning);
                if (!ready)
                {
                    UpdateUI(_debugInfo);
                    return;
                }

                var scanResult = await Task.Run(() =>
                {
                    using Bitmap bmp = CaptureBoard();
                    char[,] grid = new char[8, 8];
                    double[,] wkSim = new double[8, 8];
                    double[,] bkSim = new double[8, 8];
                    var activeCells = new List<(int r, int c, double score)>();

                    for (int r = 0; r < 8; r++)
                    {
                        for (int c = 0; c < 8; c++)
                        {
                            int boardR = _playAsBlack ? 7 - r : r;
                            int boardC = _playAsBlack ? 7 - c : c;
                            var tpl = ExtractPieceTemplate32x32(bmp, r, c);
                            var res = RecognizeCell(tpl);

                            grid[boardR, boardC] = res.piece;
                            wkSim[boardR, boardC] = res.wkSim;
                            bkSim[boardR, boardC] = res.bkSim;

                            if (res.piece != '1')
                                activeCells.Add((boardR, boardC, res.confidence));
                        }
                    }

                    if (activeCells.Count > 32)
                    {
                        var toDrop = activeCells.OrderBy(x => x.score).Take(activeCells.Count - 32);
                        foreach (var d in toDrop)
                            grid[d.r, d.c] = '1';
                    }

                    return (rawFen: GridToFen(grid), grid: grid, wkSim: wkSim, bkSim: bkSim);
                });

                if (!forceCalculate && scanResult.rawFen != _lastRawFen)
                {
                    _lastRawFen = scanResult.rawFen;
                    return;
                }
                _lastRawFen = scanResult.rawFen;

                var tracked = StabilizeGridWithTracking(scanResult.grid);
                char[,] stableGrid = tracked.grid;

                FixKingCountProtected(stableGrid, scanResult.wkSim, 'K', isWhite: true, lockedKing: tracked.lockedWhiteKing);
                FixKingCountProtected(stableGrid, scanResult.bkSim, 'k', isWhite: false, lockedKing: tracked.lockedBlackKing);

                for (int c = 0; c < 8; c++)
                {
                    if (stableGrid[0, c] == 'P') stableGrid[0, c] = 'Q';
                    if (stableGrid[0, c] == 'p') stableGrid[0, c] = 'q';
                    if (stableGrid[7, c] == 'P') stableGrid[7, c] = 'Q';
                    if (stableGrid[7, c] == 'p') stableGrid[7, c] = 'q';
                }

                int whiteKings = 0, blackKings = 0;
                for (int r = 0; r < 8; r++)
                    for (int c = 0; c < 8; c++)
                    {
                        if (stableGrid[r, c] == 'K') whiteKings++;
                        if (stableGrid[r, c] == 'k') blackKings++;
                    }

                if (whiteKings != 1 || blackKings != 1)
                {
                    _debugInfo = $"Не вижу королей (Б={whiteKings}, Ч={blackKings}).";
                    UpdateUI();
                    return;
                }

                string stableFen = GridToFen(stableGrid);
                _currentGrid = stableGrid;
                _debugInfo = "";

                if (!forceCalculate && stableFen == _lastStableFen)
                {
                    RedrawOverlay();
                    return;
                }

                bool isMyTurn = true;
                if (!forceCalculate && _confirmedGrid != null)
                {
                    bool opponentMoved = false;
                    bool iMoved = false;

                    for (int r = 0; r < 8; r++)
                    {
                        for (int c = 0; c < 8; c++)
                        {
                            char oldCell = _confirmedGrid[r, c];
                            char newCell = stableGrid[r, c];
                            if (oldCell != newCell && newCell != '1')
                            {
                                bool isWhitePiece = char.IsUpper(newCell);
                                bool isMyPiece = _playAsBlack ? !isWhitePiece : isWhitePiece;
                                if (isMyPiece) iMoved = true;
                                else opponentMoved = true;
                            }
                        }
                    }

                    if (iMoved && !opponentMoved)
                    {
                        _lastStableFen = stableFen;
                        _confirmedGrid = stableGrid;
                        _seenPositions.Add(stableFen);
                        _lastBestMove = "";
                        _lastMoveRussianText = "";
                        _lastMoveIsCastling = false;
                        RedrawOverlay();
                        UpdateUI("Жду хода соперника... (Alt+3: если соперник уже сходил)");
                        return;
                    }
                    isMyTurn = opponentMoved || (!iMoved && !opponentMoved);
                }

                _lastStableFen = stableFen;
                _confirmedGrid = stableGrid;
                _seenPositions.Add(stableFen);

                if (!isMyTurn)
                {
                    RedrawOverlay();
                    return;
                }

                char turn = _playAsBlack ? 'b' : 'w';
                string castlingRights = DetectCastlingRights(stableGrid);
                string fullFen = $"{stableFen} {turn} {castlingRights} - 0 1";
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

                        _lastMoveRussianText = $"{pieceRus} {fromSq} → {toSq}{promoSuffix}";
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

        /// <summary>
        /// Автоматически определяет права на рокировку по расположению королей и ладей на стартовых клетках
        /// (усиливает игру движка, разрешая ему планировать и выполнять 0-0 и 0-0-0)
        /// </summary>
        private static string DetectCastlingRights(char[,] grid)
        {
            StringBuilder rights = new();
            if (grid[7, 4] == 'K')
            {
                if (grid[7, 7] == 'R') rights.Append('K');
                if (grid[7, 0] == 'R') rights.Append('Q');
            }
            if (grid[0, 4] == 'k')
            {
                if (grid[0, 7] == 'r') rights.Append('k');
                if (grid[0, 0] == 'r') rights.Append('q');
            }
            return rights.Length > 0 ? rights.ToString() : "-";
        }

        private void FixKingCountProtected(char[,] grid, double[,] kingSim, char kingChar, bool isWhite, Point? lockedKing)
        {
            if (lockedKing.HasValue)
            {
                int kr = lockedKing.Value.Y;
                int kc = lockedKing.Value.X;
                for (int r = 0; r < 8; r++)
                {
                    for (int c = 0; c < 8; c++)
                    {
                        if (r == kr && c == kc)
                            grid[r, c] = kingChar;
                        else if (grid[r, c] == kingChar)
                            grid[r, c] = isWhite ? 'Q' : 'q';
                    }
                }
                return;
            }

            var kings = new List<(int r, int c, double sim)>();
            var sameColorPieces = new List<(int r, int c, double sim)>();

            for (int r = 0; r < 8; r++)
            {
                for (int c = 0; c < 8; c++)
                {
                    char p = grid[r, c];
                    if (p == '1') continue;
                    bool pieceIsWhite = char.IsUpper(p);
                    if (pieceIsWhite == isWhite)
                    {
                        sameColorPieces.Add((r, c, kingSim[r, c]));
                        if (p == kingChar) kings.Add((r, c, kingSim[r, c]));
                    }
                }
            }

            if (kings.Count == 0 && sameColorPieces.Count > 0)
            {
                var best = sameColorPieces.OrderByDescending(x => x.sim).First();
                grid[best.r, best.c] = kingChar;
            }
            else if (kings.Count > 1)
            {
                var ordered = kings.OrderByDescending(x => x.sim).ToList();
                for (int i = 1; i < ordered.Count; i++)
                {
                    grid[ordered[i].r, ordered[i].c] = isWhite ? 'Q' : 'q';
                }
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
            if (_topLeft == Point.Empty || _bottomRight == Point.Empty) return;

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
            for (int i = 1; i <= 15; i++) UnregisterHotKey(hwnd, i);

            _scanTimer.Stop();
            KillStockfish();
            base.OnClosed(e);
        }
    }
}