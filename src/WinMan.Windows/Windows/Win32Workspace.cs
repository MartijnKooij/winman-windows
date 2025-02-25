﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;

using WinMan.Windows.Utilities;

using WinMan.Windows.DllImports;
using static WinMan.Windows.DllImports.Constants;
using static WinMan.Windows.DllImports.NativeMethods;
using Microsoft.Win32;
using WinMan.Windows.Windows;

namespace WinMan.Windows
{
    public class Win32Workspace : IWorkspace
    {
        public event EventHandler<CursorLocationChangedEventArgs>? CursorLocationChanged;
        public event EventHandler<FocusedWindowChangedEventArgs>? FocusedWindowChanged;
        public event EventHandler<WindowChangedEventArgs>? WindowAdded;
        public event EventHandler<WindowChangedEventArgs>? WindowRemoved;
        public event EventHandler<WindowChangedEventArgs>? WindowDestroyed;
        public event EventHandler<WindowChangedEventArgs>? WindowManaging;
        public event UnhandledExceptionEventHandler? UnhandledException;

        // Time period over which to aggressively dirty-check for changes.
        // This is to accommodate windows which take longer to initialise properly.
        private const long RecentWindowDuration = 200;

        /// <summary>
        /// Timer to watch the whole environment for changes that are not detected as events.
        /// </summary>
        private static readonly nuint IdtTimerWatch = 1;
        /// <summary>
        /// Timer to actively watch recently created windows.
        /// </summary>
        private static readonly nuint IdtRecentTimerWatch = 2;

        private readonly object m_initSyncRoot = new object();

        private IntPtr m_msgWnd;
        private Thread? m_eventLoopThread;
        private Thread? m_processingThread;
        private EventLoop m_processingLoop = new EventLoop();
        private Deleter? m_winEventHook;
        private UIntPtr m_hTimer;
        private UIntPtr m_hTimerRecent;
        private bool m_isShuttingDown = false;
        private TimeSpan m_watchInterval = TimeSpan.FromMilliseconds(200);

        private HashSet<Win32Window> m_visibleWindows = new HashSet<Win32Window>();
        private List<Win32WindowHandle> m_windowList = new List<Win32WindowHandle>();
        private Dictionary<IntPtr, Win32WindowHandle> m_windowSet = new Dictionary<IntPtr, Win32WindowHandle>();
        private Stopwatch m_stopwatch = new Stopwatch();
        private List<(long timestamp, Win32WindowHandle handle)> m_recentWindowList = new List<(long timestamp, Win32WindowHandle handle)>();
        private IntPtr m_hwndFocused = IntPtr.Zero;
        private readonly Atomic<Point> m_cursorLocation = new Atomic<Point>();

        private IVirtualDesktopManager? m_virtualDesktops = null;

        public bool IsOpen => m_eventLoopThread != null;

        public IWindow? FocusedWindow
        {
            get
            {
                CheckOpen();

                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                {
                    return null;
                }

                IntPtr desktopHwnd = GetDesktopWindow();
                while (true)
                {
                    IntPtr parentHwnd = GetParent(new(hwnd));
                    if (parentHwnd == IntPtr.Zero || parentHwnd == desktopHwnd)
                    {
                        return FindWindow(hwnd);
                    }
                    hwnd = parentHwnd;
                }
            }
        }

        public IVirtualDesktopManager VirtualDesktopManager => VirtualDesktopManagerLazy;

        private IVirtualDesktopManager VirtualDesktopManagerLazy
        {
            get
            {
                if (!IsOpen)
                {
                    throw new InvalidOperationException($"Call Open() before accessing {nameof(VirtualDesktopManager)}!");
                }

                IVirtualDesktopManager Init()
                {
                    var osBuild = Environment.OSVersion.Version.Build;
                    var osRev = (int)Registry.LocalMachine.OpenSubKey("Software\\Microsoft\\Windows NT\\CurrentVersion")!.GetValue("UBR")!;
                    if (osBuild < 17661)
                    {
                        return new DummyVirtualDesktopManager(this);
                    }

                    IWin32VirtualDesktopService vds;
                    try
                    {
                        if (osBuild >= 22449)
                        {
                            if (osRev >= 2215)
                            {
                                vds = new Win32VirtualDesktopService22621R2215();
                            }
                            else
                            {
                                vds = new Win32VirtualDesktopService22449();
                            }
                        }
                        else if (osBuild >= 22000)
                        {
                            vds = new Win32VirtualDesktopService22000();
                        }
                        else
                        {
                            vds = new Win32VirtualDesktopService17661();
                        }
                    }
                    catch (InvalidCastException)
                    {
                        return new DummyVirtualDesktopManager(this);
                    }

                    vds = new FaultTolerantWin32VirtualDesktopService(vds);

                    // TODO: Pass HMONITOR
                    return new Win32VirtualDesktopManager(this, vds, IntPtr.Zero);
                }

                if (m_virtualDesktops == null)
                {
                    Interlocked.CompareExchange(ref m_virtualDesktops, Init(), null);
                }

                return m_virtualDesktops;
            }
        }


        private Win32DisplayManager? m_displayManager;

        public IDisplayManager DisplayManager
        {
            get
            {
                if (!IsOpen)
                {
                    throw new InvalidOperationException($"Call Open() before accessing {nameof(DisplayManager)}!");
                }
                return m_displayManager!;
            }
        }

        public Point CursorLocation => m_cursorLocation.Read();

        public TimeSpan WatchInterval
        {
            get => m_watchInterval;
            set
            {
                lock (m_initSyncRoot)
                {
                    if (IsOpen)
                    {
                        throw new InvalidOperationException("Set this value before calling Open()!");
                    }
                    m_watchInterval = value;
                }
            }
        }

        public Win32Workspace()
        {
            m_processingLoop.UnhandledException += OnProcessingLoopException;
        }

        public void Open()
        {
            lock (m_initSyncRoot)
            {
                if (IsOpen)
                {
                    throw new InvalidOperationException("Workspace has already been Open()ed!");
                }

                try
                {
                    m_cursorLocation.Exchange(GetCursorLocation());
                }
                catch (Win32Exception)
                {
                    m_cursorLocation.Exchange(new Point());
                }

                m_displayManager = new Win32DisplayManager(this);

                m_eventLoopThread = new Thread(GetMessageLoop)
                {
                    Name = "Win32Workspace.EventThread"
                };

                m_processingThread = new Thread(m_processingLoop.Run)
                {
                    Name = "Win32Workspace.ProcessingThread"
                };

                m_processingLoop.InvokeAsync(() =>
                {
                    foreach (var window in GetWindowListImpl())
                    {
                        m_windowSet.Add(window.Handle, window);
                        m_windowList.Add(window);

                        if (GetVisibility(window))
                        {
                            lock (m_visibleWindows)
                            {
                                m_visibleWindows.Add(window.WindowObject!);
                            }

                            try
                            {
                                window.WindowObject!.OnAdded();
                            }
                            finally
                            {
                                WindowManaging?.Invoke(this, new WindowChangedEventArgs(window.WindowObject!));
                            }
                        }
                    }
                });

                m_eventLoopThread.Start();
                m_processingThread.Start();
                m_stopwatch.Start();
            }
        }

        public IWindow? FindWindow(IntPtr windowHandle)
        {
            CheckOpen();
            Win32WindowHandle? handle;
            lock (m_windowList)
            {
                handle = m_windowList.FirstOrDefault(x => x.Handle == windowHandle);
            }
            try
            {
                handle?.EnsureWindowObject(this);
                return handle?.WindowObject;
            }
            catch (InvalidWindowReferenceException)
            {
                return null;
            }
        }

        public IWindow? FindWindowFromPoint(Point pt)
        {
            var candidates = new List<IWindow>();
            lock (m_visibleWindows)
            {
                foreach (var window in m_visibleWindows)
                {
                    if (window.Position.Contains(pt))
                    {
                        candidates.Add(window);
                    }
                }
            }

            if (candidates.Count == 0)
            {
                return null;
            }
            else if (candidates.Count == 1)
            {
                return candidates[0];
            }

            var comparer = CreateSnapshotZOrderComparer();
            return candidates.OrderBy(x => x, comparer).First();
        }

        public IReadOnlyList<IWindow> GetSnapshot()
        {
            CheckOpen();
            Win32Window[] windows = GetVisibleWindowList();
            return windows.Where(x => x.IsTopLevelVisible).ToArray();
        }

        public IComparer<IWindow> CreateSnapshotZOrderComparer()
        {
            Dictionary<IntPtr, int> hwndToZOrder = new Dictionary<IntPtr, int>();

            int index = 0;
            bool success = EnumWindows(delegate (HWND hwnd, LPARAM _)
            {
                hwndToZOrder[hwnd] = index++;
                return true;
            }, new LPARAM());

            return Comparer<IWindow>.Create((x, y) =>
            {
                if (hwndToZOrder.TryGetValue(x.Handle, out int zorderX))
                {
                    if (hwndToZOrder.TryGetValue(y.Handle, out int zorderY))
                    {
                        return hwndToZOrder[x.Handle] - hwndToZOrder[y.Handle];
                    }
                    return zorderX;
                }
                return 0;
            });
        }

        public void RefreshConfiguration()
        {
            m_processingLoop.InvokeAsync(() =>
            {
                CheckVirtualDesktops();
                CheckVisibilityChanges();
                OnSettingChange();
                OnDisplayChange();

                foreach (var window in GetVisibleWindowList())
                {
                    window.CheckChanges();
                }
            });
        }

        public IWindow UnsafeCreateFromHandle(IntPtr windowHandle)
        {
            return new Win32Window(this, windowHandle);
        }

        public void Dispose()
        {
            KillTimer(new HWND(), m_hTimer);
            KillTimer(new HWND(), m_hTimerRecent);
            m_isShuttingDown = true;
            m_winEventHook?.Dispose();

            m_eventLoopThread?.Join(1000);
            m_processingLoop.Shutdown();
            m_processingThread?.Join(1000);
        }

        internal IWindow? UnsafeGetWindow(IntPtr hwnd)
        {
            Win32WindowHandle? window;
            lock (m_windowList)
            {
                window = m_windowList.FirstOrDefault(x => x.Handle == hwnd);
            }
            if (window?.WindowObject?.IsTopLevelVisible == true)
            {
                return window.WindowObject;
            }
            return null;
        }

        private void OnProcessingLoopException(object? sender, UnhandledExceptionEventArgs e)
        {
            var ex = (Exception)e.ExceptionObject;
            if (ex is Win32Exception win32Ex && win32Ex.IsInvalidWindowHandleException())
            {
                try
                {
                    CheckVisibilityChanges();
                }
                catch (Exception ex2)
                {
                    OnProcessingLoopException(this, new UnhandledExceptionEventArgs(ex2, false));
                }
                return;
            }
            else if (UnhandledException != null)
            {
                UnhandledException(this, new UnhandledExceptionEventArgs(ex, false));
                return;
            }
            else
            {
                ExceptionDispatchInfo.Throw(ex);
            }
        }

        private void GetMessageLoop()
        {
            unsafe
            {
                fixed (char* lpClassName = "STATIC".ToCharArray(),
                    lpWindowName = "WinManMessageReceiver".ToCharArray())
                {
                    m_msgWnd = CreateWindowEx(
                        WINDOWS_EX_STYLE.WS_EX_NOACTIVATE,
                        new(lpClassName),
                        new(lpWindowName),
                        WINDOWS_STYLE.WS_DISABLED,
                        0, 0, 0, 0,
                        new(),
                        new(),
                        new HINSTANCE(GetModuleHandle(new PCWSTR())),
                        null);
                }
            }

            // WndHooks.InstallWindowMessageHook(m_msgWnd, (int)(WM_USER + 100), WndHooks.HookID.WH_CALLWNDPROC, 0, 0);

            m_winEventHook = WinEventHookHelper.CreateGlobalOutOfContextHook(new SortedSet<uint>
            {
                EVENT_OBJECT_CREATE,
                EVENT_OBJECT_DESTROY,

                EVENT_SYSTEM_MOVESIZESTART,
                EVENT_SYSTEM_MOVESIZEEND,

                EVENT_SYSTEM_FOREGROUND,
                EVENT_OBJECT_LOCATIONCHANGE,

                EVENT_SYSTEM_DESKTOPSWITCH,

                EVENT_OBJECT_NAMECHANGE,
            }, OnWinEvent);

            m_hTimer = SetTimer(new(m_msgWnd), IdtTimerWatch, (uint)m_watchInterval.TotalMilliseconds, null);
            m_hTimerRecent = SetTimer(new(m_msgWnd), IdtRecentTimerWatch, 10, null);
            if (m_hTimer == UIntPtr.Zero || m_hTimerRecent == UIntPtr.Zero)
            {
                throw new Win32Exception().WithMessage("Could not create a timer for the message-only window!");
            }

            while (!m_isShuttingDown && GetMessage(out MSG msg, new(m_msgWnd), 0, 0))
            {
                WndProc(m_msgWnd, msg.message, msg.wParam, msg.lParam);
            }
        }

        private void WndProc(IntPtr hwnd, uint msg, UIntPtr wParam, IntPtr lParam)
        {
            try
            {
                switch (msg)
                {
                    case WM_TIMER:
                        if (wParam == IdtTimerWatch)
                        {
                            m_processingLoop.InvokeAsync(OnTimerWatch);
                        }
                        else if (wParam == IdtRecentTimerWatch)
                        {
                            m_processingLoop.InvokeAsync(OnRecentTimerWatch);
                        }
                        break;
                    case WM_DISPLAYCHANGE:
                        m_processingLoop.InvokeAsync(OnDisplayChange);
                        break;
                    case WM_WININICHANGE:
                        m_processingLoop.InvokeAsync(OnSettingChange);
                        break;
                }
            }
            catch (Exception e)
            {
                if (UnhandledException != null)
                {
                    UnhandledException(this, new UnhandledExceptionEventArgs(e, false));
                    return;
                }
                else
                {
                    ExceptionDispatchInfo.Throw(e);
                }
            }
        }

        private void OnSettingChange()
        {
            m_displayManager!.OnSettingChange();
        }

        private void OnDisplayChange()
        {
            m_displayManager!.OnDisplayChange();
        }

        private void OnTimerWatch()
        {
            // Dirty checking is still needed, as some things do not have corresponding events.
            // For example, virtual desktop addition/removal or windows changing their WINDOWS_STYLE at runtime
            // cannot be observed directly.
            RefreshConfiguration();
        }

        /// <summary>
        /// An alternative timer with the highest possible resolution that checks only the newly created windows for changes.
        /// </summary>
        private void OnRecentTimerWatch()
        {
            var cleanedList = new List<(long timestamp, Win32WindowHandle handle)>();
            var checkList = new List<Win32WindowHandle>();
            var now = m_stopwatch.ElapsedMilliseconds;

            lock (m_recentWindowList)
            {
                foreach (var (t, w) in m_recentWindowList)
                {
                    if ((now - t) <= RecentWindowDuration)
                    {
                        cleanedList.Add((t, w));
                    }
                    checkList.Add(w);
                }

                m_recentWindowList = cleanedList;
            }

            foreach (var w in checkList)
            {
                CheckVisibilityChanges(w);
            }
        }

        private void OnWinEvent(uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
        {
            if (idObject == OBJID_CURSOR && eventType == EVENT_OBJECT_LOCATIONCHANGE)
            {
                m_processingLoop.InvokeAsync(OnCursorLocationChanged);
                return;
            }

            if (idObject != OBJID_WINDOW || idChild != 0)
            {
                return;
            }

            Win32WindowHandle? window;

            switch (eventType)
            {
                case EVENT_OBJECT_CREATE:
                    m_processingLoop.InvokeAsync(() => OnWindowCreated(hwnd));
                    return;
                case EVENT_OBJECT_DESTROY:
                    m_processingLoop.InvokeAsync(() => OnWindowDestroyed(hwnd));
                    return;

                case EVENT_SYSTEM_DESKTOPSWITCH:
                    m_processingLoop.InvokeAsync(() =>
                    {
                        CheckVirtualDesktops();
                        CheckVisibilityChanges();
                    });
                    return;

                case EVENT_OBJECT_NAMECHANGE:
                    if (m_windowSet.TryGetValue(hwnd, out window) && window.WindowObject != null)
                    {
                        m_processingLoop.InvokeAsync(() => window.WindowObject.OnTitleChange());
                    }
                    return;

                case EVENT_SYSTEM_MOVESIZESTART:
                    if (m_windowSet.TryGetValue(hwnd, out window) && window.WindowObject != null)
                    {
                        m_processingLoop.InvokeAsync(() => window.WindowObject.OnMoveSizeStart());
                    }
                    return;
                case EVENT_SYSTEM_MOVESIZEEND:
                    if (m_windowSet.TryGetValue(hwnd, out window) && window.WindowObject != null)
                    {
                        m_processingLoop.InvokeAsync(() => window.WindowObject.OnMoveSizeEnd());
                    }
                    return;

                case EVENT_SYSTEM_FOREGROUND:
                    m_processingLoop.InvokeAsync(() => OnWindowForeground(hwnd));
                    return;

                case EVENT_OBJECT_LOCATIONCHANGE:
                    if (m_windowSet.TryGetValue(hwnd, out window) && window.WindowObject != null)
                    {
                        m_processingLoop.InvokeAsync(() => window.WindowObject.OnPositionChanged());
                    }
                    return;

                default:
                    return;
            }
        }

        private void CheckVirtualDesktops()
        {
            if (VirtualDesktopManagerLazy is IWin32VirtualDesktopManagerInternal vdm)
            {
                vdm.CheckVirtualDesktopChanges();
            }
        }

        private void OnCursorLocationChanged()
        {
            try
            {
                var newLocation = GetCursorLocation();
                var oldLocation = m_cursorLocation.Exchange(newLocation);
                if (oldLocation != newLocation)
                {
                    CursorLocationChanged?.Invoke(this, new CursorLocationChangedEventArgs(this, newLocation, oldLocation));
                }
            }
            catch (Win32Exception e) when (e.IsAccessDeniedException())
            {
                // Ignore access denied
            }
        }

        private void OnWindowCreated(IntPtr hwnd)
        {
            Win32WindowHandle window = new Win32WindowHandle(hwnd);

            m_windowSet[hwnd] = window;
            lock (m_windowList)
            {
                m_windowList.Add(window);
            }

            if (GetVisibility(window))
            {
                lock (m_visibleWindows)
                {
                    m_visibleWindows.Add(window.WindowObject!);
                }
                try
                {
                    try
                    {
                        window.WindowObject!.OnAdded();
                    }
                    finally
                    {
                        WindowAdded?.Invoke(this, new WindowChangedEventArgs(window.WindowObject!));
                    }
                }
                finally
                {
                    if (GetForegroundWindow() == hwnd)
                    {
                        OnWindowForeground(hwnd);
                    }
                }
            }
            else
            {
                lock (m_recentWindowList)
                {
                    m_recentWindowList.Add((m_stopwatch.ElapsedMilliseconds, window));
                }
            }
        }

        private void OnWindowDestroyed(IntPtr hwnd)
        {
            if (m_windowSet.TryGetValue(hwnd, out Win32WindowHandle? window))
            {
                m_windowSet.Remove(hwnd);

                lock (m_windowList)
                {
                    m_windowList.Remove(window);
                }

                if (window.WindowObject == null)
                {
                    // Window was never visible, no need to continue.
                    return;
                }

                try
                {
                    bool removed;
                    lock (m_visibleWindows)
                    {
                        removed = m_visibleWindows.Remove(window.WindowObject);
                    }
                    if (removed)
                    {
                        try
                        {
                            window?.WindowObject.OnRemoved();
                        }
                        finally
                        {
                            WindowRemoved?.Invoke(this, new WindowChangedEventArgs(window.WindowObject));
                        }
                    }
                }
                finally
                {
                    try
                    {
                        window?.WindowObject.OnDestroyed();
                    }
                    finally
                    {
                        WindowDestroyed?.Invoke(this, new WindowChangedEventArgs(window.WindowObject));
                    }
                }
            }
        }

        private void OnWindowForeground(IntPtr hwnd)
        {
            if (m_hwndFocused == hwnd)
            {
                return;
            }

            try
            {
                if (m_windowSet.TryGetValue(m_hwndFocused, out var window))
                {
                    window.WindowObject?.OnBackground();
                }
            }
            finally
            {
                if (m_windowSet.TryGetValue(hwnd, out var window) && window.WindowObject != null)
                {
                    var oldFocusedHwnd = m_hwndFocused;
                    m_hwndFocused = hwnd;
                    window.WindowObject.OnForeground();

                    if (oldFocusedHwnd == IntPtr.Zero)
                    {
                        FocusedWindowChanged?.Invoke(this, new FocusedWindowChangedEventArgs(window.WindowObject, null));
                    }
                    else if (m_windowSet.TryGetValue(oldFocusedHwnd, out var oldWindow) && oldWindow.WindowObject != null)
                    {
                        FocusedWindowChanged?.Invoke(this, new FocusedWindowChangedEventArgs(window.WindowObject, oldWindow.WindowObject));
                    }
                }
                else
                {
                    var oldFocusedHwnd = m_hwndFocused;
                    m_hwndFocused = IntPtr.Zero;
                    if (m_windowSet.TryGetValue(oldFocusedHwnd, out var oldWindow) && oldWindow.WindowObject != null)
                    {
                        FocusedWindowChanged?.Invoke(this, new FocusedWindowChangedEventArgs(null, oldWindow.WindowObject));
                    }
                }
            }
        }

        private bool GetVisibility(Win32WindowHandle window)
        {
            if (Win32Window.GetIsTopLevelVisible(this, window.Handle))
            {
                try
                {
                    window.EnsureWindowObject(this);
                }
                catch (InvalidWindowReferenceException)
                {
                    return false;
                }
                return true;
            }
            return false;
        }

        private Point GetCursorLocation()
        {
            if (!GetCursorPos(out POINT pt))
            {
                throw new Win32Exception().WithMessage("Could not read the position of the cursor!");
            }
            return new Point(pt.x, pt.y);
        }

        private void CheckVisibilityChanges()
        {
            foreach (var window in GetWindowListSnapshot())
            {
                CheckVisibilityChanges(window);
            }
        }

        private void CheckVisibilityChanges(Win32WindowHandle window)
        {
            bool isVisible = GetVisibility(window);
            bool isInList;

            if (window.WindowObject != null)
            {
                lock (m_visibleWindows)
                {
                    isInList = m_visibleWindows.Contains(window.WindowObject);
                }
            }
            else
            {
                isInList = false;
            }

            if (isVisible != isInList)
            {
                if (isVisible)
                {
                    lock (m_visibleWindows)
                    {
                        m_visibleWindows.Add(window.WindowObject!);
                    }
                    try
                    {
                        try
                        {
                            window.WindowObject!.OnAdded();
                        }
                        finally
                        {
                            WindowAdded?.Invoke(this, new WindowChangedEventArgs(window.WindowObject!));
                        }
                    }
                    finally
                    {
                        if (GetForegroundWindow() == window.Handle)
                        {
                            OnWindowForeground(window.Handle);
                        }
                    }
                }
                else
                {
                    try
                    {
                        window.WindowObject!.OnRemoved();
                    }
                    finally
                    {
                        lock (m_visibleWindows)
                        {
                            m_visibleWindows.Remove(window.WindowObject!);
                        }
                        WindowRemoved?.Invoke(this, new WindowChangedEventArgs(window.WindowObject!));
                    }
                }
            }
        }

        private void CheckOpen()
        {
            if (m_eventLoopThread == null)
            {
                throw new InvalidOperationException("Call Open() first!");
            }
        }

        private IReadOnlyList<Win32WindowHandle> GetWindowListImpl()
        {
            List<Win32WindowHandle> windows = new List<Win32WindowHandle>();

            bool success = EnumWindows(delegate (HWND hwnd, LPARAM _)
            {
                try
                {
                    windows.Add(new Win32WindowHandle(hwnd));
                }
                catch (InvalidWindowReferenceException)
                {
                    // ignore
                }
                return true; // Continue
            }, new LPARAM());

            if (!success)
            {
                throw new Win32Exception().WithMessage("Could not enumerate the application windows!");
            }

            return windows;
        }

        private Win32WindowHandle[] GetWindowListSnapshot()
        {
            Win32WindowHandle[] windowListCopy;
            lock (m_windowList)
            {
                windowListCopy = m_windowList.ToArray();
            }
            return windowListCopy;
        }

        private Win32Window[] GetVisibleWindowList()
        {
            Win32Window[] windowListCopy;
            lock (m_visibleWindows)
            {
                windowListCopy = m_visibleWindows.ToArray();
            }
            return windowListCopy;
        }
    }
}
