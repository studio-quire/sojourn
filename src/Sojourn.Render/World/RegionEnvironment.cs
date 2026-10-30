using LibreMetaverse;
using Sojourn.Render.Environment;

namespace Sojourn.Render.World;

/// <summary>A region's environment: fetched when the region arrives and again every so often (the estate, a script or
/// the World module can change it at any time: storms darken the sky through EEP; the viewer re-requests on the
/// simulator's refresh notice), evaluated per render for the render's time. With the agent in it, the parcel the agent
/// stands in has its own environment asked for too (LLEnvironment::requestParcel, on entering a parcel and when its
/// environment changes; here also with each refresh), over the region's.</summary>
public sealed class RegionEnvironment
{
    readonly RegionSource _src;
    readonly Action<string>? _log;
    public EnvironmentState State { get; } = new();
    Task? _fetch;
    long _lastFetch;
    public bool Loaded { get; private set; }
    /// <summary>The agent's parcel as last answered (0: none); whether it has its own sky is State.Parcel.</summary>
    public int Parcel => _parcel;
    int _parcel, _parcelAsked;   // the parcel whose environment State.Parcel holds, and the one last asked about (0: none)

    /// <summary>How often a loaded environment is fetched again.</summary>
    public static TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(10);

    public RegionEnvironment(RegionSource src, Action<string>? log)
    {
        _src = src;
        _log = log;
        Refresh();
    }

    /// <summary>Fetch again if the last fetch is older than <see cref="RefreshInterval"/> (one attempt; the old
    /// environment stays until the answer arrives).</summary>
    public void RefreshIfStale()
    {
        if (!Loaded || _src.FetchEnvironment == null || _fetch is { IsCompleted: false }) return;
        // walking into another parcel asks at once, as the viewer does
        bool parcelChanged = (_src.AgentParcel?.Invoke() ?? 0) != _parcelAsked;
        if (!parcelChanged && System.Diagnostics.Stopwatch.GetElapsedTime(_lastFetch) < RefreshInterval) return;
        Refresh(attempts: 1);
    }

    public void Refresh(int attempts = 30)
    {
        if (_src.FetchEnvironment == null) { Loaded = true; return; }
        if (_fetch is { IsCompleted: false }) return;
        _lastFetch = System.Diagnostics.Stopwatch.GetTimestamp();
        _fetch = Task.Run(async () =>
        {
            // the capability appears once the region's seed caps arrive: retry for a while, then keep the default
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var map = await _src.FetchEnvironment(cts.Token).ConfigureAwait(false);
                    if (map != null)
                    {
                        lock (State) State.ApplyCapabilityResponse(map);
                        await RefreshParcel(cts.Token).ConfigureAwait(false);
                        break;
                    }
                }
                catch (Exception e) { if (attempts > 1) _log?.Invoke($"environment {_src.Name}: {e.Message}"); }
                if (attempt + 1 < attempts) await Task.Delay(1000).ConfigureAwait(false);
            }
            _lastFetch = System.Diagnostics.Stopwatch.GetTimestamp();
            Loaded = true;
        });
    }

    /// <summary>The agent's parcel's own environment, or none: a parcel without one answers with the region's
    /// (parcel_id -1), which holds the region's as recordEnvironment does and clears the parcel's.</summary>
    async Task RefreshParcel(CancellationToken ct)
    {
        int parcel = _src.AgentParcel?.Invoke() ?? 0;
        _parcelAsked = parcel;
        if (parcel <= 0 || _src.FetchParcelEnvironment == null)
        {
            if (_parcel != 0) { lock (State) State.Parcel = null; _parcel = 0; }
            return;
        }
        var map = await _src.FetchParcelEnvironment(parcel, ct).ConfigureAwait(false);
        if (map == null) return;   // asked again with the next refresh
        lock (State)
        {
            State.ApplyCapabilityResponse(map);
            var env = map["environment"] as LibreMetaverse.StructuredData.OSDMap;
            int answered = env != null && env.ContainsKey("parcel_id") ? env["parcel_id"].AsInteger() : -1;
            if (answered != parcel) State.Parcel = null;
        }
        if (_parcel != parcel) _log?.Invoke($"environment {_src.Name}: parcel {parcel} {(State.Parcel != null ? "has its own sky" : "uses the region's")}");
        _parcel = parcel;
    }

    public EnvironmentSnapshot Evaluate(DateTime utc, double altitude)
    {
        lock (State) return State.Evaluate(utc, altitude);
    }

    /// <summary>With the person's own environment over this region's and parcel's (null: theirs alone).</summary>
    public EnvironmentSnapshot Evaluate(DateTime utc, double altitude, EnvironmentLayer? personal)
    {
        lock (State) return State.Evaluate(utc, altitude, personal);
    }
}
