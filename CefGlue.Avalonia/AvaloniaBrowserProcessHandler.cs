using System;
using System.Globalization;
using System.Reactive.Linq;
using ReactiveUI.Avalonia;
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
        private IDisposable _pump;

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
            // CEF's delay is a deadline, not a timer interval. In particular, a
            // delay of zero is commonly emitted while work is already pending.
            // Calling DoMessageLoopWork immediately from that callback can form a
            // self-sustaining UI-thread loop with CEF 152. A bounded free-running
            // pump keeps progress without allowing callback storms to consume a CPU.
            lock (_schedule)
            {
                if (_pump == null)
                {
                    _pump = Observable.Interval(TimeSpan.FromMilliseconds(PumpIntervalMs))
                        .ObserveOn(AvaloniaScheduler.Instance)
                        .Subscribe(_ => CefRuntime.DoMessageLoopWork());
                }
            }
        }
    }
}
