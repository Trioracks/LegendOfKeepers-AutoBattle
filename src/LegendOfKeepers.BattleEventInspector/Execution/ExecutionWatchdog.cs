using System;
using System.Diagnostics;

namespace LegendOfKeepers.BattleEventInspector.Execution;

internal sealed class ExecutionWatchdog
{
    private readonly Stopwatch _clock = new();
    private readonly int _timeoutSeconds;
    public ExecutionWatchdog(int timeoutSeconds) => _timeoutSeconds = Math.Clamp(timeoutSeconds, 1, 60);
    public bool Active => _clock.IsRunning;
    public void Start() { _clock.Restart(); }
    public void Stop() { _clock.Reset(); }
    public bool Expired => Active && _clock.Elapsed.TotalSeconds >= _timeoutSeconds;
}
