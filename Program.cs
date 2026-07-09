using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockTray;

// ─────────────────────────────────────────────
// 日志服务（写入 exe 同目录下 stocktray.log）
// ─────────────────────────────────────────────
internal static class Logger
{
    private static readonly string LogPath =
        Path.Combine(AppContext.BaseDirectory, "stocktray.log");

    private static readonly object LockObj = new();
    private const long MaxLogSize = 2 * 1024 * 1024; // 2MB 自动滚动

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null)
    {
        var msg = ex == null ? message : $"{message} | Exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
        Write("ERROR", msg);
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (LockObj)
            {
                // 超过 2MB 时滚动存档
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogSize)
                {
                    var archivePath = Path.Combine(
                        AppContext.BaseDirectory,
                        $"stocktray_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                    File.Move(LogPath, archivePath);
                }

                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                var line = $"[{timestamp}] [{level}] {message}\n";
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写入失败不崩溃
        }
    }
}

// ─────────────────────────────────────────────
// Win32 互操作：安全释放 HICON 非托管句柄
// ─────────────────────────────────────────────
internal static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);
}

// ─────────────────────────────────────────────
// 数据模型：持久化配置
// ─────────────────────────────────────────────
internal sealed class AppConfig
{
    [JsonPropertyName("stocks")]
    public List<string> Stocks { get; set; } = new();

    [JsonPropertyName("currentStock")]
    public string CurrentStock { get; set; } = string.Empty;
}

// ─────────────────────────────────────────────
// 数据模型：行情快照
// ─────────────────────────────────────────────
internal sealed class StockSnapshot
{
    public string Name { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public double BasePrice  { get; init; }   // 昨收
    public double CurrentPrice { get; init; } // 现价
    public bool   IsValid { get; init; }
}

// ─────────────────────────────────────────────
// 配置持久化服务
// ─────────────────────────────────────────────
internal sealed class ConfigService
{
    private static readonly string ConfigPath =
        Path.Combine(AppContext.BaseDirectory, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    public AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                Logger.Info("配置文件不存在，使用空配置");
                return new AppConfig();
            }
            var json = File.ReadAllText(ConfigPath, Encoding.UTF8);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? new AppConfig();
            Logger.Info($"配置加载成功 | 股票数: {cfg.Stocks.Count} | 当前股票: {cfg.CurrentStock}");
            return cfg;
        }
        catch (Exception ex)
        {
            Logger.Error("配置加载失败", ex);
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        try
        {
            var json = JsonSerializer.Serialize(config, JsonOpts);
            File.WriteAllText(ConfigPath, json, Encoding.UTF8);
            Logger.Info($"配置保存成功 | 当前股票: {config.CurrentStock}");
        }
        catch (Exception ex)
        {
            Logger.Error("配置保存失败", ex);
        }
    }
}

// ─────────────────────────────────────────────
// 行情拉取服务
// ─────────────────────────────────────────────
internal sealed class StockDataService : IDisposable
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    static StockDataService()
    {
        Http.DefaultRequestHeaders.Add(
            "Referer", "https://finance.sina.com.cn");
        Http.DefaultRequestHeaders.Add(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    /// <summary>拉取单只股票行情并解析为快照</summary>
    public async Task<StockSnapshot> FetchAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            Logger.Warn("股票代码为空，无法拉取");
            return Invalid(code);
        }

        try
        {
            var url = $"http://hq.sinajs.cn/list={code}";
            Logger.Info($"开始拉取行情 | 股票代码: {code} | URL: {url}");

            var raw = await Http.GetStringAsync(url).ConfigureAwait(false);
            Logger.Info($"HTTP 响应成功 | 股票代码: {code} | 响应长度: {raw.Length} 字符");
            Logger.Info($"HTTP 响应内容 | {raw.Substring(0, Math.Min(200, raw.Length))}");

            return Parse(code, raw);
        }
        catch (Exception ex)
        {
            Logger.Error($"拉取行情失败 | 股票代码: {code}", ex);
            return Invalid(code);
        }
    }

    private static StockSnapshot Parse(string code, string raw)
    {
        // 格式：var hq_str_xxxx="data1,data2,...";
        var startIdx = raw.IndexOf('"');
        var endIdx   = raw.LastIndexOf('"');
        if (startIdx < 0 || endIdx <= startIdx)
        {
            Logger.Error($"响应格式错误（无引号）| 股票代码: {code} | 响应: {raw.Substring(0, Math.Min(100, raw.Length))}");
            return Invalid(code);
        }

        var csvPart = raw[(startIdx + 1)..endIdx];
        if (string.IsNullOrWhiteSpace(csvPart))
        {
            Logger.Error($"响应内容为空 | 股票代码: {code}");
            return Invalid(code);
        }

        var fields = csvPart.Split(',');
        Logger.Info($"CSV 字段数量: {fields.Length} | 股票代码: {code}");

        if (fields.Length < 32)
        {
            Logger.Error($"CSV 字段不足 32 个（实际 {fields.Length}）| 股票代码: {code} | 内容: {csvPart.Substring(0, Math.Min(100, csvPart.Length))}");
            return Invalid(code);
        }

        if (!double.TryParse(fields[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var basePrice) || basePrice <= 0)
        {
            Logger.Error($"昨收价格解析失败或 ≤0 | 股票代码: {code} | fields[2]: {fields[2]}");
            return Invalid(code);
        }

        if (!double.TryParse(fields[3], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var currentPrice))
        {
            Logger.Error($"当前价格解析失败 | 股票代码: {code} | fields[3]: {fields[3]}");
            return Invalid(code);
        }

        Logger.Info($"行情解析成功 | 股票: {fields[0].Trim()} ({code}) | 昨收: {basePrice:F2} | 现价: {currentPrice:F2}");

        return new StockSnapshot
        {
            Name         = fields[0].Trim(),
            Code         = code,
            BasePrice    = basePrice,
            CurrentPrice = currentPrice,
            IsValid      = true
        };
    }

    private static StockSnapshot Invalid(string code) =>
        new() { Code = code, IsValid = false };

    public void Dispose() => Http.Dispose();
}

// ─────────────────────────────────────────────
// 图标渲染服务（GDI+，严格内存管理）
// ─────────────────────────────────────────────
internal static class IconRenderer
{
    private const int IconSize = 32;
    private const int Padding  = 2;

    // 科创板 688xxx / 创业板 30xxxx 涨跌幅限制 20%，其余 10%
    private static double GetLimit(string code)
    {
        var lower = code.ToLowerInvariant().TrimStart('s','h','z');
        return lower.StartsWith("688") || lower.StartsWith("30") ? 0.20 : 0.10;
    }

    /// <summary>
    /// 根据历史价格列表与最新快照绘制托盘图标。
    /// 调用方负责将返回的 Icon 赋值给 NotifyIcon，
    /// 并在替换旧图标前调用 DestroyIcon 释放旧句柄。
    /// </summary>
    public static Icon Render(IReadOnlyList<double> history, StockSnapshot? snap)
    {
        using var bmp = new Bitmap(IconSize, IconSize);
        using var g   = Graphics.FromImage(bmp);

        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (snap == null || !snap.IsValid || history.Count == 0)
        {
            DrawErrorState(g);
        }
        else
        {
            DrawChart(g, history, snap);
        }

        // 转为 HICON；调用方负责 DestroyIcon
        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    private static void DrawErrorState(Graphics g)
    {
        var drawW = IconSize - Padding * 2;
        var drawH = IconSize - Padding * 2;

        using var pen = new Pen(Color.Gray, 1.5f);
        // 绘制 "X"
        g.DrawLine(pen, Padding, Padding, Padding + drawW, Padding + drawH);
        g.DrawLine(pen, Padding + drawW, Padding, Padding, Padding + drawH);
    }

    private static void DrawChart(Graphics g, IReadOnlyList<double> history, StockSnapshot snap)
    {
        var drawW = IconSize - Padding * 2;   // 可绘区域宽度
        var drawH = IconSize - Padding * 2;   // 可绘区域高度

        var limit  = GetLimit(snap.Code);
        var pBase  = snap.BasePrice;
        var pMax   = pBase * (1.0 + limit);
        var pMin   = pBase * (1.0 - limit);
        var range  = pMax - pMin;

        // 零轴（昨收线）
        float zeroY = Padding + (float)(drawH * (1.0 - (pBase - pMin) / range));
        using var zeroLinePen = new Pen(Color.FromArgb(80, Color.Gray), 0.5f);
        g.DrawLine(zeroLinePen, Padding, zeroY, Padding + drawW, zeroY);

        // 折线颜色
        var lineColor = snap.CurrentPrice >= snap.BasePrice
            ? Color.Red
            : Color.LimeGreen;

        if (history.Count < 2)
        {
            // 只有一个点，画一个小点
            using var dot = new SolidBrush(lineColor);
            float x = Padding + drawW / 2f;
            float y = CalcY(history[0], pMin, range, Padding, drawH);
            g.FillEllipse(dot, x - 1.5f, y - 1.5f, 3f, 3f);
            return;
        }

        // 构建折线点集
        var pts = new PointF[history.Count];
        float xStep = history.Count > 1
            ? (float)drawW / (history.Count - 1)
            : 0;

        for (int i = 0; i < history.Count; i++)
        {
            float px = Padding + i * xStep;
            float py = CalcY(history[i], pMin, range, Padding, drawH);
            pts[i] = new PointF(px, py);
        }

        using var linePen = new Pen(lineColor, 1.2f);
        g.DrawLines(linePen, pts);
    }

    private static float CalcY(double price, double pMin, double range, int pad, int drawH)
    {
        var ratio = range > 0 ? (price - pMin) / range : 0.5;
        var clamped = Math.Clamp(ratio, 0.0, 1.0);
        return pad + (float)(drawH * (1.0 - clamped));
    }
}

// ─────────────────────────────────────────────
// 悬浮信息窗（左键点击弹出，失焦自动关闭）
// ─────────────────────────────────────────────
internal sealed class FloatInfoForm : Form
{
    public FloatInfoForm(StockSnapshot snap)
    {
        FormBorderStyle = FormBorderStyle.None;
        BackColor       = Color.FromArgb(30, 30, 30);
        ForeColor       = Color.White;
        StartPosition   = FormStartPosition.Manual;
        TopMost         = true;
        ShowInTaskbar   = false;
        AutoSize        = false;

        BuildContent(snap);
        Deactivate += (_, _) => Close();
    }

    private void BuildContent(StockSnapshot snap)
    {
        const int PadX = 12, PadY = 10, LineH = 22;
        int y = PadY;

        // 标题行
        var lblTitle = MakeLabel(
            $"{snap.Name}  ({snap.Code.ToUpper()})",
            new Point(PadX, y), Color.White, 9.5f, true);
        Controls.Add(lblTitle);
        y += LineH + 4;

        // 当前价格行
        var priceColor = snap.CurrentPrice >= snap.BasePrice ? Color.Red : Color.LimeGreen;
        var lblPrice = MakeLabel(
            $"当前价格：{snap.CurrentPrice:F2}",
            new Point(PadX, y), priceColor, 9f);
        Controls.Add(lblPrice);
        y += LineH;

        // 涨跌幅行
        var pctChange = snap.BasePrice > 0
            ? (snap.CurrentPrice - snap.BasePrice) / snap.BasePrice * 100.0
            : 0.0;
        var sign      = pctChange >= 0 ? "+" : string.Empty;
        var lblChange = MakeLabel(
            $"当日涨跌幅：{sign}{pctChange:F2}%",
            new Point(PadX, y), priceColor, 9f);
        Controls.Add(lblChange);
        y += LineH + PadY;

        // 自适应窗口大小
        var maxW = Controls.Cast<Control>()
            .Max(c => c.PreferredSize.Width) + PadX * 2;
        ClientSize = new Size(maxW + 10, y);

        // 放置在鼠标附近（托盘图标上方）
        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var pos    = Cursor.Position;
        int formX  = Math.Min(pos.X, screen.Right  - Width  - 4);
        int formY  = Math.Max(screen.Top, pos.Y - Height - 8);
        Location = new Point(formX, formY);
    }

    private static Label MakeLabel(string text, Point loc, Color color, float size, bool bold = false)
        => new()
        {
            Text      = text,
            Location  = loc,
            ForeColor = color,
            BackColor = Color.Transparent,
            AutoSize  = true,
            Font      = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
        };
}

// ─────────────────────────────────────────────
// 主应用：ApplicationContext
// ─────────────────────────────────────────────
internal sealed class StockTrayApp : ApplicationContext, IDisposable
{
    // ── 服务 ──────────────────────────────────
    private readonly ConfigService   _cfgSvc  = new();
    private readonly StockDataService _dataSvc = new();

    // ── 状态 ──────────────────────────────────
    private AppConfig          _config      = new();
    private StockSnapshot?     _lastSnap;
    private readonly List<double> _priceHistory = new();
    private IntPtr             _prevHIcon   = IntPtr.Zero;  // 上一帧 HICON，待销毁

    // ── UI 组件 ───────────────────────────────
    private readonly NotifyIcon         _notifyIcon;
    private readonly ContextMenuStrip   _contextMenu;
    private readonly ToolStripMenuItem  _menuSwitch;
    private readonly System.Windows.Forms.Timer _timer;

    // ── 常量 ──────────────────────────────────
    private const int TradeIntervalMs    = 5_000;   // 盘中 5s
    private const int OffHourIntervalMs  = 300_000; // 非交易 5min

    public StockTrayApp()
    {
        Logger.Info("========== StockTray 启动 ==========");
        _config = _cfgSvc.Load();

        // ── 上下文菜单 ────────────────────────
        _menuSwitch = new ToolStripMenuItem("切换展示股票");

        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.Add("添加股票代码", null, OnAddStock);
        _contextMenu.Items.Add(_menuSwitch);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("退出", null, (_, _) => ExitApp());

        // ── 托盘图标 ──────────────────────────
        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Text    = "StockTray 加载中…",
            ContextMenuStrip = _contextMenu
        };
        _notifyIcon.MouseClick += OnTrayMouseClick;

        // 初始图标（灰色错误态）
        UpdateTrayIcon(null);

        // ── 定时器 ────────────────────────────
        _timer = new System.Windows.Forms.Timer
        {
            Interval = TradeIntervalMs
        };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();

        RefreshSwitchMenu();

        // 首次立即拉取
        Logger.Info("首次拉取行情");
        _ = TickAsync();
    }

    // ── 交易时间判断 ───────────────────────────
    private static bool IsTradeTime()
    {
        var now = DateTime.Now.TimeOfDay;
        var open    = new TimeSpan(9,  15, 0);
        var close   = new TimeSpan(15, 0,  0);
        return now >= open && now <= close;
    }

    // ── 定时 Tick ─────────────────────────────
    private async Task TickAsync()
    {
        // 调整下次 interval
        var isTradeTime = IsTradeTime();
        _timer.Interval = isTradeTime ? TradeIntervalMs : OffHourIntervalMs;
        Logger.Info($"定时器 Tick | 交易时间: {isTradeTime} | 下次间隔: {_timer.Interval}ms");

        if (string.IsNullOrWhiteSpace(_config.CurrentStock))
        {
            Logger.Warn("当前股票代码为空，跳过拉取");
            UpdateTrayIcon(null);
            return;
        }

        var snap = await _dataSvc.FetchAsync(_config.CurrentStock);
        _lastSnap = snap;

        if (snap.IsValid)
        {
            // 非交易日（数据无效时间段），清空历史
            var tradeDate = GetCurrentTradeDate();
            if (_priceHistory.Count == 0 || !IsSameTradingDay(tradeDate))
            {
                Logger.Info($"交易日切换或首次加载 | 旧日期: {_lastTradeDate} | 新日期: {tradeDate} | 清空历史价格");
                _priceHistory.Clear();
                _lastTradeDate = tradeDate;
            }
            _priceHistory.Add(snap.CurrentPrice);
            Logger.Info($"价格历史更新 | 总点数: {_priceHistory.Count} | 最新价: {snap.CurrentPrice:F2}");
        }
        else
        {
            Logger.Warn($"行情快照无效，跳过价格历史更新 | 股票代码: {_config.CurrentStock}");
        }

        UpdateTrayIcon(snap.IsValid ? snap : null);
    }

    private string _lastTradeDate = string.Empty;

    private static string GetCurrentTradeDate() =>
        DateTime.Now.ToString("yyyy-MM-dd");

    private bool IsSameTradingDay(string date) =>
        _lastTradeDate == date;

    // ── 更新托盘图标（含旧句柄释放） ───────────
    private void UpdateTrayIcon(StockSnapshot? snap)
    {
        var newIcon = IconRenderer.Render(_priceHistory, snap);

        // 释放旧 HICON（防止非托管内存泄漏）
        if (_prevHIcon != IntPtr.Zero)
            NativeMethods.DestroyIcon(_prevHIcon);

        // Icon.FromHandle 不复制句柄，直接保存原句柄用于后续销毁
        _prevHIcon = newIcon.Handle;

        _notifyIcon.Icon = newIcon;

        if (snap is { IsValid: true })
        {
            var pct = snap.BasePrice > 0
                ? (snap.CurrentPrice - snap.BasePrice) / snap.BasePrice * 100.0
                : 0.0;
            var sign = pct >= 0 ? "+" : string.Empty;
            _notifyIcon.Text = $"{snap.Name}  {snap.CurrentPrice:F2}  ({sign}{pct:F2}%)";
        }
        else if (!string.IsNullOrWhiteSpace(_config.CurrentStock))
        {
            _notifyIcon.Text = $"{_config.CurrentStock.ToUpper()} 数据获取失败";
        }
        else
        {
            _notifyIcon.Text = "StockTray — 未配置股票";
        }
    }

    // ── 鼠标事件 ───────────────────────────────
    private void OnTrayMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            ShowFloatInfo();
        // 右键由 ContextMenuStrip 自动处理
    }

    private void ShowFloatInfo()
    {
        if (_lastSnap == null || !_lastSnap.IsValid) return;
        var form = new FloatInfoForm(_lastSnap);
        form.Show();
        form.Activate();
    }

    // ── 添加股票 ───────────────────────────────
    private void OnAddStock(object? sender, EventArgs e)
    {
        using var dlg = new AddStockDialog();
        if (dlg.ShowDialog() != DialogResult.OK) return;

        var code = dlg.StockCode.Trim().ToLower();
        if (string.IsNullOrEmpty(code))
        {
            Logger.Warn("用户输入股票代码为空");
            return;
        }

        Logger.Info($"用户添加股票 | 股票代码: {code}");

        if (!_config.Stocks.Contains(code))
            _config.Stocks.Add(code);

        _config.CurrentStock = code;
        _cfgSvc.Save(_config);

        _priceHistory.Clear();
        _lastTradeDate = string.Empty;

        RefreshSwitchMenu();
        _ = TickAsync();
    }

    // ── 切换子菜单刷新 ─────────────────────────
    private void RefreshSwitchMenu()
    {
        _menuSwitch.DropDownItems.Clear();
        foreach (var code in _config.Stocks)
        {
            var item = new ToolStripMenuItem(code.ToUpper())
            {
                Checked = code == _config.CurrentStock
            };
            var captured = code;
            item.Click += (_, _) => SwitchStock(captured);
            _menuSwitch.DropDownItems.Add(item);
        }
    }

    private void SwitchStock(string code)
    {
        if (_config.CurrentStock == code) return;

        Logger.Info($"用户切换股票 | 旧代码: {_config.CurrentStock} | 新代码: {code}");

        _config.CurrentStock = code;
        _cfgSvc.Save(_config);

        _priceHistory.Clear();
        _lastTradeDate = string.Empty;

        RefreshSwitchMenu();
        _ = TickAsync();
    }

    // ── 退出 ───────────────────────────────────
    private void ExitApp()
    {
        Logger.Info("用户请求退出程序");
        _timer.Stop();
        _notifyIcon.Visible = false;
        Application.Exit();
    }

    // ── IDisposable ────────────────────────────
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _notifyIcon.Dispose();
            _contextMenu.Dispose();
            _dataSvc.Dispose();

            if (_prevHIcon != IntPtr.Zero)
                NativeMethods.DestroyIcon(_prevHIcon);
        }
        base.Dispose(disposing);
    }
}

// ─────────────────────────────────────────────
// 添加股票对话框（轻量原生 WinForms）
// ─────────────────────────────────────────────
internal sealed class AddStockDialog : Form
{
    private readonly TextBox _input;

    public string StockCode => _input.Text.Trim();

    public AddStockDialog()
    {
        Text            = "添加股票代码";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition   = FormStartPosition.CenterScreen;
        MaximizeBox     = false;
        MinimizeBox     = false;
        ClientSize      = new Size(300, 110);
        ShowInTaskbar   = false;

        var lbl = new Label
        {
            Text     = "请输入股票代码（如 sh600519 或 sz000002）：",
            Location = new Point(12, 14),
            AutoSize = true
        };

        _input = new TextBox
        {
            Location = new Point(12, 38),
            Width    = 276,
            Font     = new Font("Segoe UI", 10f)
        };
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { DialogResult = DialogResult.OK; Close(); }
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
        };

        var btnOk = new Button
        {
            Text         = "确定",
            DialogResult = DialogResult.OK,
            Location     = new Point(120, 72),
            Width        = 80
        };
        var btnCancel = new Button
        {
            Text         = "取消",
            DialogResult = DialogResult.Cancel,
            Location     = new Point(208, 72),
            Width        = 80
        };

        Controls.AddRange(new Control[] { lbl, _input, btnOk, btnCancel });
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }
}

// ─────────────────────────────────────────────
// 程序入口
// ─────────────────────────────────────────────
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 捕获未处理的 UI 线程异常
        Application.ThreadException += (_, e) =>
            Logger.Error("未捕获的 UI 线程异常", e.Exception);

        // 捕获未处理的后台线程异常
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.Error($"未捕获的后台异常（IsTerminating={e.IsTerminating}）",
                e.ExceptionObject as Exception);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        Logger.Info($"StockTray 进程启动 | OS: {Environment.OSVersion} | PID: {Environment.ProcessId}");
        Logger.Info($"程序目录: {AppContext.BaseDirectory}");

        try
        {
            using var app = new StockTrayApp();
            Application.Run(app);
        }
        catch (Exception ex)
        {
            Logger.Error("程序异常退出", ex);
            throw;
        }
        finally
        {
            Logger.Info("========== StockTray 退出 ==========");
        }
    }
}
