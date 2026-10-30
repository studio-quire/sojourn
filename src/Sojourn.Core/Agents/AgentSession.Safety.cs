namespace Sojourn.Core.Agents;

public sealed partial class AgentSession
{
    Safety? _safety;
    MoveLock? _moveLock;
    readonly object _safetyLock = new();

    /// <summary>Derendering, the asset blacklist, antispam, bumps and releasing controls (Safety.cs). Made once (it
    /// watches the world model from then on).</summary>
    public Safety Safety
    {
        get
        {
            if (_safety is { } s) return s;
            lock (_safetyLock) return _safety ??= new Safety(this);
        }
    }

    /// <summary>"Keep me in place" (MoveLock.cs).</summary>
    public MoveLock MoveLock
    {
        get
        {
            if (_moveLock is { } m) return m;
            lock (_safetyLock) return _moveLock ??= new MoveLock(this);
        }
    }
}
