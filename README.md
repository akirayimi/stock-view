# StockTray - Windows 11 任务栏股票盯盘微端

一个运行在 Windows 11 系统托盘的轻量级实时股票盯盘软件，通过动态绘制托盘图标展示股票分时 K 线折线图。

---

## ✨ 核心功能

### 📊 实时图表渲染
- **32×32 像素**托盘图标动态绘制分时折线图
- 以**昨日收盘价**为零轴（几何中心）
- 自动适配科创板（688）/ 创业板（30）20% 涨跌幅限制
- 涨跌颜色：红色（上涨）/ 绿色（下跌）
- **Y 轴标尺**：左侧显示涨停/昨收/跌停三条刻度线
- **固定窗口**：只保留最近 **30 个数据点**（约 2.5 分钟走势），保证图表清晰可读
- **左键点击**：弹出 320×180 像素的大图，清晰展示完整分时走势

### ⏱️ 智能刷新策略
- **盘中时间**（09:15 - 15:00）：每 **5 秒**轮询一次
- **非交易时间**：降低至每 **5 分钟**一次
- 网络容错：断网时显示灰色"X"提示，不会崩溃

### 🖱️ 鼠标交互
- **左键单击**：弹出悬浮信息窗，显示股票名称、当前价格、涨跌幅
- **右键单击**：打开上下文菜单
  - 添加股票代码
  - 切换展示股票（多股票列表切换）
  - **输出运行日志**（✓ 启用 / 禁用）
  - 退出程序

### 💾 数据持久化
- 使用 `config.json` 保存股票列表与当前选中的股票
- 程序重启自动恢复上次状态

---

## 🛠️ 技术栈

- **开发语言**：C#
- **目标框架**：.NET 8.0 (Windows Forms)
- **核心组件**：
  - `System.Windows.Forms.NotifyIcon` - 托盘图标
  - `System.Drawing` (GDI+) - 图形绘制
  - `System.Text.Json` - 配置持久化
  - `System.Text.Encoding.CodePages` - GB2312 编码支持
  - Win32 API `DestroyIcon` - 非托管资源管理

---

## 📝 日志功能

### 日志存储位置
- **Windows**：`C:\Users\<用户名>\.stockview\log\stocktray.log`
- **跨平台兼容**：使用用户主目录 `~/.stockview/log/` 存储日志

### 控制日志输出
- **右键菜单** → **输出运行日志**（勾选启用，取消禁用）
- 设置会自动保存到 `~/.stockview/config.json`，下次启动自动恢复
- **建议**：排查问题时启用日志；日常使用可禁用以减少磁盘写入

### 自动记录运行日志
程序会在 `~/.stockview/log/` 目录下自动生成日志文件，记录以下信息：

- **启动 / 退出**：进程 PID、操作系统版本、程序目录
- **HTTP 请求**：请求 URL、响应长度、响应内容（前 200 字符）
- **数据解析**：字段数量、价格解析结果、错误原因
- **配置操作**：股票添加、切换、配置文件读写
- **异常捕获**：网络超时、解析失败、未处理异常（含完整堆栈）

### 日志自动滚动
- 当 `stocktray.log` 超过 **2MB** 时，自动重命名为 `stocktray_20260709_161500.log`
- 重新创建空白日志文件继续写入，防止日志文件过大

### 故障排查
遇到"数据获取失败"时，启用日志后重启程序，打开 `~/.stockview/log/stocktray.log` 查看最后的 `[ERROR]` 日志行，定位具体失败原因。

---

## 📦 构建与发布

### 前置要求
1. 安装 [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
2. Windows 10/11 操作系统

### 本地开发构建
```powershell
# 克隆或进入项目目录
cd C:\Users\akira\project\stock-view

# 调试运行
dotnet run

# Release 编译
dotnet build --configuration Release
```

### 打包为独立单文件（推荐部署方式）
```powershell
# 打包为 Self-contained Single File（包含 .NET 运行时，无需目标机器安装 SDK）
dotnet publish `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output ./publish `
  /p:PublishSingleFile=true `
  /p:IncludeNativeLibrariesForSelfExtract=true

# 输出位置：./publish/StockTray.exe
```

### 打包参数说明
- `--runtime win-x64`：目标 64 位 Windows 系统
- `--self-contained true`：包含 .NET 运行时，无需安装框架
- `/p:PublishSingleFile=true`：打包为单个 `.exe` 文件
- `/p:IncludeNativeLibrariesForSelfExtract=true`：嵌入原生依赖库

---

## 🚀 使用指南

### 首次启动
1. 运行 `StockTray.exe`
2. 托盘区域出现图标（初始为灰色 "X"）
3. **右键** → **添加股票代码**
4. 输入股票代码（如 `sh600519` 茅台、`sz000002` 万科）
5. 点击确定，程序自动开始拉取行情并绘制图表

### 股票代码格式
- **上海交易所**：`sh` + 6位代码（如 `sh600519`）
- **深圳交易所**：`sz` + 6位代码（如 `sz000002`）
- **科创板**：`sh688xxx`（自动适配 20% 涨跌幅）
- **创业板**：`sz30xxxx`（自动适配 20% 涨跌幅）

### 切换股票
1. **右键** → **切换展示股票**
2. 子菜单显示所有已添加的股票列表（格式：**股票名称 (代码)**）
3. 点击目标股票即可切换（当前股票会显示 ✓ 标记）
4. 股票名称会自动从接口获取并保存到 `config.json`，下次启动直接读取，无延迟

### 查看详细信息
- **左键单击**托盘图标
- 弹出悬浮窗显示：
  - 股票名称与代码
  - 当前价格（涨跌颜色标记）
  - 当日涨跌幅百分比
  - **320×180 像素分时大图**（含 Y 轴价格标签，最近 30 个数据点）
- 点击窗口外任意位置自动关闭

---

## 🔧 配置文件

### 配置存储位置
- **Windows**：`C:\Users\<用户名>\.stockview\config.json`
- **跨平台兼容**：使用用户主目录 `~/.stockview/config.json` 存储配置

程序首次运行后会自动创建目录和配置文件：

```json
{
  "stocks": [
    "sh600519",
    "sz000002"
  ],
  "currentStock": "sh600519",
  "logEnabled": true,
  "stockNames": {
    "sh600519": "贵州茅台",
    "sz000002": "万科A"
  }
}
```

- `stocks`：历史添加过的所有股票代码列表
- `currentStock`：当前正在监控的股票代码
- `logEnabled`：日志开关（`true` 启用，`false` 禁用）
- `stockNames`：股票代码与名称的映射（自动从接口获取并持久化）

> **注意**：
> - 直接编辑此文件需确保 JSON 格式正确，否则程序会忽略并重置为空配置
> - `stockNames` 会在首次拉取行情后自动填充，无需手动编辑
> - 配置与日志独立于 exe 文件，可在任意位置运行程序

---

## 📐 技术实现细节

### GDI+ 内存管理（关键）
每次重绘托盘图标时，必须显式释放旧的 `HICON` 句柄，避免非托管资源泄漏：

```csharp
// 释放旧图标句柄
if (_prevHIcon != IntPtr.Zero)
    NativeMethods.DestroyIcon(_prevHIcon);

// 创建新图标并保存句柄
var newIcon = IconRenderer.Render(_priceHistory, snap);
_prevHIcon = newIcon.Handle;
_notifyIcon.Icon = newIcon;
```

所有 `Bitmap`、`Graphics`、`Pen` 对象都在 `using` 块中自动释放。

### Y 轴归一化算法
以昨日收盘价为零轴中心，上下限按涨跌幅限制动态计算：

```
P_max = P_base × (1 + limit)    // limit = 0.10 或 0.20
P_min = P_base × (1 - limit)

Pixel_Y = Padding + height × (1 - (P_current - P_min) / (P_max - P_min))
```

屏幕坐标系左上角为 `(0, 0)`，故需用 `1 - ratio` 反转纵坐标。

### 数据接口
- **API**：新浪财经行情接口 `http://hq.sinajs.cn/list=[股票代码]`
- **必要 Header**：`Referer: https://finance.sina.com.cn`（否则返回 403）
- **响应格式**：CSV 字符串，通过 `,` 分割后取关键字段：
  - `[0]`：股票名称
  - `[2]`：昨日收盘价
  - `[3]`：当前最新价
  - `[30]`：行情日期
  - `[31]`：行情时间

---

## 🐛 故障排查

### 托盘图标显示灰色 "X"
- **原因**：网络异常、股票代码错误、接口被限流、字符编码解析失败
- **解决**：
  1. 检查网络连接
  2. 确认股票代码格式正确（如 `sh600519`）
  3. 查看同目录下 `stocktray.log` 文件，搜索最后的 `[ERROR]` 行定位具体原因
  4. 等待程序自动重试（非交易时间会降低请求频率）

### 常见错误码解读

**`InvalidOperationException: The character set provided in ContentType is invalid`**  
- **原因**：新浪接口返回 GB2312 编码，.NET 默认不支持
- **状态**：已修复（v1.1.0+），通过 `System.Text.Encoding.CodePages` 包支持 GB2312

**`响应内容为空 | 股票代码: xxxxx`**  
- **原因**：股票代码不存在或格式错误
- **解决**：检查代码格式（上海 `sh` + 6 位，深圳 `sz` + 6 位）

**`TaskCanceledException: The request was canceled due to the configured HttpClient.Timeout`**  
- **原因**：网络超时（8 秒未响应）
- **解决**：检查网络连接，程序会自动重试

### 程序无法启动
- **原因**：目标机器未安装 .NET 8.0 运行时
- **解决**：
  - 使用 `--self-contained true` 打包的单文件版本（已包含运行时）
  - 或手动安装 [.NET 8.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

### 悬浮窗无法显示
- **原因**：当前股票数据拉取失败（`IsValid = false`）
- **解决**：右键切换到其他可用股票，或重新添加正确的股票代码

---

## 📋 文件清单

```
stock-view/
├── StockTray.csproj       # 项目配置文件
├── Program.cs             # 主程序源代码（所有逻辑）
├── README.md              # 本文档
├── promot/
│   └── project.md         # 原始需求规格书
└── config.json            # 运行时自动生成的配置文件
```

---

## 🔒 安全与隐私

- **无外部依赖**：仅依赖 .NET 8.0 框架，无第三方 NuGet 包（除 `System.Text.Json`，已内置于 .NET）
- **本地存储**：所有配置存储在本地 `config.json`，不上传任何数据
- **只读接口**：仅调用新浪财经公开行情接口，不涉及账户登录或交易操作

---

## 📄 许可证

本项目为个人学习/内部使用项目，未声明开源协议。如需商业使用或二次分发，请联系作者授权。

---

## 📞 技术支持

遇到问题或有功能建议？请通过以下方式反馈：

- 📧 Email: （添加你的联系方式）
- 💬 Issues: （如托管在 GitHub/GitLab，添加链接）

---

## 🎯 未来计划

- [ ] 支持多股票同时监控（轮播切换图标）
- [ ] 添加 K 线图（日线/周线）查看功能
- [ ] 支持自选股分组管理
- [ ] 添加价格预警通知（弹窗/声音提醒）
- [ ] 支持更多数据源接口（腾讯、东方财富等）

---

**⭐ 如果这个项目对你有帮助，欢迎 Star 支持！**
