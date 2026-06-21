using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;


namespace NetBar
{
    public partial class MainWindow : Window
    {
        private CancellationTokenSource? _cts;
        // 左键水平拖动状态
        private bool _isLeftDragging = false;
        private System.Windows.Point _dragStartPoint;
        private double _dragStartLeft = 0.0;
        private int _dragStartCursorX = 0;

        // (taskbar auto-hide / visibility logic removed to avoid flicker when opening the overflow tray)

        private NetworkInterface? _netInterface;
        private long lastDownloadBytes = 0;
        private long lastUploadBytes = 0;
        private DateTime lastUpdateTime = DateTime.Now;

        // 主题控制
        private enum ThemeMode { FollowSystem, Light, Dark }
        private ThemeMode _themeMode = ThemeMode.FollowSystem;
        private bool _themeMonitoringEnabled = false;
        private System.Windows.Controls.MenuItem? _miThemeFollow;
        private System.Windows.Controls.MenuItem? _miThemeLight;
        private System.Windows.Controls.MenuItem? _miThemeDark;
        private System.Windows.Controls.MenuItem? _miAutoStart;

        // 本地配置
        private AppSettings _settings = new AppSettings();
        private string _configPath = string.Empty;
        private bool _initialPositioned = false;

        private class AppSettings
        {
            public bool AutoStart { get; set; } = false;
            public string ThemeMode { get; set; } = "FollowSystem"; // FollowSystem | Light | Dark
            public bool LeftDragMove { get; set; } = true; // 是否允许左键拖动窗口
            public double? SavedLeft { get; set; } = null; // 记录上次水平位置
        }

        // 将窗口放在工作区右下角的安全初始位置
        private void SetInitialPositionInWorkArea()
        {
            try
            {
                // 延迟到布局完成后使用 ActualWidth/ActualHeight 进行一次性定位，避免尺寸变动导致放到屏幕外
                if (_initialPositioned) return;
                this.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        double w = this.ActualWidth > 0 ? this.ActualWidth : this.Width;
                        double h = this.ActualHeight > 0 ? this.ActualHeight : this.Height;

                        // 首先尝试基于任务栏所在显示器的工作区定位
                        Rect waRect = SystemParameters.WorkArea;
                        try
                        {
                            IntPtr shell = FindWindow("Shell_TrayWnd", null);
                            IntPtr mon = IntPtr.Zero;
                            if (shell != IntPtr.Zero)
                                mon = MonitorFromWindow(shell, 2);
                            if (mon == IntPtr.Zero)
                            {
                                var helper = new WindowInteropHelper(this);
                                if (helper.Handle != IntPtr.Zero)
                                    mon = MonitorFromWindow(helper.Handle, 2);
                            }

                            if (mon != IntPtr.Zero)
                            {
                                MONITORINFO mi = new MONITORINFO();
                                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                                if (GetMonitorInfo(mon, ref mi))
                                {
                                    // mi.rcWork 是像素坐标，需转换为 WPF 设备无关单位
                                    var src = PresentationSource.FromVisual(this);
                                    if (src != null)
                                    {
                                        var m = src.CompositionTarget.TransformFromDevice;
                                        var tl = m.Transform(new System.Windows.Point(mi.rcWork.left, mi.rcWork.top));
                                        var br = m.Transform(new System.Windows.Point(mi.rcWork.right, mi.rcWork.bottom));
                                        waRect = new Rect(tl, br);
                                    }
                                }
                            }
                        }
                        catch { /* fallback to SystemParameters.WorkArea */ }

                        double left = waRect.Right - w - 8;
                        double top = waRect.Bottom - h - 8;
                        // 限制在工作区内
                        left = Math.Clamp(left, waRect.Left + 2, waRect.Right - w - 2);
                        top = Math.Clamp(top, waRect.Top + 2, waRect.Bottom - h - 2);
                        this.Left = left;
                        this.Top = top;
                        _initialPositioned = true;
                        LogDebug("SetInitialPositionInWorkArea applied (deferred)");
                    }
                    catch { }
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            catch { }
        }

        // 控制是否启用定期/显式置顶（用于调试/回退）
        private bool _topmostEnabled = false; // 目前禁用以避免与任务栏交互冲突
        // 控制是否允许自动定位到任务栏附近，默认关闭以避免与系统托盘冲突
        private bool _autoPositionEnabled = false;
        private readonly object _positionLock = new object();
        private DateTime _lastPositionTime = DateTime.MinValue;
        // Deactivated 延迟任务控制
        private CancellationTokenSource? _deactivatedCts;
        // 位置调整并发守卫
        private int _positioningGuard = 0;

        // Taskbar API
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public int lParam;
        }

        [DllImport("shell32.dll")]
        private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        private const uint ABM_GETTASKBARPOS = 0x00000005;
        private const uint ABM_GETSTATE = 0x00000004;
        private const int ABS_AUTOHIDE = 0x1;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string lclassName, string? windowTitle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private const int GWL_EXSTYLE = -20;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WS_EX_NOACTIVATE = 0x08000000;

        // 兼容 32/64 位的 Get/SetWindowLongPtr
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;

            this.PreviewMouseRightButtonDown += MainWindow_PreviewMouseRightButtonDown;
            this.MouseMove += new System.Windows.Input.MouseEventHandler(MainWindow_MouseMove);
            this.MouseLeftButtonUp += new System.Windows.Input.MouseButtonEventHandler(MainWindow_MouseLeftButtonUp);
            this.Activated += MainWindow_Activated;
            this.Deactivated += MainWindow_Deactivated;
            // 运行时诊断事件：记录位置、可见性与状态变化
            this.LocationChanged += MainWindow_LocationChanged;
            this.IsVisibleChanged += MainWindow_IsVisibleChanged;
            this.StateChanged += MainWindow_StateChanged;
            this.SizeChanged += MainWindow_SizeChanged;
            // 监控始终运行，窗口右键使用窗口自身菜单。
        }

        private string GetWindowClassName(IntPtr h)
        {
            try
            {
                var sb = new System.Text.StringBuilder(256);
                int r = GetClassName(h, sb, sb.Capacity);
                if (r > 0) return sb.ToString();
            }
            catch { }
            return string.Empty;
        }

        private void MainWindow_LocationChanged(object? sender, EventArgs e)
        {
            try { LogDebug($"LocationChanged: Left={this.Left:F0} Top={this.Top:F0}"); } catch { }
        }

        private void MainWindow_IsVisibleChanged(object? sender, DependencyPropertyChangedEventArgs e)
        {
            try { LogDebug($"IsVisibleChanged: New={this.IsVisible} Visibility={this.Visibility}"); } catch { }
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            try { LogDebug($"StateChanged: WindowState={this.WindowState}"); } catch { }
        }

        private void MainWindow_SizeChanged(object? sender, SizeChangedEventArgs e)
        {
            try { LogDebug($"SizeChanged: W={this.Width:F0} H={this.Height:F0}"); } catch { }
        }

        // CheckVisibilityForFullscreenOrTaskbar() removed to stop window visibility toggling when
        // the user opens the taskbar overflow (hidden icons) which caused flicker.

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 配置文件路径
            _configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetBar", "config.json");
            LoadSettings();

            InitializeNetworkCounters();
            StartMonitoring();
            SetupThemeMonitoring();
            HookContextMenuItems();
            SyncLeftMoveCheck();
            // 初始定位到工作区右下角（避免与任务栏/托盘交互）
            SetInitialPositionInWorkArea();
            try { this.Topmost = true; _topmostEnabled = false; } catch { }
            // 处理 Loaded 后可能的窗口激活问题
            this.Dispatcher.BeginInvoke(new Action(() => this.Activate()));
        }

        private void InitializeNetworkCounters()
        {
            try
            {
                // 使用托管 API 获取首要网络接口，避免 PerformanceCounter 的额外开销
                _netInterface = GetPrimaryNetworkInterface();
                if (_netInterface != null)
                {
                    var stats = _netInterface.GetIPv4Statistics();
                    lastDownloadBytes = (long)stats.BytesReceived;
                    lastUploadBytes = (long)stats.BytesSent;
                    lastUpdateTime = DateTime.Now;
                }
            }
            catch { /* 静默处理 */ }
        }
        private NetworkInterface? GetPrimaryNetworkInterface()
        {
            try
            {
                var ifaces = NetworkInterface.GetAllNetworkInterfaces();
                NetworkInterface? best = null;
                long bestTotal = -1;
                foreach (var ni in ifaces)
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                        continue;
                    var desc = ni.Description ?? string.Empty;
                    var name = ni.Name ?? string.Empty;
                    if (desc.Contains("Virtual") || desc.Contains("VPN") || name.Contains("Loopback"))
                        continue;
                    // 忽略环回和隧道类型
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;
                    try
                    {
                        var stats = ni.GetIPv4Statistics();
                        long total = (long)stats.BytesReceived + (long)stats.BytesSent;
                        if (total > bestTotal)
                        {
                            bestTotal = total;
                            best = ni;
                        }
                    }
                    catch { /* skip problematic interface */ }
                }
                if (best != null)
                {
                    try
                    {
                        var s = best.GetIPv4Statistics();
                    }
                    catch { }
                    return best;
                }
                // 回退到任意一个可用接口
                return ifaces.Length > 0 ? ifaces[0] : null;
            }
            catch { return null; }
        }

        private void StartMonitoring()
        {
            if (_cts != null && !_cts.IsCancellationRequested)
                return; // 已经在运行

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            // 以后台任务方式高频轮询，但通过 UI Dispatcher 更新，避免阻塞 UI
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        if (_netInterface == null)
                        {
                            await Task.Delay(500, token);
                            continue;
                        }

                        var now = DateTime.Now;
                        double intervalSec = (now - lastUpdateTime).TotalSeconds;
                        if (intervalSec < 0.2)
                        {
                            await Task.Delay(100, token);
                            continue;
                        }

                        try
                        {
                            var stats = _netInterface.GetIPv4Statistics();
                            long downloadBytes = (long)stats.BytesReceived;
                            long uploadBytes = (long)stats.BytesSent;

                            double downloadSpeed = (downloadBytes - lastDownloadBytes) / Math.Max(intervalSec, 1e-6) / 1024.0; // KB/s
                            double uploadSpeed = (uploadBytes - lastUploadBytes) / Math.Max(intervalSec, 1e-6) / 1024.0;

                            // 更新 UI
                            this.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                DownloadText.Text = $"↓ {FormatSpeed(downloadSpeed)}";
                                UploadText.Text = $"↑ {FormatSpeed(uploadSpeed)}";
                            }));

                            lastDownloadBytes = downloadBytes;
                            lastUploadBytes = uploadBytes;
                            lastUpdateTime = now;
                        }
                        catch (Exception ex)
                        {
                            LogDebug($"StartMonitoring inner exception: {ex.GetType().Name} {ex.Message}");
                        }
                        await Task.Delay(200, token); // 200ms 实时更新
                    }
                }
                catch (TaskCanceledException) { }
            }, token);
            // Note: taskbar/visibility polling removed to avoid flicker when opening the overflow tray.
        }

        private void StopMonitoring()
        {
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
            }
            catch { }
            finally { _cts = null; }
        }

        private string FormatSpeed(double kb)
        {
            if (kb >= 1024) return $"{kb / 1024:F1} M/s";
            if (kb >= 1) return $"{kb:F1} K/s";
            return "0.0 K/s";
        }

        // 简单诊断日志（写入 %TEMP%\NetBar_debug.log），仅在调试时使用
        private void LogDebug(string message)
        {
            try
            {
                string f = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NetBar_debug.log");
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] PID:{System.Diagnostics.Process.GetCurrentProcess().Id} TID:{System.Threading.Thread.CurrentThread.ManagedThreadId} {message}\r\n";
                System.IO.File.AppendAllText(f, line);
            }
            catch { }
        }



        private void SyncThemeChecks()
        {
            if (_miThemeFollow != null) _miThemeFollow.IsChecked = _themeMode == ThemeMode.FollowSystem;
            if (_miThemeLight != null) _miThemeLight.IsChecked = _themeMode == ThemeMode.Light;
            if (_miThemeDark != null) _miThemeDark.IsChecked = _themeMode == ThemeMode.Dark;
        }

        private void EnsureWindowTopmost()
        {
            try
            {
                if (!_topmostEnabled)
                {
                    LogDebug("EnsureWindowTopmost skipped (disabled)");
                    return;
                }

                // 保持 WPF Topmost 与原生 topmost 一致，避免 z-order 不一致导致被覆盖
                this.Topmost = true;
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                IntPtr h = helper.Handle;
                if (h != IntPtr.Zero)
                {
                    SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
                LogDebug($"EnsureWindowTopmost called: Visible={this.Visibility} Topmost={this.Topmost} Left={this.Left:F0} Top={this.Top:F0}");
            }
            catch { }
        }

        private void MainWindow_Activated(object? sender, EventArgs e)
        {
            try
            {
                // 取消任何延迟恢复操作
                try { _deactivatedCts?.Cancel(); _deactivatedCts = null; } catch { }
                LogDebug("Activated event received");
            }
            catch { }
        }

        private void MainWindow_Deactivated(object? sender, EventArgs e)
        {
            try
            {
                LogDebug("Deactivated event received");
                // 当失去激活且前台为任务栏/托盘交互时，延迟短时间后恢复原生 topmost（不改变可见性）
                try
                {
                    _deactivatedCts?.Cancel();
                    _deactivatedCts = new CancellationTokenSource();
                    var token = _deactivatedCts.Token;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(300, token); // 等待任务栏动画结束
                            if (token.IsCancellationRequested) return;

                            IntPtr fg = GetForegroundWindow();
                            IntPtr shellTray = FindWindow("Shell_TrayWnd", null);
                            bool isTray = false;
                            if (fg != IntPtr.Zero && shellTray != IntPtr.Zero)
                            {
                                if (fg == shellTray) isTray = true;
                                else
                                {
                                    try { if (IsChild(shellTray, fg)) isTray = true; } catch { }
                                }
                            }

                            if (isTray)
                            {
                                // 仅恢复原生 topmost，不改 WPF 可见性/激活，避免闪烁
                                try
                                {
                                    this.Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        try
                                        {
                                            var helper = new WindowInteropHelper(this);
                                            IntPtr h = helper.Handle;
                                            if (h != IntPtr.Zero)
                                            {
                                                SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                                                LogDebug("Deferred SetWindowPos to TOPMOST applied after Deactivated");
                                            }
                                        }
                                        catch { }
                                    }));
                                }
                                catch { }
                            }
                        }
                        catch (TaskCanceledException) { }
                        catch (Exception ex) { LogDebug("Deactivated deferred error: " + ex.Message); }
                    }, token);
                }
                catch { }
            }
            catch { }
        }

        private void SyncLeftMoveCheck()
        {
            var miLeftMove = FindMenuItemByName(RootBorder.ContextMenu?.Items, "MenuLeftMove");
            if (miLeftMove != null) miLeftMove.IsChecked = _settings.LeftDragMove;
            // 不再自动重新定位，避免与任务栏交互冲突
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            StopMonitoring();
            // 使用 NetworkInterface 无需手动释放计数器
            TearDownThemeMonitoring();
            SaveSettings();
        }

        private void SetupThemeMonitoring()
        {
            try
            {
                if (!_themeMonitoringEnabled)
                {
                    SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
                    _themeMonitoringEnabled = true;
                }
                UpdateTheme();
            }
            catch { }
        }

        private void PositionWindowAtTaskbarSide(bool force = false)
        {
            // EMERGENCY: 临时完全禁用自动/强制定位以阻止窗口被移出可见区域
            LogDebug("PositionWindowAtTaskbarSide disabled by emergency stop");
            return;

                if (!_autoPositionEnabled && !force)
                {
                    LogDebug("PositionWindowAtTaskbarSide skipped because autoPosition disabled");
                    return;
                }
                lock (_positionLock)
                {
                    if ((DateTime.UtcNow - _lastPositionTime).TotalMilliseconds < 200)
                    {
                        LogDebug("PositionWindowAtTaskbarSide skipped (debounce)");
                        return;
                    }
                    _lastPositionTime = DateTime.UtcNow;
                }
                LogDebug("PositionWindowAtTaskbarSide start");
                // 如果当前前台窗口是任务栏或其子窗口，跳过定位以避免与托盘交互冲突
                try
                {
                    IntPtr fg = GetForegroundWindow();
                    IntPtr shellTray = FindWindow("Shell_TrayWnd", null);
                    if (fg != IntPtr.Zero && shellTray != IntPtr.Zero)
                    {
                        bool isChildOfTray = false;
                        try { isChildOfTray = IsChild(shellTray, fg); } catch { }
                        if (fg == shellTray || isChildOfTray)
                        {
                            LogDebug("PositionWindowAtTaskbarSide skipped because foreground is taskbar/tray");
                            return;
                        }
                    }
                }
                catch { }
                // Get taskbar position
                APPBARDATA data = new APPBARDATA();
                data.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
                IntPtr result = SHAppBarMessage(ABM_GETTASKBARPOS, ref data);
                if (result == IntPtr.Zero)
                {
                    // fallback: place bottom-right
                    this.Dispatcher.Invoke(new Action(() =>
                    {
                        var wa = SystemParameters.WorkArea;
                        this.Left = wa.Right - this.Width - 8;
                        this.Top = wa.Bottom - this.Height - 8;
                    }));
                    return;
                }

                var rc = data.rc;
                bool isBottom = data.uEdge == 3; // ABE_BOTTOM
                bool isTop = data.uEdge == 1; // ABE_TOP
                bool isLeft = data.uEdge == 0; // ABE_LEFT
                bool isRight = data.uEdge == 2; // ABE_RIGHT

                // try to get tray icon area so we don't overlap system tray icons
                bool haveTray = TryGetTrayNotifyRect(out RECT trayRect);

                // 使用同步调用确保位置立即应用
                this.Dispatcher.Invoke(new Action(() =>
                {
                    int taskbarHeight = Math.Abs(rc.bottom - rc.top);
                    if (taskbarHeight > 0)
                    {
                        this.Height = taskbarHeight;
                    }

                    if (isBottom || isTop)
                    {
                        if (haveTray)
                        {
                            int leftCandidate = (int)Math.Round(trayRect.left - this.Width - 8);
                            if (leftCandidate < rc.left + 8) leftCandidate = rc.left + 8;
                            int maxLeft = (int)Math.Round(rc.right - this.Width - 8);
                            this.Left = Math.Min(maxLeft, leftCandidate);
                        }
                        else
                        {
                            this.Left = rc.right - this.Width - 8;
                        }

                        // place the window inside the taskbar area (vertically aligned with taskbar)
                        try
                        {
                            this.Top = rc.top;
                        }
                        catch { }
                    }
                    else if (isLeft)
                    {
                        if (haveTray)
                        {
                            this.Left = rc.left;
                            this.Top = Math.Max(rc.top, trayRect.top - this.Height - 8);
                        }
                        else
                        {
                            this.Left = rc.left;
                            this.Top = Math.Max(rc.top, rc.bottom - this.Height - 8);
                        }
                    }
                    else if (isRight)
                    {
                        if (haveTray)
                        {
                            this.Left = Math.Max(rc.left, rc.right - this.Width);
                            this.Top = Math.Max(rc.top, trayRect.top - this.Height - 8);
                        }
                        else
                        {
                            this.Left = Math.Max(rc.left, rc.right - this.Width);
                            this.Top = Math.Max(rc.top, rc.bottom - this.Height - 8);
                        }
                    }

                    try
                    {
                        if (_settings.SavedLeft.HasValue)
                        {
                            var wa = SystemParameters.WorkArea;
                            double minLeft = wa.Left + 8;
                            double maxLeft = wa.Right - this.Width - 8;
                            double saved = _settings.SavedLeft.Value;
                            if (saved < minLeft) saved = minLeft;
                            if (saved > maxLeft) saved = maxLeft;
                            if (Math.Abs(this.Left - saved) > 20)
                                this.Left = saved;
                        }
                    }
                    catch { }

                    try
                    {
                        // 确保最终位置在工作区内，避免被放到屏幕外或覆盖任务栏外
                        var wa2 = SystemParameters.WorkArea;
                        this.Left = Math.Clamp(this.Left, wa2.Left + 2, wa2.Right - this.Width - 2);
                        this.Top = Math.Clamp(this.Top, wa2.Top + 2, wa2.Bottom - this.Height - 2);
                    }
                    catch { }
                    LogDebug($"PositionWindowAtTaskbarSide applied: Left={this.Left:F0} Top={this.Top:F0} Width={this.Width:F0} Height={this.Height:F0}");
                }));
        }

        private bool TryGetTrayNotifyRect(out RECT rect)
        {
            rect = new RECT();
            try
            {
                IntPtr shell = FindWindow("Shell_TrayWnd", null);
                if (shell == IntPtr.Zero) return false;

                IntPtr trayNotify = FindWindowEx(shell, IntPtr.Zero, "TrayNotifyWnd", null);
                if (trayNotify == IntPtr.Zero)
                {
                    trayNotify = FindWindowEx(shell, IntPtr.Zero, "TrayClockWClass", null);
                }

                IntPtr sysPager = IntPtr.Zero;
                IntPtr toolbar = IntPtr.Zero;
                if (trayNotify != IntPtr.Zero)
                {
                    sysPager = FindWindowEx(trayNotify, IntPtr.Zero, "SysPager", null);
                    if (sysPager != IntPtr.Zero)
                        toolbar = FindWindowEx(sysPager, IntPtr.Zero, "ToolbarWindow32", null);
                }

                if (toolbar == IntPtr.Zero && trayNotify != IntPtr.Zero)
                    toolbar = FindWindowEx(trayNotify, IntPtr.Zero, "ToolbarWindow32", null);

                if (toolbar != IntPtr.Zero)
                {
                    if (GetWindowRect(toolbar, out rect)) return true;
                }

                if (trayNotify != IntPtr.Zero)
                {
                    if (GetWindowRect(trayNotify, out rect)) return true;
                }

                // fallback to entire taskbar
                if (GetWindowRect(shell, out rect)) return true;
            }
            catch { }
            return false;
        }

        private void LoadSettings()
        {
            try
            {
                var dir = Path.GetDirectoryName(_configPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                if (File.Exists(_configPath))
                {
                    var txt = File.ReadAllText(_configPath);
                    var obj = JsonSerializer.Deserialize<AppSettings>(txt);
                    if (obj != null)
                    {
                        _settings = obj;
                        // 应用设置

                        if (Enum.TryParse<ThemeMode>(_settings.ThemeMode, out var tm))
                        {
                            _themeMode = tm;
                        }
                        // 应用自动启动设置（使用启动文件夹快捷方式）
                        TryApplyAutoStart(_settings.AutoStart);
                    }
                }
                else
                {
                    SaveSettings();
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                _settings.AutoStart = _settings.AutoStart; // keep
                _settings.ThemeMode = _themeMode.ToString();


                var dir = Path.GetDirectoryName(_configPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var txt = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configPath, txt);
            }
            catch { }
        }

        private void TryApplyAutoStart(bool enable)
        {
            try
            {
                SetAutoStartShortcut(enable);
                _settings.AutoStart = enable;
            }
            catch { }
        }
        private void SetAutoStartShortcut(bool enable)
        {
            try
            {
                var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                var lnk = Path.Combine(startup, "NetBar.lnk");
                if (enable)
                {
                    string? exe = Assembly.GetEntryAssembly()?.Location;
                    if (string.IsNullOrEmpty(exe)) exe = Process.GetCurrentProcess().MainModule?.FileName;
                    if (string.IsNullOrEmpty(exe)) return;
                    // Create shortcut via WScript.Shell COM
                    Type? t = Type.GetTypeFromProgID("WScript.Shell");
                    if (t == null) return;
                    dynamic shell = Activator.CreateInstance(t);
                    dynamic shortcut = shell.CreateShortcut(lnk);
                    shortcut.TargetPath = exe;
                    shortcut.WorkingDirectory = Path.GetDirectoryName(exe);
                    shortcut.WindowStyle = 1;
                    shortcut.Save();
                }
                else
                {
                    if (File.Exists(lnk)) File.Delete(lnk);
                }
            }
            catch { }
        }

        private void TearDownThemeMonitoring()
        {
            try
            {
                if (_themeMonitoringEnabled)
                {
                    SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
                    _themeMonitoringEnabled = false;
                }
            }
            catch { }
        }

        private void SystemEvents_UserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.VisualStyle)
                UpdateTheme();
        }

        private void UpdateTheme()
        {
            try
            {
                System.Windows.Media.Brush brush;
                if (_themeMode == ThemeMode.FollowSystem)
                {
                    var val = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
                    int useLight = 1;
                    if (val is int i) useLight = i;
                    else if (val is byte b) useLight = b;
                    else if (val != null) useLight = Convert.ToInt32(val);
                    brush = useLight == 1 ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White;
                }
                else if (_themeMode == ThemeMode.Light)
                {
                    brush = System.Windows.Media.Brushes.Black;
                }
                else
                {
                    brush = System.Windows.Media.Brushes.White;
                }

                this.Dispatcher.BeginInvoke(new Action(() =>
                {
                    DownloadText.Foreground = brush;
                    UploadText.Foreground = brush;
                    // 更新菜单项前景色及选中状态
                    if (_miThemeFollow != null) _miThemeFollow.IsChecked = _themeMode == ThemeMode.FollowSystem;
                    if (_miThemeLight != null) _miThemeLight.IsChecked = _themeMode == ThemeMode.Light;
                    if (_miThemeDark != null) _miThemeDark.IsChecked = _themeMode == ThemeMode.Dark;
                }));
            }
            catch { }
        }

        private void HookContextMenuItems()
        {
            try
            {
                var cm = RootBorder.ContextMenu as System.Windows.Controls.ContextMenu;
                if (cm == null) return;
                _miThemeFollow = FindMenuItemByName(cm.Items, "MenuThemeFollow");
                _miThemeLight = FindMenuItemByName(cm.Items, "MenuThemeLight");
                _miThemeDark = FindMenuItemByName(cm.Items, "MenuThemeDark");
                _miAutoStart = FindMenuItemByName(cm.Items, "MenuAutoStart");
                var miLeftMove = FindMenuItemByName(cm.Items, "MenuLeftMove");
                if (miLeftMove != null) miLeftMove.IsChecked = _settings.LeftDragMove;
                if (_miAutoStart != null)
                {
                    _miAutoStart.IsChecked = _settings.AutoStart;
                }
            }
            catch { }
        }

        private System.Windows.Controls.MenuItem? FindMenuItemByName(System.Windows.Controls.ItemCollection? items, string name)
        {
            if (items == null) return null;
            foreach (var obj in items)
            {
                if (obj is System.Windows.Controls.MenuItem mi)
                {
                    if (mi.Name == name) return mi;
                    var found = FindMenuItemByName(mi.Items, name);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private void SetThemeMode(ThemeMode mode)
        {
            _themeMode = mode;
            if (_themeMode == ThemeMode.FollowSystem)
                SetupThemeMonitoring();
            else
                TearDownThemeMonitoring();
            UpdateTheme();
            // 同步托盘菜单状态
            SyncThemeChecks();
            // 持久化主题设置
            _settings.ThemeMode = _themeMode.ToString();
            SaveSettings();
        }

        private void MenuThemeFollow_Click(object sender, RoutedEventArgs e) => SetThemeMode(ThemeMode.FollowSystem);
        private void MenuThemeLight_Click(object sender, RoutedEventArgs e) => SetThemeMode(ThemeMode.Light);
        private void MenuThemeDark_Click(object sender, RoutedEventArgs e) => SetThemeMode(ThemeMode.Dark);

        private void MenuLeftMove_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool isChecked = false;
                if (sender is System.Windows.Controls.MenuItem mi)
                {
                    isChecked = mi.IsChecked;
                }
                else
                {
                    var miLeft = FindMenuItemByName(RootBorder.ContextMenu?.Items, "MenuLeftMove");
                    if (miLeft != null)
                    {
                        miLeft.IsChecked = !miLeft.IsChecked;
                        isChecked = miLeft.IsChecked;
                    }
                }

                _settings.LeftDragMove = isChecked;
                SaveSettings();
            }
            catch { }
            CloseContextMenuDelayed();
        }

        // ==================== 现有事件 ====================
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (e.LeftButton == MouseButtonState.Pressed && _settings.LeftDragMove)
                {
                    // 开始水平拖动：记录起始屏幕光标 X 与窗口 Left 并捕获鼠标
                    _isLeftDragging = true;
                    _dragStartLeft = this.Left;
                    try
                    {
                        if (GetCursorPos(out POINT p))
                        {
                            _dragStartCursorX = p.X;
                        }
                        else
                        {
                            // fallback to pointer position relative to window
                            var pt = e.GetPosition(this);
                            _dragStartCursorX = (int)(this.Left + pt.X);
                        }
                    }
                    catch
                    {
                        var pt = e.GetPosition(this);
                        _dragStartCursorX = (int)(this.Left + pt.X);
                    }
                    this.CaptureMouse();
                }
            }
            catch { }
        }

        private void MainWindow_MouseMove(object? sender, System.Windows.Input.MouseEventArgs e)
        {
            try
            {
                if (_isLeftDragging && e.LeftButton == MouseButtonState.Pressed)
                {
                    try
                    {
                        if (GetCursorPos(out POINT p))
                        {
                            int delta = p.X - _dragStartCursorX;
                            this.Left = _dragStartLeft + delta;
                        }
                        else
                        {
                            var cur = e.GetPosition(this);
                            double deltaX = cur.X - _dragStartPoint.X;
                            this.Left = _dragStartLeft + deltaX;
                        }
                    }
                    catch
                    {
                        var cur = e.GetPosition(this);
                        double deltaX = cur.X - _dragStartPoint.X;
                        this.Left = _dragStartLeft + deltaX;
                    }
                }
            }
            catch { }
        }

        private void MainWindow_MouseLeftButtonUp(object? sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (_isLeftDragging)
                {
                    _isLeftDragging = false;
                    try { this.ReleaseMouseCapture(); } catch { }
                    // 记录当前位置并持久化
                    _settings.SavedLeft = this.Left;
                    SaveSettings();
                }
            }
            catch { }
        }

        private void MainWindow_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                var cm = RootBorder.ContextMenu as System.Windows.Controls.ContextMenu;
                if (cm != null)
                {
                    cm.PlacementTarget = RootBorder;
                    cm.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                    cm.IsOpen = true;
                    e.Handled = true;
                }
            }
            catch { }
        }

        private void AutoStart_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool isChecked = false;
                if (sender is System.Windows.Controls.MenuItem mi)
                {
                    isChecked = mi.IsChecked;
                }
                else if (_miAutoStart != null)
                {
                    _miAutoStart.IsChecked = !_miAutoStart.IsChecked;
                    isChecked = _miAutoStart.IsChecked;
                }

                TryApplyAutoStart(isChecked);
                SaveSettings();
            }
            catch { }
            CloseContextMenuDelayed();
        }

        private void Exit_Click(object sender, RoutedEventArgs? e)
        {
            System.Windows.Application.Current.Shutdown();
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 重置为默认设置
                _settings.LeftDragMove = true;
                _settings.SavedLeft = null;
                // 取消开机自启
                TryApplyAutoStart(false);
                // 恢复主题为跟随系统（SetThemeMode 会保存设置并更新监控）
                SetThemeMode(ThemeMode.FollowSystem);
                // 持久化剩余设置
                SaveSettings();

                // 更新菜单状态引用并同步
                HookContextMenuItems();
                SyncThemeChecks();
                SyncLeftMoveCheck();

                // 重新定位到默认靠右位置并保持最前
                PositionWindowAtTaskbarSide(force: true);
                EnsureWindowTopmost();
            }
            catch { }
            CloseContextMenuDelayed();
        }

        private void CloseContextMenuDelayed(int ms = 600)
        {
            try
            {
                var cm = RootBorder.ContextMenu;
                if (cm == null) return;
                // ensure it stays open a bit
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(ms); }
                    catch { }
                    try { this.Dispatcher.BeginInvoke(new Action(() => { cm.IsOpen = false; })); }
                    catch { }
                });
            }
            catch { }
        }
    }
}