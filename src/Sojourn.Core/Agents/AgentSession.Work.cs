using LibreMetaverse;
using Microsoft.Extensions.Logging;

namespace Sojourn.Core.Agents;

public sealed partial class AgentSession
{
    ObjectViews? _objects;
    BuildOps? _build;
    AgentInventory? _inventory;
    Motion? _motion;
    CameraControl? _camera;

    public ObjectViews Objects => _objects ??= new ObjectViews(this);
    public BuildOps Build => _build ??= new BuildOps(this);
    public AgentInventory Inventory => _inventory ??= new AgentInventory(this);
    InventoryOps? _inventoryOps;
    public InventoryOps InventoryOps => _inventoryOps ??= new InventoryOps(this);
    GestureOps? _gestures;
    public GestureOps Gestures => _gestures ??= new GestureOps(this);
    ParcelOps? _parcels;
    public ParcelOps Parcels => _parcels ??= new ParcelOps(this);
    ProfileOps? _profiles;
    public ProfileOps Profiles => _profiles ??= new ProfileOps(this);
    MuteList? _mutes;
    public MuteList Mutes => _mutes ??= new MuteList(this);
    GroupOps? _groupOps;
    public GroupOps GroupOps => _groupOps ??= new GroupOps(this);
    SearchOps? _searchOps;
    public SearchOps Search => _searchOps ??= new SearchOps(this);
    ContentsOps? _contents;
    public ContentsOps Contents => _contents ??= new ContentsOps(this);
    SelfAnimations? _anims;
    public SelfAnimations Anims => _anims ??= new SelfAnimations(this);
    SeatLeftovers? _seatLeftovers;
    public SeatLeftovers SeatLeftovers => _seatLeftovers ??= new SeatLeftovers(this);
    public Motion Motion => _motion ??= new Motion(this);
    public CameraControl Camera => _camera ??= new CameraControl(this);
    /// <summary>The client's say over LLViewerControl settings: a reason to refuse changing one right now (the headset
    /// owns the field of view, say), or null to allow it.</summary>
    public Func<string, string?>? SettingRefusal { get; set; }
    /// <summary>A viewer setting changed (by key, as LLViewerControl names it), from any caller: the client keeps it.</summary>
    public event Action<string>? SettingChanged;
    internal void OnSettingChanged(string key) => SettingChanged?.Invoke(key);
    /// <summary>Whether the person is in the headset now: their head turns the view there, so ops that would turn it
    /// say they can't.</summary>
    public Func<bool>? InHeadset { get; set; }
    internal bool Headset => InHeadset?.Invoke() == true;
    /// <summary>LLAppViewer requestQuit: the client closes (as the window's own close does) when something listens.</summary>
    public event Action? QuitAsked;
    internal bool Quit()
    {
        if (QuitAsked is not { } q) return false;
        q();
        return true;
    }
    public XferFetch Xfer { get; private set; } = new();
    Sound.Ears? _ears;
    public Sound.Ears Ears => _ears ??= new Sound.Ears(this);
    Appearance.Look? _look;
    Appearance.Wardrobe? _wardrobe;
    public Appearance.Look Look => _look ??= new Appearance.Look(this);
    public Appearance.Wardrobe Wardrobe => _wardrobe ??= new Appearance.Wardrobe(this);

    CancellationTokenSource? _work;
    double _sinceSweep;
    int _tickErrors;

    void StartBackgroundWork(GridClient c)
    {
        _work?.Cancel();
        _work = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        Inventory.StartLoading(c, _work.Token);
        // The body faces east until told otherwise; start from where the region has us facing.
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            if (_client == c && c.Self.Movement.BodyRotation == Quaternion.Identity && c.Self.SimRotation != Quaternion.Identity)
                c.Self.Movement.BodyRotation = c.Self.Movement.HeadRotation = c.Self.SimRotation;
        });
        // what was sent while the person was away (IMs, offers, group notices): the viewer asks for it once after login
        // (LLIMProcessing::requestOfflineMessages), through the region's ReadOfflineMsgs capability (LibreMetaverse falls
        // back to the old RetrieveInstantMessages packet without it); nothing asks LibreMetaverse to on its own
        var work = _work.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 40 && c.Network.CurrentSim?.Caps?.CapabilityURI("ReadOfflineMsgs") == null; i++)
                    await Task.Delay(500, work);
                if (_client != c || !c.Network.Connected) return;
                await c.Self.RetrieveInstantMessagesAsync(work);
                Log.LogInformation("offline messages asked for ({How})",
                    c.Network.CurrentSim?.Caps?.CapabilityURI("ReadOfflineMsgs") != null ? "ReadOfflineMsgs" : "RetrieveInstantMessages");
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Log.LogWarning(e, "offline messages"); }
        });
        HostClock.Subscribe(this, dt =>
        {
            var cur = _client;
            if (cur is null || !cur.Network.Connected || cur.Network.CurrentSim is null) return;
            try
            {
                Motion.Tick(cur, dt);
                Ears.Tick(cur, dt);
                _sinceSweep += dt;
                if (_sinceSweep >= 0.25) { _sinceSweep = 0; Build.Sweep(cur); }
                SeatLeftovers.Tick(cur, dt);
            }
            catch (Exception e) { if (_tickErrors++ % 600 == 0) Log.LogWarning(e, "tick"); }
        });
    }

    void StopBackgroundWork()
    {
        HostClock.Unsubscribe(this);
        _work?.Cancel();
        _work = null;
    }
}
