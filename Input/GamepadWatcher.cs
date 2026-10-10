using System.Windows.Threading;
using JensTV.Native;

namespace JensTV.Input;

/// <summary>
/// Polls XInput controllers system-wide so "hold Back + Start" returns home even while Firefox has focus.
/// </summary>
public sealed class GamepadWatcher
{
    const ushort StartButton = 0x0010, BackButton = 0x0020;
    static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(2);

    readonly Action _onHome;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly bool[] _connected = new bool[4];
    readonly DateTime[] _nextProbe = new DateTime[4];
    DateTime? _chordSince;
    bool _fired;

    public GamepadWatcher(Action onHome)
    {
        _onHome = onHome;
        _timer.Tick += Poll;
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    void Poll(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var chord = false;

        for (uint i = 0; i < 4; i++)
        {
            // Polling empty slots is slow on some systems, so only probe them occasionally.
            if (!_connected[i] && now < _nextProbe[i])
                continue;

            uint result;
            try
            {
                result = Win32.XInputGetState(i, out var state);
                if (result == 0 && (state.Gamepad.wButtons & (StartButton | BackButton)) == (StartButton | BackButton))
                    chord = true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                Stop();
                return;
            }

            _connected[i] = result == 0;
            if (result != 0)
                _nextProbe[i] = now + ProbeInterval;
        }

        if (!chord)
        {
            _chordSince = null;
            _fired = false;
            return;
        }

        _chordSince ??= now;
        if (!_fired && now - _chordSince >= HoldTime)
        {
            _fired = true;
            _onHome();
        }
    }
}
