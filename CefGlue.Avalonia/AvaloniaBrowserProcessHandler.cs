using System;
using System.Globalization;
using System.Threading;
using Avalonia.Threading;
using Xilium.CefGlue.Common.Handlers;

namespace Xilium.CefGlue.Avalonia
{
    public class AvaloniaBrowserProcessHandler : BrowserProcessHandler
    {
        private const int DefaultPumpIntervalMs = 1000 / 30;
        private const int MinPumpIntervalMs = 1000 / 60;
        private const int MaxPumpIntervalMs = 1000;
        private const string PumpIntervalSetting = "CefGlue.Avalonia.MessagePumpIntervalMs";
        private const string PumpIntervalVariable = "CEFGLUE_MESSAGE_PUMP_INTERVAL_MS";

        private static readonly int PumpIntervalMs = ResolvePumpIntervalMs();
        private readonly object _schedule = new();
        private DispatcherTimer _pump;
        private bool _pumpRequested;
        private int _immediatePumpQueued;

        private static int ResolvePumpIntervalMs()
        {
            var data = AppContext.GetData(PumpIntervalSetting);
            var configured = data != null
                ? Convert.ToString(data, CultureInfo.InvariantCulture)
                : Environment.GetEnvironmentVariable(PumpIntervalVariable);

            return int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intervalMs)
                ? Math.Clamp(intervalMs, MinPumpIntervalMs, MaxPumpIntervalMs)
                : DefaultPumpIntervalMs;
        }

        protected override void OnScheduleMessagePumpWork(long delayMs)
        {
            // CEF's delay is a deadline, not a timer interval, so it is never used as
            // the timer period. A free-running 30 Hz safety pump keeps Chromium alive
            // when it stops notifying, and requests for immediate work are honoured
            // straight away; otherwise input, scrolling, and compositor frames are
            // throttled to the safety rate.
            lock (_schedule)
            {
                if (!_pumpRequested)
                {
                    _pumpRequested = true;
                    // DispatcherTimer must be created on the UI thread, where
                    // DoMessageLoopWork also has to run.
                    Dispatcher.UIThread.Post(() =>
                    {
                        _pump = new DispatcherTimer(DispatcherPriority.Default)
                        {
                            Interval = TimeSpan.FromMilliseconds(PumpIntervalMs)
                        };
                        _pump.Tick += (_, _) => CefRuntime.DoMessageLoopWork();
                        _pump.Start();
                    }, DispatcherPriority.Default);
                }
            }

            if (delayMs > 0)
            {
                return;
            }

            // Notified from any thread, and CEF notifies again while work remains, so a
            // single queued pump is enough. Coalescing prevents the callback storm that
            // a job per notification would cause.
            if (Interlocked.Exchange(ref _immediatePumpQueued, 1) == 1)
            {
                return;
            }

            // Default is the lowest priority Avalonia dispatches unconditionally. Input
            // and Background are held back while OS input is pending, which stalls the
            // pump exactly while the user scrolls; anything above Render lets CEF
            // preempt layout and repaint.
            Dispatcher.UIThread.Post(
                () =>
                {
                    // Cleared first, so work scheduled from inside the pump queues a new one.
                    Volatile.Write(ref _immediatePumpQueued, 0);
                    CefRuntime.DoMessageLoopWork();
                },
                DispatcherPriority.Default);
        }
    }
}
