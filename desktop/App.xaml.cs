using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LocalAIHub;

public partial class App : System.Windows.Application
{
    private const string MutexName = "LocalAIHub_SingleInstance_Mutex_v1";
    private const string ActivateEventName = "LocalAIHub_Activate_Event_v1";
    private const string ActivateAckEventName = "LocalAIHub_Activate_Ack_v1";
    private const int MaxCrashLogBytes = 512 * 1024;
    private const int TrimmedCrashLogBytes = 256 * 1024;

    private static readonly object CrashLogLock = new();
    private static int _crashHandlersRegistered;
    private static int _handlingDispatcherException;

    private static Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private CancellationTokenSource? _listenerCts;

    public static string CrashLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalAIHub",
        "logs",
        "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        RegisterGlobalExceptionHandlers();

        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Try to wake up and bring the already running window to the foreground
            bool wokeUp = TryActivateExistingInstance();
            if (wokeUp)
            {
                Shutdown();
                return;
            }

            // Existing process did not respond (zombie/headless instance) — clean it up and start fresh
            TerminateUnresponsiveInstances();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                try { _singleInstanceMutex.WaitOne(1500); } catch (AbandonedMutexException) { }
            }
        }

        StartActivationListener();
        RegisterPrecisionTouchpadScrollHandler();
        base.OnStartup(e);
    }

    /// <summary>
    /// Fixes WPF's infamous Precision Touchpad overscroll bug: WPF's default ScrollViewer.OnMouseWheel
    /// scrolls a full 3 lines (~48px) per WM_MOUSEWHEEL message even when a touchpad sends tiny
    /// fractional deltas (10..30 instead of 120). Scaling proportionally by (Delta / 120.0) keeps
    /// normal mouse wheels identical while making laptop touchpads smooth and controllable everywhere.
    /// </summary>
    private static void RegisterPrecisionTouchpadScrollHandler()
    {
        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnScrollViewerPreviewMouseWheel));
    }

    private sealed class FractionalScrollBucket
    {
        public double Remainder;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollViewer, FractionalScrollBucket> LogicalScrollBuckets = new();

    private static void OnScrollViewerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || sender is not ScrollViewer sv) return;
        if (Keyboard.Modifiers != ModifierKeys.None) return; // preserve Ctrl+Wheel zoom etc.
        if (sv.ScrollableHeight <= 0) return;

        // Only handle on the innermost scrollable viewer under the pointer so nested boxes/lists still scroll first
        var innermost = FindInnermostScrollableViewer(e.OriginalSource as DependencyObject, e.Delta);
        if (!ReferenceEquals(innermost, sv)) return;

        double lines = Math.Clamp(SystemParameters.WheelScrollLines, 1, 6);
        if (!sv.CanContentScroll)
        {
            double pxPerDetent = lines * 16.0; // standard 48 px per 120-delta mouse notch
            double deltaPx = -(e.Delta / 120.0) * pxPerDetent;
            double target = Math.Clamp(sv.VerticalOffset + deltaPx, 0, sv.ScrollableHeight);
            sv.ScrollToVerticalOffset(target);
        }
        else
        {
            // Logical item-based ScrollViewer (e.g. virtualized DataGrid/ListBox):
            // Accumulate fractional Precision Touchpad deltas so tiny ticks don't jump whole rows.
            var bucket = LogicalScrollBuckets.GetOrCreateValue(sv);
            double itemsPerDetent = Math.Clamp(lines * 0.5, 1.0, 2.5);
            double raw = bucket.Remainder + (-(e.Delta / 120.0) * itemsPerDetent);
            int wholeSteps = (int)Math.Truncate(raw);
            bucket.Remainder = raw - wholeSteps;
            if (wholeSteps != 0)
            {
                double target = Math.Clamp(sv.VerticalOffset + wholeSteps, 0, sv.ScrollableHeight);
                sv.ScrollToVerticalOffset(target);
            }
        }
        e.Handled = true;
    }

    private static ScrollViewer? FindInnermostScrollableViewer(DependencyObject? node, int wheelDelta)
    {
        while (node is not null)
        {
            if (node is ScrollViewer sv && sv.ScrollableHeight > 0)
            {
                bool canMove = wheelDelta > 0
                    ? sv.VerticalOffset > 0.5
                    : sv.VerticalOffset < sv.ScrollableHeight - 0.5;
                if (canMove) return sv;
            }
            node = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private void RegisterGlobalExceptionHandlers()
    {
        if (Interlocked.Exchange(ref _crashHandlersRegistered, 1) != 0)
            return;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        bool isFatal = IsFatalException(e.Exception);
        string report = LogCrash("DispatcherUnhandledException", e.Exception, isFatal);

        // Prevent re-entrant dialog storms if another exception occurs while the dialog is visible
        if (Interlocked.Exchange(ref _handlingDispatcherException, 1) == 1)
        {
            e.Handled = true;
            if (isFatal)
            {
                try { Shutdown(-1); } catch { }
            }
            return;
        }

        try
        {
            bool startupIncomplete = MainWindow is null || !MainWindow.IsLoaded;
            string shortMessage = string.IsNullOrWhiteSpace(e.Exception.Message)
                ? e.Exception.GetType().Name
                : $"{e.Exception.GetType().Name}: {e.Exception.Message}";

            string dialogText =
                "В приложении Brazhnichenko Hub произошла непредвиденная ошибка.\n\n" +
                $"Ошибка: {shortMessage}\n\n" +
                $"Подробный журнал сохранён в:\n{CrashLogPath}\n\n" +
                "Скопировать полный текст ошибки в буфер обмена?";

            var result = System.Windows.MessageBox.Show(
                dialogText,
                "Brazhnichenko Hub — Непредвиденная ошибка",
                MessageBoxButton.YesNo,
                MessageBoxImage.Error);

            if (result == MessageBoxResult.Yes)
            {
                TryCopyToClipboard(report);
            }

            e.Handled = true;

            if (isFatal || startupIncomplete)
            {
                try { Shutdown(-1); } catch { }
            }
        }
        catch
        {
            e.Handled = !isFatal;
        }
        finally
        {
            Interlocked.Exchange(ref _handlingDispatcherException, 0);
        }
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception
            ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown AppDomain unhandled exception");
        LogCrash("AppDomain.UnhandledException", ex, e.IsTerminating);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash("TaskScheduler.UnobservedTaskException", e.Exception, isTerminating: false);
        e.SetObserved();
    }

    private static bool IsFatalException(Exception ex)
    {
        for (Exception? cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is OutOfMemoryException
                or StackOverflowException
                or AccessViolationException
                or AppDomainUnloadedException
                or BadImageFormatException
                or SEHException)
            {
                return true;
            }
        }
        return false;
    }

    private static string GetAppVersion()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                int plusIdx = info.IndexOf('+');
                return plusIdx > 0 ? info[..plusIdx] : info;
            }
            return asm.GetName().Version?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string FormatCrashReport(string source, Exception? ex, bool isTerminating)
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine("================================================================================");
        sb.AppendLine($"Timestamp   : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}");
        sb.AppendLine($"Source      : {source}");
        sb.AppendLine($"Terminating : {isTerminating}");
        sb.AppendLine($"App Version : {GetAppVersion()}");
        sb.AppendLine($"OS          : {Environment.OSVersion.VersionString} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")}, proc={(Environment.Is64BitProcess ? "x64" : "x86")})");
        sb.AppendLine($"Runtime     : {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Process ID  : {Environment.ProcessId}");
        sb.AppendLine("--------------------------------------------------------------------------------");

        if (ex is null)
        {
            sb.AppendLine("Exception   : <null>");
        }
        else
        {
            sb.AppendLine(ex.ToString());

            if (ex is AggregateException agg)
            {
                var flattened = agg.Flatten();
                for (int i = 0; i < flattened.InnerExceptions.Count; i++)
                {
                    sb.AppendLine($"--- Aggregate InnerException [{i}] ---");
                    sb.AppendLine(flattened.InnerExceptions[i].ToString());
                }
            }
            else if (ex.InnerException is not null)
            {
                int depth = 1;
                for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException, depth++)
                {
                    sb.AppendLine($"--- InnerException [{depth}] ---");
                    sb.AppendLine(inner.ToString());
                }
            }
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private static string LogCrash(string source, Exception? ex, bool isTerminating)
    {
        string report = FormatCrashReport(source, ex, isTerminating);

        try
        {
            lock (CrashLogLock)
            {
                string path = CrashLogPath;
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.AppendAllText(path, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxCrashLogBytes)
                {
                    TrimCrashLog(path);
                }
            }
        }
        catch
        {
            // Never throw from crash logger
        }

        return report;
    }

    private static void TrimCrashLog(string path)
    {
        byte[] allBytes = File.ReadAllBytes(path);
        if (allBytes.Length <= MaxCrashLogBytes)
            return;

        int start = Math.Max(0, allBytes.Length - TrimmedCrashLogBytes);
        // Advance to the next newline (0x0A is always a standalone ASCII byte in UTF-8)
        while (start < allBytes.Length && allBytes[start] != (byte)'\n')
        {
            start++;
        }
        if (start < allBytes.Length)
        {
            start++;
        }

        byte[] header = Encoding.UTF8.GetBytes("[... older crash log entries trimmed ...]\r\n");
        int remaining = allBytes.Length - start;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        fs.Write(header, 0, header.Length);
        if (remaining > 0)
        {
            fs.Write(allBytes, start, remaining);
        }
    }

    private static void TryCopyToClipboard(string text)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                return;
            }
            catch
            {
                Thread.Sleep(40);
            }
        }
    }

    private static bool TryActivateExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var activateEvt))
            {
                using (activateEvt)
                using (var ackEvt = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateAckEventName))
                {
                    ackEvt.Reset();
                    activateEvt.Set();
                    return ackEvt.WaitOne(800);
                }
            }
        }
        catch { }
        return false;
    }

    private static void TerminateUnresponsiveInstances()
    {
        try
        {
            int myPid = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName("LocalAIHub"))
            {
                if (p.Id == myPid) continue;
                try
                {
                    p.Kill(entireProcessTree: false);
                    p.WaitForExit(1500);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
    }

    private void StartActivationListener()
    {
        try
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _listenerCts = new CancellationTokenSource();
            var ct = _listenerCts.Token;

            var thread = new Thread(() =>
            {
                WaitHandle[] handles = [_activateEvent, ct.WaitHandle];
                while (!ct.IsCancellationRequested)
                {
                    int idx = WaitHandle.WaitAny(handles);
                    if (idx != 0) break;

                    try
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (MainWindow is MainWindow mw)
                            {
                                mw.RestoreFromTray();
                            }
                        });

                        if (EventWaitHandle.TryOpenExisting(ActivateAckEventName, out var ackEvt))
                        {
                            using (ackEvt) { ackEvt.Set(); }
                        }
                    }
                    catch { }
                }
            })
            {
                IsBackground = true,
                Name = "LocalAIHub-ActivationListener"
            };
            thread.Start();
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _listenerCts?.Cancel(); } catch { }
        try { _activateEvent?.Dispose(); } catch { }
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        try { _singleInstanceMutex?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
