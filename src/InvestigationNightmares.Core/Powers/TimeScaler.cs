using System;

namespace InvestigationNightmares.Powers;

/// <summary>
/// A clock the game sees instead of the real one. Changing the scale never makes the game's clock jump
/// or run backwards: game time continues from where it is at the new rate.
/// Thread-safe (the hooked clock functions are called from any game thread).
/// </summary>
public sealed class TimeScaler
{
    readonly object _gate = new();
    long _realBase;
    long _gameBase;
    double _scale = 1.0;
    long _lastReturned = long.MinValue;
    bool _started;

    public double Scale { get { lock (_gate) return _scale; } }

    /// <summary>Map a real counter reading to the game's counter reading.</summary>
    public long ToGame(long real)
    {
        lock (_gate)
        {
            if (!_started) { _started = true; _realBase = real; _gameBase = real; }
            long g = _gameBase + (long)((real - _realBase) * _scale);
            if (g < _lastReturned) g = _lastReturned; // monotonic even if callers race
            _lastReturned = g;
            return g;
        }
    }

    public void SetScale(double scale, long realNow)
    {
        if (scale < 0 || double.IsNaN(scale) || scale > 16) throw new ArgumentOutOfRangeException(nameof(scale));
        lock (_gate)
        {
            if (!_started) { _started = true; _realBase = realNow; _gameBase = realNow; }
            _gameBase = _gameBase + (long)((realNow - _realBase) * _scale);
            _realBase = realNow;
            _scale = scale;
        }
    }
}
