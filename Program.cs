using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockTray;

// ─────────────────────────────────────────────
// 日志服务（写入 ~/.stockview/log/ 目录下）
// ─────────────────────────────────────────────
internal static class Logger
{
    private static readonly string LogDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".stockview", "log");

    private static readonly string LogPath = Path.Combine(LogDir, "stocktray.log");

    private static readonly object LockObj = new();
    private const long MaxLogSize = 2 * 1024 * 1024; // 2MB 自动滚动

    // 日志开关（默认启用）
    public static bool Enabled { get; set; } = true;

    static Logger()
    {
        // 确保日志目录存在
        try
        {
            if (!Directory.Exists(LogDir))
                Directory.CreateDirectory(LogDir);
        }
        catch
        {
            // 创建目录失败不崩溃
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null)
    {
        var msg = ex == null ? message : $"{message} | Exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
        Write("ERROR", msg);
    }

    private static void Write(string level, string message)
    {
        if (!Enabled) return;  // 日志已禁用，直接返回

        try
        {
            lock (LockObj)
            {
                // 超过 2MB 时滚动存档
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogSize)
                {
                    var archivePath = Path.Combine(LogDir, $"stocktray_{DateTime.Now:yyyyMMdd_HHmmss}.log");
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

    [JsonPropertyName("logEnabled")]
    public bool LogEnabled { get; set; } = true;  // 默认启用日志

    [JsonPropertyName("stockNames")]
    public Dictionary<string, string> StockNames { get; set; } = new();  // 股票代码 → 名称映射
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
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".stockview");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    static ConfigService()
    {
        // 确保配置目录存在
        try
        {
            if (!Directory.Exists(ConfigDir))
                Directory.CreateDirectory(ConfigDir);
        }
        catch
        {
            // 创建目录失败不崩溃
        }
    }

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

            // 同步日志开关状态到 Logger
            Logger.Enabled = cfg.LogEnabled;

            Logger.Info($"配置加载成功 | 股票数: {cfg.Stocks.Count} | 当前股票: {cfg.CurrentStock} | 日志: {(cfg.LogEnabled ? "启用" : "禁用")}");
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
        // 注册 GB2312/GBK 编码提供程序（.NET Core 默认不包含）
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

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

            // 使用 GetByteArrayAsync + GB2312 解码，避免 ContentType charset 无效异常
            var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
            var raw = Encoding.GetEncoding("GB2312").GetString(bytes);

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
    private const int YAxisWidth = 4;  // Y 轴宽度（含刻度线）

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
        var drawW = IconSize - Padding * 2 - YAxisWidth;   // 减去 Y 轴宽度
        var drawH = IconSize - Padding * 2;                // 可绘区域高度
        var chartLeft = Padding + YAxisWidth;              // 图表起始 X 坐标

        var pBase  = snap.BasePrice;

        // 动态 Y 轴：基于当前历史价格自动缩放，使小波动清晰可见
        var dataMax   = history.Max();
        var dataMin   = history.Min();
        var dataRange = dataMax - dataMin;

        // buffer：取振幅 20% 与昨收价 0.3% 中的较大值，防止折线贴边
        var buffer = Math.Max(dataRange * 0.2, pBase * 0.003);

        // 确保昨收线（零轴）始终落在可见区域内
        var pMax  = Math.Max(dataMax + buffer, pBase + buffer);
        var pMin  = Math.Min(dataMin - buffer, pBase - buffer);
        var range = pMax - pMin;

        // 零轴（昨收线）Y 坐标
        float zeroY = Padding + (float)(drawH * (1.0 - (pBase - pMin) / range));

        // 绘制 Y 轴（左侧竖线 + 三条刻度线）
        using (var axisLinePen = new Pen(Color.FromArgb(100, Color.Gray), 1f))
        {
            // Y 轴竖线
            g.DrawLine(axisLinePen, Padding + YAxisWidth - 1, Padding, Padding + YAxisWidth - 1, Padding + drawH);

            // 上涨停刻度（顶部）
            g.DrawLine(axisLinePen, Padding, Padding, Padding + 2, Padding);

            // 零轴刻度（中心）
            g.DrawLine(axisLinePen, Padding, zeroY, Padding + 2, zeroY);

            // 跌停刻度（底部）
            g.DrawLine(axisLinePen, Padding, Padding + drawH, Padding + 2, Padding + drawH);
        }

        // 零轴参考线（从 Y 轴延伸到图表右侧）
        using var zeroLinePen = new Pen(Color.FromArgb(80, Color.Gray), 0.5f);
        g.DrawLine(zeroLinePen, chartLeft, zeroY, Padding + YAxisWidth + drawW, zeroY);

        // 折线颜色
        var lineColor = snap.CurrentPrice >= snap.BasePrice
            ? Color.Red
            : Color.LimeGreen;

        if (history.Count < 2)
        {
            // 只有一个点，画一个小点
            using var dot = new SolidBrush(lineColor);
            float x = chartLeft + drawW / 2f;
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
            float px = chartLeft + i * xStep;
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
    public FloatInfoForm(StockSnapshot snap, IReadOnlyList<double> priceHistory)
    {
        FormBorderStyle = FormBorderStyle.None;
        BackColor       = Color.FromArgb(30, 30, 30);
        ForeColor       = Color.White;
        StartPosition   = FormStartPosition.Manual;
        TopMost         = true;
        ShowInTaskbar   = false;
        AutoSize        = false;

        BuildContent(snap, priceHistory);
        Deactivate += (_, _) => Close();
    }

    private void BuildContent(StockSnapshot snap, IReadOnlyList<double> priceHistory)
    {
        const int PadX = 12, PadY = 10, LineH = 22;
        const int ChartWidth = 320, ChartHeight = 180;  // 更大的图表尺寸
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
        y += LineH + 8;

        // 绘制分时图（如果有历史数据）
        if (priceHistory.Count > 0)
        {
            var chartPanel = new PictureBox
            {
                Location = new Point(PadX, y),
                Size = new Size(ChartWidth, ChartHeight),
                BackColor = Color.FromArgb(20, 20, 20),
                BorderStyle = BorderStyle.FixedSingle
            };

            var chartBitmap = RenderLargeChart(ChartWidth, ChartHeight, priceHistory, snap);
            chartPanel.Image = chartBitmap;

            Controls.Add(chartPanel);
            y += ChartHeight + PadY;
        }

        // 自适应窗口大小
        ClientSize = new Size(ChartWidth + PadX * 2, y);

        // 放置在鼠标附近（托盘图标上方）
        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var pos    = Cursor.Position;
        int formX  = Math.Min(pos.X, screen.Right  - Width  - 4);
        int formY  = Math.Max(screen.Top, pos.Y - Height - 8);
        Location = new Point(formX, formY);
    }

    private static Bitmap RenderLargeChart(int width, int height, IReadOnlyList<double> history, StockSnapshot snap)
    {
        var bmp = new Bitmap(width, height);
        using var g = Graphics.FromImage(bmp);

        g.Clear(Color.FromArgb(20, 20, 20));
        g.SmoothingMode = SmoothingMode.AntiAlias;

        const int PadX = 10, PadY = 10;
        const int YAxisWidth = 8;
        var drawW = width - PadX * 2 - YAxisWidth;
        var drawH = height - PadY * 2;
        var chartLeft = PadX + YAxisWidth;

        var pBase = snap.BasePrice;

        // 动态 Y 轴：基于当前历史价格自动缩放，使小波动清晰可见
        var dataMax   = history.Count > 0 ? history.Max() : pBase;
        var dataMin   = history.Count > 0 ? history.Min() : pBase;
        var dataRange = dataMax - dataMin;

        // buffer：取振幅 20% 与昨收价 0.3% 中的较大值，防止折线贴边
        var buffer = Math.Max(dataRange * 0.2, pBase * 0.003);

        // 确保昨收线（零轴）始终落在可见区域内
        var pMax = Math.Max(dataMax + buffer, pBase + buffer);
        var pMin = Math.Min(dataMin - buffer, pBase - buffer);
        var range = pMax - pMin;

        // 零轴 Y 坐标
        float zeroY = PadY + (float)(drawH * (1.0 - (pBase - pMin) / range));

        // 绘制 Y 轴
        using (var axisLinePen = new Pen(Color.FromArgb(80, Color.Gray), 1f))
        {
            // Y 轴竖线
            g.DrawLine(axisLinePen, PadX + YAxisWidth - 1, PadY, PadX + YAxisWidth - 1, PadY + drawH);

            // 上涨停刻度
            g.DrawLine(axisLinePen, PadX, PadY, PadX + 6, PadY);

            // 零轴刻度
            g.DrawLine(axisLinePen, PadX, zeroY, PadX + 6, zeroY);

            // 跌停刻度
            g.DrawLine(axisLinePen, PadX, PadY + drawH, PadX + 6, PadY + drawH);
        }

        // 零轴参考线
        using (var zeroLinePen = new Pen(Color.FromArgb(60, Color.Gray), 1f))
        {
            zeroLinePen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
            g.DrawLine(zeroLinePen, chartLeft, zeroY, PadX + YAxisWidth + drawW, zeroY);
        }

        // 绘制价格标签（Y 轴右侧）
        using var font = new Font("Segoe UI", 7f);
        using var textBrush = new SolidBrush(Color.FromArgb(150, Color.Gray));
        g.DrawString($"{pMax:F2}", font, textBrush, chartLeft + 2, PadY - 2);
        g.DrawString($"{pBase:F2}", font, textBrush, chartLeft + 2, zeroY - 8);
        g.DrawString($"{pMin:F2}", font, textBrush, chartLeft + 2, PadY + drawH - 12);

        if (history.Count == 0) return bmp;

        // 折线颜色
        var lineColor = snap.CurrentPrice >= snap.BasePrice
            ? Color.Red
            : Color.LimeGreen;

        if (history.Count == 1)
        {
            using var dot = new SolidBrush(lineColor);
            float x = chartLeft + drawW / 2f;
            float y = CalcY(history[0], pMin, range, PadY, drawH);
            g.FillEllipse(dot, x - 2f, y - 2f, 4f, 4f);
            return bmp;
        }

        // 构建折线点集
        var pts = new PointF[history.Count];
        float xStep = (float)drawW / (history.Count - 1);

        for (int i = 0; i < history.Count; i++)
        {
            float px = chartLeft + i * xStep;
            float py = CalcY(history[i], pMin, range, PadY, drawH);
            pts[i] = new PointF(px, py);
        }

        using var linePen = new Pen(lineColor, 2f);
        g.DrawLines(linePen, pts);

        return bmp;
    }

    private static float CalcY(double price, double pMin, double range, int pad, int drawH)
    {
        var ratio = range > 0 ? (price - pMin) / range : 0.5;
        var clamped = Math.Clamp(ratio, 0.0, 1.0);
        return pad + (float)(drawH * (1.0 - clamped));
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
    private readonly ToolStripMenuItem  _menuLog;  // 日志开关菜单项
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _menuRefreshTimer;  // 菜单刷新定时器（30分钟）

    // ── 菜单数据缓存 ──────────────────────────
    private readonly Dictionary<string, StockSnapshot> _menuStockCache = new();  // 菜单显示用的股票快照缓存

    // ── 常量 ──────────────────────────────────
    private const int TradeIntervalMs    = 5_000;   // 盘中 5s
    private const int OffHourIntervalMs  = 300_000; // 非交易 5min

    public StockTrayApp()
    {
        Logger.Info("========== StockTray 启动 ==========");
        _config = _cfgSvc.Load();

        // ── 上下文菜单 ────────────────────────
        _menuSwitch = new ToolStripMenuItem("切换展示股票");
        _menuLog = new ToolStripMenuItem("输出运行日志")
        {
            Checked = _config.LogEnabled,
            CheckOnClick = true
        };
        _menuLog.Click += OnToggleLog;

        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.Add("添加股票代码", null, OnAddStock);
        _contextMenu.Items.Add(_menuSwitch);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(_menuLog);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("退出", null, (_, _) => ExitApp());

        // 菜单打开时刷新列表
        _contextMenu.Opening += (_, _) => RefreshSwitchMenu();

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

        // ── 主定时器 ──────────────────────────
        _timer = new System.Windows.Forms.Timer
        {
            Interval = TradeIntervalMs
        };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();

        // ── 菜单刷新定时器（30分钟） ───────────
        _menuRefreshTimer = new System.Windows.Forms.Timer
        {
            Interval = 30 * 60 * 1000  // 30分钟
        };
        _menuRefreshTimer.Tick += async (_, _) => await RefreshAllStocksAsync();
        _menuRefreshTimer.Start();

        RefreshSwitchMenu();

        // 首次立即拉取
        Logger.Info("首次拉取行情");
        _ = TickAsync();

        // 首次异步刷新所有股票状态
        _ = RefreshAllStocksAsync();
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
            // 更新股票名称并持久化
            if (!string.IsNullOrWhiteSpace(snap.Name) &&
                (!_config.StockNames.TryGetValue(snap.Code, out var cachedName) || cachedName != snap.Name))
            {
                _config.StockNames[snap.Code] = snap.Name;
                _cfgSvc.Save(_config);
                Logger.Info($"股票名称更新并保存 | {snap.Code} → {snap.Name}");
            }

            // 非交易日（数据无效时间段），清空历史
            var tradeDate = GetCurrentTradeDate();
            if (_priceHistory.Count == 0 || !IsSameTradingDay(tradeDate))
            {
                Logger.Info($"交易日切换或首次加载 | 旧日期: {_lastTradeDate} | 新日期: {tradeDate} | 清空历史价格");
                _priceHistory.Clear();
                _lastTradeDate = tradeDate;
            }
            _priceHistory.Add(snap.CurrentPrice);

            // 🔥 固定窗口：只保留最近 30 个点
            const int MaxPoints = 30;
            if (_priceHistory.Count > MaxPoints)
            {
                _priceHistory.RemoveAt(0);
                Logger.Info($"价格历史超过 {MaxPoints} 点，移除最早数据");
            }

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
        var form = new FloatInfoForm(_lastSnap, _priceHistory);
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

        // 如果没有股票，显示提示项
        if (_config.Stocks.Count == 0)
        {
            var emptyItem = new ToolStripMenuItem("（暂无股票）")
            {
                Enabled = false
            };
            _menuSwitch.DropDownItems.Add(emptyItem);
            return;
        }

        foreach (var code in _config.Stocks)
        {
            // 从配置读取名称
            var stockName = _config.StockNames.TryGetValue(code, out var name) ? name : code.ToUpper();

            // 从缓存读取实时数据
            string displayText;
            Color textColor = Color.White;

            if (_menuStockCache.TryGetValue(code, out var snap) && snap.IsValid)
            {
                var pctChange = snap.BasePrice > 0
                    ? (snap.CurrentPrice - snap.BasePrice) / snap.BasePrice * 100.0
                    : 0.0;
                var sign = pctChange >= 0 ? "+" : string.Empty;

                // 格式：贵州茅台 (SH600519)  1701.50  +0.80%
                displayText = $"{stockName} ({code.ToUpper()})  {snap.CurrentPrice:F2}  {sign}{pctChange:F2}%";
                textColor = snap.CurrentPrice >= snap.BasePrice ? Color.Red : Color.LimeGreen;
            }
            else
            {
                // 无缓存数据，仅显示名称
                displayText = $"{stockName} ({code.ToUpper()})";
            }

            var item = new ToolStripMenuItem(displayText)
            {
                Checked = code == _config.CurrentStock,
                ForeColor = textColor,
                Tag = code
            };

            var captured = code;
            item.Click += (_, _) => SwitchStock(captured);

            // 右键删除功能
            item.MouseUp += (sender, e) =>
            {
                if (e.Button == MouseButtons.Right && sender is ToolStripMenuItem menuItem)
                {
                    var targetCode = menuItem.Tag as string;
                    if (!string.IsNullOrEmpty(targetCode))
                    {
                        DeleteStock(targetCode);
                    }
                }
            };

            _menuSwitch.DropDownItems.Add(item);
        }
    }

    // ── 后台刷新所有股票状态（30分钟一次） ─────
    private async Task RefreshAllStocksAsync()
    {
        if (_config.Stocks.Count == 0)
        {
            Logger.Info("股票列表为空，跳过菜单刷新");
            return;
        }

        Logger.Info($"开始后台刷新所有股票状态 | 总数: {_config.Stocks.Count}");

        foreach (var code in _config.Stocks)
        {
            try
            {
                var snap = await _dataSvc.FetchAsync(code);

                if (snap.IsValid)
                {
                    _menuStockCache[code] = snap;

                    // 更新名称缓存
                    if (!string.IsNullOrWhiteSpace(snap.Name) &&
                        (!_config.StockNames.TryGetValue(code, out var cachedName) || cachedName != snap.Name))
                    {
                        _config.StockNames[code] = snap.Name;
                        Logger.Info($"股票名称更新 | {code} → {snap.Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"刷新股票失败 | {code} | {ex.Message}");
            }

            // 每只股票请求间隔 3 秒，防止被封
            await Task.Delay(3000);
        }

        // 批量保存名称更新
        _cfgSvc.Save(_config);
        Logger.Info($"后台刷新完成 | 缓存股票数: {_menuStockCache.Count}");
    }

    // ── 删除股票 ───────────────────────────────
    private void DeleteStock(string code)
    {
        var stockName = _config.StockNames.TryGetValue(code, out var name) ? name : code.ToUpper();

        var result = MessageBox.Show(
            $"确定要删除股票 {stockName} ({code.ToUpper()}) 吗？",
            "删除确认",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (result != DialogResult.Yes) return;

        Logger.Info($"用户删除股票 | {code}");

        _config.Stocks.Remove(code);
        _config.StockNames.Remove(code);
        _menuStockCache.Remove(code);

        // 如果删除的是当前股票，切换到第一个
        if (_config.CurrentStock == code)
        {
            _config.CurrentStock = _config.Stocks.Count > 0 ? _config.Stocks[0] : string.Empty;
            _priceHistory.Clear();
            _lastTradeDate = string.Empty;
        }

        _cfgSvc.Save(_config);
        RefreshSwitchMenu();

        // 如果删除后当前股票变了，立即拉取
        if (_config.CurrentStock != code)
        {
            _ = TickAsync();
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

    // ── 日志开关切换 ───────────────────────────
    private void OnToggleLog(object? sender, EventArgs e)
    {
        _config.LogEnabled = _menuLog.Checked;
        Logger.Enabled = _config.LogEnabled;

        // 必须先更新 Logger.Enabled，再调用 Save（否则 Save 内的日志不会输出）
        Logger.Info($"用户切换日志状态 | 新状态: {(_config.LogEnabled ? "启用" : "禁用")}");

        _cfgSvc.Save(_config);
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
            _menuRefreshTimer.Dispose();
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
