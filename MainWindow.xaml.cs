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
        public double[] MaskPatch { get; set; } = Array.Empty<double>();
        public double PieceLuma { get; set; }
        public double RawContrast { get; set; }
        public int ForegroundPixels { get; set; }
        public bool IsWhite { get; set; }
    }

    public class SavedConfig
    {
        public int TopLeftX { get; set; }
        public int TopLeftY { get; set; }
        public int BottomRightX { get; set; }
        public int BottomRightY { get; set; }
        public double WhiteBlackThreshold { get; set; } = 150.0;
        public Dictionary<char, List<PieceTemplate>> Signatures { get; set; } = new();
    }

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

        private const string DB_FILE = "board_auto_v10.json";
        private const string LEGACY_V9_FILE = "board_auto_v9.json";
        private const int PATCH_SIZE = 32;
        private static readonly char[] PieceTypesUpper = { 'P', 'N', 'B', 'R', 'Q', 'K' };

        private readonly (char fen, string name)[] _manualFixOrder =
        {
            ('P', "Белая ПЕШКА"), ('N', "Белый КОНЬ"), ('B', "Белый СЛОН"),
            ('R', "Белая ЛАДЬЯ"), ('Q', "Белый ФЕРЗЬ"), ('K', "Белый КОРОЛЬ"),
            ('p', "Чёрная ПЕШКА"), ('n', "Чёрный КОНЬ"), ('b', "Чёрный СЛОН"),
            ('r', "Чёрная ЛАДЬЯ"), ('q', "Чёрный ФЕРЗЬ"), ('k', "Чёрный КОРОЛЬ")
        };
        private int _fixIndex = 0;

        private Dictionary<char, List<PieceTemplate>> _pieceSignatures = new();
        private double _whiteBlackThreshold = 150.0;

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
                        UpdateUI("Углы заданы! Нажмите Alt+3 на старте партии (1 раз) для калибровки.");
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
                }
                handled = true;
            }
            return IntPtr.Zero;
        }

        private bool HasValidBoardBounds()
        {
            return _topLeft != Point.Empty
                && _bottomRight != Point.Empty
                && (_bottomRight.X - _topLeft.X) >= 120
                && (_bottomRight.Y - _topLeft.Y) >= 120;
        }

        private bool HasValidSignatures()
        {
            if (_pieceSignatures.Count < 12) return false;
            foreach (var kv in _pieceSignatures)
            {
                if (kv.Value == null || kv.Value.Count == 0) return false;
                if (kv.Value.Any(t => t.EdgePatch.Length != PATCH_SIZE * PATCH_SIZE || t.MaskPatch.Length != PATCH_SIZE * PATCH_SIZE))
                    return false;
            }
            return true;
        }

        private void SmartAlt3Action()
        {
            if (!HasValidBoardBounds())
            {
                UpdateUI("Сначала задайте углы доски: Alt+1 (верх-лево) и Alt+2 (низ-право)!");
                return;
            }

            if (!HasValidSignatures())
                LoadConfig();

            bool playAsBlackSnap = _playAsBlack;
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

                    bool hasPiece = IsCellOccupied(tpl);
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

            bool isStartingLayout = (backRanksOccupied >= 26 && centerOccupied <= 2);

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
                var whiteLumas = new List<double>();
                var blackLumas = new List<double>();
                var seenSquareParity = new HashSet<(char piece, int parity)>();

                for (int r = 0; r < 8; r++)
                {
                    for (int c = 0; c < 8; c++)
                    {
                        int boardR = playAsBlackSnap ? 7 - r : r;
                        int boardC = playAsBlackSnap ? 7 - c : c;
                        char piece = startRows[boardR][boardC];
                        if (piece == '.') continue;

                        var tpl = templatesGrid[r, c];
                        bool isWhitePiece = char.IsUpper(piece);
                        tpl.IsWhite = isWhitePiece;

                        if (isWhitePiece) whiteLumas.Add(tpl.PieceLuma);
                        else blackLumas.Add(tpl.PieceLuma);

                        int parity = (r + c) & 1;
                        if (!_pieceSignatures.ContainsKey(piece))
                            _pieceSignatures[piece] = new List<PieceTemplate>();

                        if (seenSquareParity.Add((piece, parity)) || _pieceSignatures[piece].Count < 2)
                            _pieceSignatures[piece].Add(tpl);
                    }
                }

                if (whiteLumas.Count > 0 && blackLumas.Count > 0)
                {
                    whiteLumas.Sort();
                    blackLumas.Sort();
                    double medianWhite = whiteLumas[whiteLumas.Count / 2];
                    double medianBlack = blackLumas[blackLumas.Count / 2];
                    _whiteBlackThreshold = (medianWhite + medianBlack) * 0.5;
                }

                SaveConfig();
                ResetBoardTracking();
                SendUciNewGame();
                ClearGraphics();

                if (!_isRunning)
                {
                    _isRunning = true;
                    _scanTimer.Start();
                }

                UpdateUI($"Эталоны v10 сняты со старта (порог={_whiteBlackThreshold:F0}) -> Считаю ход...");
                _ = ScanAndSuggestAsync(forceCalculate: true);
                return;
            }

            if (!HasValidSignatures())
            {
                UpdateUI($"Нет базы эталонов v10 (края={backRanksOccupied}, центр={centerOccupied}). Нажмите 1 раз Alt+3 на стартовой доске!");
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
            if (_pieceSignatures[targetFen].Count > 3)
                _pieceSignatures[targetFen].RemoveAt(0);

            RecalculateWhiteBlackThreshold();
            SaveConfig();

            _fixIndex = (_fixIndex + 1) % _manualFixOrder.Length;

            if (!HasValidSignatures())
            {
                UpdateUI($"Записано: [{savedName}] ({_pieceSignatures.Count}/12). Далее: [{_manualFixOrder[_fixIndex].name}] -> Alt+F");
            }
            else
            {
                ResetBoardTracking();
                UpdateUI($"Обновлено: [{savedName}]! Далее: [{_manualFixOrder[_fixIndex].name}]");
                if (_isRunning)
                    _ = ScanAndSuggestAsync(forceCalculate: true);
            }
        }

        private void RecalculateWhiteBlackThreshold()
        {
            var whiteLumas = new List<double>();
            var blackLumas = new List<double>();

            foreach (var kv in _pieceSignatures)
            {
                bool isWhite = char.IsUpper(kv.Key);
                foreach (var t in kv.Value)
                {
                    if (isWhite) whiteLumas.Add(t.PieceLuma);
                    else blackLumas.Add(t.PieceLuma);
                }
            }

            if (whiteLumas.Count > 0 && blackLumas.Count > 0)
            {
                whiteLumas.Sort();
                blackLumas.Sort();
                _whiteBlackThreshold = (whiteLumas[whiteLumas.Count / 2] + blackLumas[blackLumas.Count / 2]) * 0.5;
            }
        }

        private void ResetBoardTracking()
        {
            _lastStableFen = "";
            _lastRawFen = "";
            _lastBestMove = "";
            _lastMoveRussianText = "";
            _lastMoveIsCastling = false;
            _debugInfo = "";
            _confirmedGrid = null;
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

            HeaderText.Text = $"CHESS GHOST • {sideText} • {stateText} • {speedText} ({powerText})";
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
                StatusText.Text = "1. Угол A8 -> Alt+1 | 2. Угол H1 -> Alt+2";
                return;
            }

            if (!HasValidSignatures())
            {
                StatusText.Text = $"Откройте старт партии и нажмите 1 раз Alt+3 (или вручную: [{_manualFixOrder[_fixIndex].name}] -> Alt+F)";
                return;
            }

            if (_isRunning)
            {
                if (!string.IsNullOrEmpty(_lastMoveRussianText))
                    StatusText.Text = $"ТВОЙ ХОД: {_lastMoveRussianText} | Alt+3: пересчитать | Alt+B: скорость";
                else if (!string.IsNullOrEmpty(_debugInfo))
                    StatusText.Text = _debugInfo;
                else
                    StatusText.Text = "Жду хода соперника... (Alt+3: пересчитать заново | Alt+B: блиц/рапид)";
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
            _whiteBlackThreshold = 150.0;
            _fixIndex = 0;
            _topLeft = Point.Empty;
            _bottomRight = Point.Empty;
            ResetBoardTracking();

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
                WhiteBlackThreshold = _whiteBlackThreshold,
                Signatures = _pieceSignatures
            };
            File.WriteAllText(cfgPath, JsonSerializer.Serialize(cfg));
        }

        private void LoadConfig()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string curDir = Directory.GetCurrentDirectory();

            string[] v10Candidates =
            {
                System.IO.Path.Combine(baseDir, DB_FILE),
                System.IO.Path.Combine(curDir, DB_FILE),
                System.IO.Path.Combine(curDir, "Release_Build", DB_FILE),
                System.IO.Path.Combine(curDir, "ChGhost_v1.0_Win64", DB_FILE)
            };

            string? v10Path = v10Candidates.FirstOrDefault(File.Exists);
            if (!string.IsNullOrEmpty(v10Path))
            {
                try
                {
                    var cfg = JsonSerializer.Deserialize<SavedConfig>(File.ReadAllText(v10Path));
                    if (cfg != null)
                    {
                        _topLeft = new Point(cfg.TopLeftX, cfg.TopLeftY);
                        _bottomRight = new Point(cfg.BottomRightX, cfg.BottomRightY);
                        _whiteBlackThreshold = cfg.WhiteBlackThreshold > 50.0 ? cfg.WhiteBlackThreshold : 150.0;
                        _pieceSignatures = cfg.Signatures ?? new();
                        if (HasValidSignatures())
                            return;
                    }
                }
                catch { }
            }

            string[] legacyCandidates =
            {
                System.IO.Path.Combine(baseDir, LEGACY_V9_FILE),
                System.IO.Path.Combine(curDir, LEGACY_V9_FILE),
                System.IO.Path.Combine(curDir, "Release_Build", LEGACY_V9_FILE),
                System.IO.Path.Combine(curDir, "ChGhost_v1.0_Win64", LEGACY_V9_FILE)
            };

            string? legacyPath = legacyCandidates.FirstOrDefault(File.Exists);
            if (!string.IsNullOrEmpty(legacyPath))
            {
                try
                {
                    var legacyCfg = JsonSerializer.Deserialize<SavedConfig>(File.ReadAllText(legacyPath));
                    if (legacyCfg != null && _topLeft == Point.Empty && _bottomRight == Point.Empty)
                    {
                        _topLeft = new Point(legacyCfg.TopLeftX, legacyCfg.TopLeftY);
                        _bottomRight = new Point(legacyCfg.BottomRightX, legacyCfg.BottomRightY);
                    }
                }
                catch { }
            }
        }

        private void ToggleEngine()
        {
            if (!HasValidBoardBounds() || !HasValidSignatures())
            {
                UpdateUI("Сначала задайте углы (Alt+1, Alt+2) и нажмите Alt+3 на старте партии!");
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
            int width = _bottomRight.X - _topLeft.X;
            int height = _bottomRight.Y - _topLeft.Y;
            Bitmap bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using Graphics g = Graphics.FromImage(bmp);
            g.CopyFromScreen(_topLeft.X, _topLeft.Y, 0, 0, new System.Drawing.Size(width, height));
            return bmp;
        }

        private static bool IsCellOccupied(PieceTemplate tpl)
        {
            return tpl.ForegroundPixels >= 75 && tpl.RawContrast >= 12.0;
        }

        private PieceTemplate ExtractPieceTemplate32x32(Bitmap board, int row, int col)
        {
            double cellW = board.Width / 8.0;
            double cellH = board.Height / 8.0;

            int x0 = (int)(col * cellW);
            int y0 = (int)(row * cellH);

            int srcX = Math.Clamp((int)(x0 + cellW * 0.08), 0, Math.Max(0, board.Width - 1));
            int srcY = Math.Clamp((int)(y0 + cellH * 0.08), 0, Math.Max(0, board.Height - 1));
            int srcW = Math.Clamp((int)(cellW * 0.84), 1, board.Width - srcX);
            int srcH = Math.Clamp((int)(cellH * 0.84), 1, board.Height - srcY);

            using Bitmap patchBmp = new Bitmap(PATCH_SIZE, PATCH_SIZE, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(patchBmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(board, new System.Drawing.Rectangle(0, 0, PATCH_SIZE, PATCH_SIZE),
                            new System.Drawing.Rectangle(srcX, srcY, srcW, srcH), GraphicsUnit.Pixel);
            }

            var bmpData = patchBmp.LockBits(
                new System.Drawing.Rectangle(0, 0, PATCH_SIZE, PATCH_SIZE),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format24bppRgb);

            int stride = bmpData.Stride;
            byte[] rawBytes = new byte[stride * PATCH_SIZE];
            Marshal.Copy(bmpData.Scan0, rawBytes, 0, rawBytes.Length);
            patchBmp.UnlockBits(bmpData);

            double[,] rGrid = new double[PATCH_SIZE, PATCH_SIZE];
            double[,] gGrid = new double[PATCH_SIZE, PATCH_SIZE];
            double[,] bGrid = new double[PATCH_SIZE, PATCH_SIZE];
            double[,] lumaGrid = new double[PATCH_SIZE, PATCH_SIZE];

            for (int y = 0; y < PATCH_SIZE; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < PATCH_SIZE; x++)
                {
                    int idx = rowOffset + x * 3;
                    double b = rawBytes[idx];
                    double g = rawBytes[idx + 1];
                    double r = rawBytes[idx + 2];
                    rGrid[y, x] = r;
                    gGrid[y, x] = g;
                    bGrid[y, x] = b;
                    lumaGrid[y, x] = 0.299 * r + 0.587 * g + 0.114 * b;
                }
            }

            var bgRs = new List<double>(80);
            var bgGs = new List<double>(80);
            var bgBs = new List<double>(80);

            for (int x = 7; x <= 27; x++)
            {
                bgRs.Add(rGrid[0, x]); bgGs.Add(gGrid[0, x]); bgBs.Add(bGrid[0, x]);
                bgRs.Add(rGrid[1, x]); bgGs.Add(gGrid[1, x]); bgBs.Add(bGrid[1, x]);
            }
            for (int y = 3; y <= 24; y++)
            {
                bgRs.Add(rGrid[y, 31]); bgGs.Add(gGrid[y, 31]); bgBs.Add(bGrid[y, 31]);
            }
            for (int y = 7; y <= 24; y++)
            {
                bgRs.Add(rGrid[y, 0]); bgGs.Add(gGrid[y, 0]); bgBs.Add(bGrid[y, 0]);
            }

            bgRs.Sort(); bgGs.Sort(); bgBs.Sort();
            double bgR = bgRs[bgRs.Count / 2];
            double bgG = bgGs[bgGs.Count / 2];
            double bgB = bgBs[bgBs.Count / 2];

            double[,] gradGrid = new double[PATCH_SIZE, PATCH_SIZE];
            for (int y = 0; y < PATCH_SIZE; y++)
            {
                int ym = Math.Max(0, y - 1);
                int yp = Math.Min(PATCH_SIZE - 1, y + 1);
                for (int x = 0; x < PATCH_SIZE; x++)
                {
                    int xm = Math.Max(0, x - 1);
                    int xp = Math.Min(PATCH_SIZE - 1, x + 1);

                    double gxR = (rGrid[ym, xp] + 2 * rGrid[y, xp] + rGrid[yp, xp] - rGrid[ym, xm] - 2 * rGrid[y, xm] - rGrid[yp, xm]) * 0.25;
                    double gyR = (rGrid[yp, xm] + 2 * rGrid[yp, x] + rGrid[yp, xp] - rGrid[ym, xm] - 2 * rGrid[ym, x] - rGrid[ym, xp]) * 0.25;

                    double gxG = (gGrid[ym, xp] + 2 * gGrid[y, xp] + gGrid[yp, xp] - gGrid[ym, xm] - 2 * gGrid[y, xm] - gGrid[yp, xm]) * 0.25;
                    double gyG = (gGrid[yp, xm] + 2 * gGrid[yp, x] + gGrid[yp, xp] - gGrid[ym, xm] - 2 * gGrid[ym, x] - gGrid[ym, xp]) * 0.25;

                    double gxB = (bGrid[ym, xp] + 2 * bGrid[y, xp] + bGrid[yp, xp] - bGrid[ym, xm] - 2 * bGrid[y, xm] - bGrid[yp, xm]) * 0.25;
                    double gyB = (bGrid[yp, xm] + 2 * bGrid[yp, x] + bGrid[yp, xp] - bGrid[ym, xm] - 2 * bGrid[ym, x] - bGrid[ym, xp]) * 0.25;

                    gradGrid[y, x] = Math.Sqrt((gxR * gxR + gyR * gyR + gxG * gxG + gyG * gyG + gxB * gxB + gyB * gyB) / 3.0);
                }
            }

            bool[,] isBg = new bool[PATCH_SIZE, PATCH_SIZE];
            var queue = new Queue<(int x, int y)>(256);

            double ColorDistToBg(int x, int y)
            {
                double dr = rGrid[y, x] - bgR;
                double dg = gGrid[y, x] - bgG;
                double db = bGrid[y, x] - bgB;
                return Math.Sqrt(dr * dr + dg * dg + db * db);
            }

            void TrySeed(int x, int y)
            {
                if (isBg[y, x]) return;
                if (ColorDistToBg(x, y) < 42.0 && gradGrid[y, x] < 32.0)
                {
                    isBg[y, x] = true;
                    queue.Enqueue((x, y));
                }
            }

            for (int i = 0; i < PATCH_SIZE; i++)
            {
                TrySeed(i, 0);
                TrySeed(i, PATCH_SIZE - 1);
                TrySeed(0, i);
                TrySeed(PATCH_SIZE - 1, i);
            }

            int[] dx4 = { 1, -1, 0, 0 };
            int[] dy4 = { 0, 0, 1, -1 };

            while (queue.Count > 0)
            {
                var (cx, cy) = queue.Dequeue();
                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + dx4[d];
                    int ny = cy + dy4[d];
                    if (nx < 0 || nx >= PATCH_SIZE || ny < 0 || ny >= PATCH_SIZE || isBg[ny, nx])
                        continue;

                    if (ColorDistToBg(nx, ny) < 38.0 && gradGrid[ny, nx] < 26.0)
                    {
                        isBg[ny, nx] = true;
                        queue.Enqueue((nx, ny));
                    }
                }
            }

            bool[,] rawFg = new bool[PATCH_SIZE, PATCH_SIZE];
            for (int y = 0; y < PATCH_SIZE; y++)
            {
                for (int x = 0; x < PATCH_SIZE; x++)
                {
                    rawFg[y, x] = !isBg[y, x] || ColorDistToBg(x, y) > 48.0;
                }
            }

            bool[,] pieceMask = new bool[PATCH_SIZE, PATCH_SIZE];
            for (int y = 7; y <= 25; y++)
            {
                for (int x = 7; x <= 24; x++)
                {
                    if (rawFg[y, x] && !pieceMask[y, x])
                    {
                        pieceMask[y, x] = true;
                        queue.Enqueue((x, y));
                    }
                }
            }

            while (queue.Count > 0)
            {
                var (cx, cy) = queue.Dequeue();
                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + dx4[d];
                    int ny = cy + dy4[d];
                    if (nx < 0 || nx >= PATCH_SIZE || ny < 0 || ny >= PATCH_SIZE)
                        continue;
                    if (rawFg[ny, nx] && !pieceMask[ny, nx])
                    {
                        pieceMask[ny, nx] = true;
                        queue.Enqueue((nx, ny));
                    }
                }
            }

            int len = PATCH_SIZE * PATCH_SIZE;
            double[] edgeVec = new double[len];
            double[] maskVec = new double[len];
            var fgLumas = new List<double>(300);
            int fgCount = 0;

            for (int y = 0; y < PATCH_SIZE; y++)
            {
                for (int x = 0; x < PATCH_SIZE; x++)
                {
                    int idx = y * PATCH_SIZE + x;
                    bool inPiece = pieceMask[y, x];
                    bool nearPiece = inPiece
                        || (x > 0 && pieceMask[y, x - 1])
                        || (x < PATCH_SIZE - 1 && pieceMask[y, x + 1])
                        || (y > 0 && pieceMask[y - 1, x])
                        || (y < PATCH_SIZE - 1 && pieceMask[y + 1, x]);

                    edgeVec[idx] = nearPiece ? gradGrid[y, x] : 0.0;
                    maskVec[idx] = inPiece ? 1.0 : 0.0;

                    if (inPiece)
                    {
                        fgCount++;
                        fgLumas.Add(lumaGrid[y, x]);
                    }
                }
            }

            double edgeMean = edgeVec.Average();
            double edgeStd = Math.Sqrt(edgeVec.Sum(v => (v - edgeMean) * (v - edgeMean)) / len);
            double[] edgeNorm = new double[len];
            if (edgeStd > 1.0)
            {
                for (int i = 0; i < len; i++)
                    edgeNorm[i] = (edgeVec[i] - edgeMean) / edgeStd;
            }

            double maskMean = maskVec.Average();
            double maskStd = Math.Sqrt(maskVec.Sum(v => (v - maskMean) * (v - maskMean)) / len);
            double[] maskNorm = new double[len];
            if (maskStd > 0.01)
            {
                for (int i = 0; i < len; i++)
                    maskNorm[i] = (maskVec[i] - maskMean) / maskStd;
            }

            fgLumas.Sort();
            double coreLuma = fgLumas.Count > 0
                ? fgLumas[(int)(fgLumas.Count * 0.65)]
                : 128.0;

            return new PieceTemplate
            {
                EdgePatch = edgeNorm,
                MaskPatch = maskNorm,
                PieceLuma = coreLuma,
                RawContrast = edgeStd,
                ForegroundPixels = fgCount,
                IsWhite = coreLuma >= _whiteBlackThreshold
            };
        }

        private static double ComputeShiftSimilarity(PieceTemplate a, PieceTemplate b)
        {
            double[] ae = a.EdgePatch;
            double[] be = b.EdgePatch;
            double[] am = a.MaskPatch;
            double[] bm = b.MaskPatch;
            int expectedLen = PATCH_SIZE * PATCH_SIZE;
            if (ae.Length != expectedLen || be.Length != expectedLen || am.Length != expectedLen || bm.Length != expectedLen)
                return -1.0;

            double bestScore = -1.0;
            const double invCount = 1.0 / (30.0 * 30.0);

            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    double edgeDot = 0.0;
                    double maskDot = 0.0;

                    for (int y = 1; y < PATCH_SIZE - 1; y++)
                    {
                        int rowA = y * PATCH_SIZE;
                        int rowB = (y + dy) * PATCH_SIZE + dx;
                        for (int x = 1; x < PATCH_SIZE - 1; x++)
                        {
                            int ia = rowA + x;
                            int ib = rowB + x;
                            edgeDot += ae[ia] * be[ib];
                            maskDot += am[ia] * bm[ib];
                        }
                    }

                    double score = 0.62 * (edgeDot * invCount) + 0.38 * (maskDot * invCount);
                    if (score > bestScore)
                        bestScore = score;
                }
            }

            return bestScore;
        }

        private static (char piece, double confidence, double[] scores) RecognizeCellFull(
            PieceTemplate tpl,
            Dictionary<char, List<PieceTemplate>> signatures,
            double whiteBlackThreshold)
        {
            double[] scores = { -1.0, -1.0, -1.0, -1.0, -1.0, -1.0 };
            if (!IsCellOccupied(tpl))
                return ('1', 0.0, scores);

            bool isWhite = tpl.PieceLuma >= whiteBlackThreshold;
            char bestPiece = '1';
            double bestScore = -1.0;

            for (int idx = 0; idx < PieceTypesUpper.Length; idx++)
            {
                char key = isWhite ? PieceTypesUpper[idx] : char.ToLower(PieceTypesUpper[idx]);
                if (!signatures.TryGetValue(key, out var samples))
                    continue;

                double maxSim = -1.0;
                foreach (var sample in samples)
                {
                    double sim = ComputeShiftSimilarity(tpl, sample);
                    if (sim > maxSim)
                        maxSim = sim;
                }

                scores[idx] = maxSim;
                if (maxSim > bestScore)
                {
                    bestScore = maxSim;
                    bestPiece = key;
                }
            }

            if (bestScore < 0.24)
                return ('1', 0.0, scores);

            return (bestPiece, bestScore, scores);
        }

        private static char BestPieceFromScores(double[] scores, bool isWhite, bool allowPawn, bool allowKing)
        {
            int bestIdx = -1;
            double bestVal = double.NegativeInfinity;

            for (int i = 0; i < 6; i++)
            {
                if (!allowPawn && i == 0) continue;
                if (!allowKing && i == 5) continue;
                if (scores[i] > bestVal)
                {
                    bestVal = scores[i];
                    bestIdx = i;
                }
            }

            if (bestIdx < 0) bestIdx = 4;
            char upper = PieceTypesUpper[bestIdx];
            return isWhite ? upper : char.ToLower(upper);
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
                bool isPromotion = char.ToUpper(movedPiece) == 'P' && (arrived[0].r == 0 || arrived[0].r == 7);

                if (!isPromotion)
                {
                    stabilized[arrived[0].r, arrived[0].c] = movedPiece;
                    if (movedPiece == 'K') lockedWK = new Point(arrived[0].c, arrived[0].r);
                    if (movedPiece == 'k') lockedBK = new Point(arrived[0].c, arrived[0].r);
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

        private Dictionary<char, List<PieceTemplate>> SnapshotSignatures()
        {
            return _pieceSignatures.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ToList()
            );
        }

        private async Task ScanAndSuggestAsync(bool forceCalculate = false)
        {
            if (_isScanning || !HasValidBoardBounds() || !HasValidSignatures()) return;

            if (!forceCalculate && (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0)
                return;

            _isScanning = true;
            bool playAsBlackSnap = _playAsBlack;
            double wbThresholdSnap = _whiteBlackThreshold;
            var signaturesSnap = SnapshotSignatures();

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
                    double[,][] scoreMatrix = new double[8, 8][];
                    var activeCells = new List<(int r, int c, double score)>();

                    for (int r = 0; r < 8; r++)
                    {
                        for (int c = 0; c < 8; c++)
                        {
                            int boardR = playAsBlackSnap ? 7 - r : r;
                            int boardC = playAsBlackSnap ? 7 - c : c;
                            var tpl = ExtractPieceTemplate32x32(bmp, r, c);
                            var res = RecognizeCellFull(tpl, signaturesSnap, wbThresholdSnap);

                            grid[boardR, boardC] = res.piece;
                            scoreMatrix[boardR, boardC] = res.scores;

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

                    return (rawFen: GridToFen(grid), grid: grid, scores: scoreMatrix);
                });

                if (!forceCalculate && scanResult.rawFen != _lastRawFen)
                {
                    _lastRawFen = scanResult.rawFen;
                    return;
                }
                _lastRawFen = scanResult.rawFen;

                var tracked = StabilizeGridWithTracking(scanResult.grid);
                char[,] stableGrid = tracked.grid;

                for (int c = 0; c < 8; c++)
                {
                    if (stableGrid[0, c] == 'P')
                        stableGrid[0, c] = BestPieceFromScores(scanResult.scores[0, c], isWhite: true, allowPawn: false, allowKing: true);
                    if (stableGrid[0, c] == 'p')
                        stableGrid[0, c] = BestPieceFromScores(scanResult.scores[0, c], isWhite: false, allowPawn: false, allowKing: true);
                    if (stableGrid[7, c] == 'P')
                        stableGrid[7, c] = BestPieceFromScores(scanResult.scores[7, c], isWhite: true, allowPawn: false, allowKing: true);
                    if (stableGrid[7, c] == 'p')
                        stableGrid[7, c] = BestPieceFromScores(scanResult.scores[7, c], isWhite: false, allowPawn: false, allowKing: true);
                }

                EnforceKingConstraint(stableGrid, scanResult.scores, isWhite: true, lockedKing: tracked.lockedWhiteKing);
                EnforceKingConstraint(stableGrid, scanResult.scores, isWhite: false, lockedKing: tracked.lockedBlackKing);
                EnforcePawnCap(stableGrid, scanResult.scores, isWhite: true);
                EnforcePawnCap(stableGrid, scanResult.scores, isWhite: false);

                int whiteKings = 0, blackKings = 0;
                for (int r = 0; r < 8; r++)
                    for (int c = 0; c < 8; c++)
                    {
                        if (stableGrid[r, c] == 'K') whiteKings++;
                        if (stableGrid[r, c] == 'k') blackKings++;
                    }

                if (whiteKings != 1 || blackKings != 1)
                {
                    _debugInfo = $"Не вижу королей (Б={whiteKings}, Ч={blackKings}). Нажмите Alt+3 для пересчёта.";
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
                                bool isMyPiece = playAsBlackSnap ? !isWhitePiece : isWhitePiece;
                                if (isMyPiece) iMoved = true;
                                else opponentMoved = true;
                            }
                        }
                    }

                    if (iMoved && !opponentMoved)
                    {
                        _lastStableFen = stableFen;
                        _confirmedGrid = stableGrid;
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

                if (!isMyTurn)
                {
                    RedrawOverlay();
                    return;
                }

                char turn = playAsBlackSnap ? 'b' : 'w';
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

        private static void EnforceKingConstraint(char[,] grid, double[,][] scores, bool isWhite, Point? lockedKing)
        {
            char kingChar = isWhite ? 'K' : 'k';

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
                            grid[r, c] = BestPieceFromScores(scores[r, c], isWhite, allowPawn: r > 0 && r < 7, allowKing: false);
                    }
                }
                return;
            }

            var kings = new List<(int r, int c, double score, double margin)>();
            var sameColor = new List<(int r, int c, double kingScore)>();

            for (int r = 0; r < 8; r++)
            {
                for (int c = 0; c < 8; c++)
                {
                    char p = grid[r, c];
                    if (p == '1' || char.IsUpper(p) != isWhite) continue;

                    double kScore = scores[r, c][5];
                    sameColor.Add((r, c, kScore));

                    if (p == kingChar)
                    {
                        double bestOther = -1.0;
                        for (int i = 0; i < 5; i++)
                        {
                            if ((r == 0 || r == 7) && i == 0) continue;
                            if (scores[r, c][i] > bestOther) bestOther = scores[r, c][i];
                        }
                        kings.Add((r, c, kScore, kScore - bestOther));
                    }
                }
            }

            if (kings.Count == 0 && sameColor.Count > 0)
            {
                var best = sameColor.OrderByDescending(x => x.kingScore).First();
                grid[best.r, best.c] = kingChar;
            }
            else if (kings.Count > 1)
            {
                var ordered = kings.OrderByDescending(x => x.margin).ThenByDescending(x => x.score).ToList();
                for (int i = 1; i < ordered.Count; i++)
                {
                    int rr = ordered[i].r;
                    int cc = ordered[i].c;
                    grid[rr, cc] = BestPieceFromScores(scores[rr, cc], isWhite, allowPawn: rr > 0 && rr < 7, allowKing: false);
                }
            }
        }

        private static void EnforcePawnCap(char[,] grid, double[,][] scores, bool isWhite)
        {
            char pawnChar = isWhite ? 'P' : 'p';
            var pawns = new List<(int r, int c, double margin)>();

            for (int r = 1; r < 7; r++)
            {
                for (int c = 0; c < 8; c++)
                {
                    if (grid[r, c] != pawnChar) continue;
                    double pScore = scores[r, c][0];
                    double bestNonPawn = -1.0;
                    for (int i = 1; i < 5; i++)
                    {
                        if (scores[r, c][i] > bestNonPawn) bestNonPawn = scores[r, c][i];
                    }
                    pawns.Add((r, c, pScore - bestNonPawn));
                }
            }

            if (pawns.Count > 8)
            {
                var excess = pawns.OrderBy(x => x.margin).Take(pawns.Count - 8);
                foreach (var item in excess)
                {
                    grid[item.r, item.c] = BestPieceFromScores(scores[item.r, item.c], isWhite, allowPawn: false, allowKing: false);
                }
            }
        }

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
            for (int i = 1; i <= 15; i++) UnregisterHotKey(hwnd, i);

            _scanTimer.Stop();
            KillStockfish();
            base.OnClosed(e);
        }
    }
}