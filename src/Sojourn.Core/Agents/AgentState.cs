using System.Collections.Concurrent;
using System.Diagnostics;
using LibreMetaverse;

namespace Sojourn.Core.Agents;

/// <summary>One IM conversation, as TesselIM reports it.</summary>
public sealed class ImSession
{
    public required UUID Id { get; init; }
    public required string Type { get; set; }       // p2p | group | conference (a group's chat first taken for p2p is retyped)
    public string Name { get; set; } = "";
    public UUID OtherId { get; set; }
    public int Unread { get; set; }
    public bool Offline { get; set; }
    public bool Ready { get; set; } = true;
    public readonly List<ImLine> Lines = new();
    public TaskCompletionSource? Started;           // group/conference: until the server says the session is up
}

public sealed record ImLine(string From, UUID FromId, string Message, long Timestamp)
{
    /// <summary>The spell of incognito it was said in (0: none): never written to ims.jsonl, and served by the history
    /// ops only while that spell lasts and sees (PrivateMode.ShownSpell).</summary>
    public int Spell { get; init; }
    public bool Private => Spell != 0;
}

/// <summary>A create/rez/duplicate waiting for its object to appear (TesselBuild's matcher).</summary>
public sealed class PendingCreate
{
    public required Vector3 WantPos { get; init; }
    public required HashSet<uint> Before { get; init; }   // my objects near WantPos when asked
    public required DateTime Asked { get; init; }
    public bool MoveToPos { get; init; }
    public System.Text.Json.Nodes.JsonObject? Modify { get; init; }
    public string? ModifyError { get; set; }
    public readonly TaskCompletionSource<Primitive> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Per-agent state the emulated APIs keep. Lives as long as the agent (across reconnects), not per login.</summary>
public sealed class AgentState : IDisposable
{
    readonly AgentSession _s;

    public AgentState(AgentSession s)
    {
        _s = s;
        Notify = new Notifications(s);
        LoadIms();
    }

    // ---------- IM transcripts: the viewer keeps them on disk, so history survives a restart ----------

    string ImFile => Path.Combine(_s.StateDir, "ims.jsonl");
    readonly object _imFileLock = new();

    public void RecordIm(ImSession s, ImLine line)
    {
        if (_s.Private.Spell is > 0 and var spell) line = line with { Spell = spell };
        lock (s.Lines)
        {
            s.Lines.Add(line);
            if (s.Lines.Count > 500) s.Lines.RemoveRange(0, 100);
        }
        var o = new System.Text.Json.Nodes.JsonObject
        {
            ["session_id"] = s.Id.ToString(), ["type"] = s.Type, ["name"] = s.Name, ["other_id"] = s.OtherId.ToString(),
            ["from"] = line.From, ["from_id"] = line.FromId.ToString(), ["message"] = line.Message, ["timestamp"] = line.Timestamp,
        };
        if (line.Private) return;
        try { lock (_imFileLock) File.AppendAllText(ImFile, o.ToJsonString(Json.Compact) + "\n"); }
        catch { /* history is a convenience; never fail an IM over it */ }
    }

    void LoadIms()
    {
        try
        {
            if (!File.Exists(ImFile)) return;
            var lines = File.ReadAllLines(ImFile);
            foreach (var l in lines.Skip(Math.Max(0, lines.Length - 20000)))
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(l) is not System.Text.Json.Nodes.JsonObject o) continue;
                if (!UUID.TryParse(o.Str("session_id") ?? "", out var sid)) continue;
                var s = Ims.GetOrAdd(sid, _ => new ImSession
                {
                    Id = sid, Type = o.Str("type") ?? "p2p", Name = o.Str("name") ?? "",
                    OtherId = UUID.TryParse(o.Str("other_id") ?? "", out var other) ? other : UUID.Zero,
                    Ready = (o.Str("type") ?? "p2p") == "p2p",
                });
                s.Lines.Add(new ImLine(o.Str("from") ?? "", UUID.TryParse(o.Str("from_id") ?? "", out var f) ? f : UUID.Zero,
                    o.Str("message") ?? "", (long)o.Num("timestamp", 0)));
                if (s.Lines.Count > 500) s.Lines.RemoveRange(0, 100);
            }
        }
        catch { }
    }

    public Notifications Notify { get; }

    /// <summary>The newest thing this agent said in chat or IM, for overview pages.</summary>
    public string? LastWords { get; set; }

    /// <summary>Per-observer prim flags (UpdateFlags differ per agent on the wire; shared prims hold the last writer's).</summary>
    public ConcurrentDictionary<uint, PrimFlags> Flags { get; } = new();

    public PrimFlags PrimFlagsOf(Primitive p) => Flags.TryGetValue(p.LocalID, out var f) ? f : p.Flags;

    public ConcurrentDictionary<UUID, ImSession> Ims { get; } = new();

    /// <summary>Groups I'm in (from the server's AgentGroupDataUpdate).</summary>
    public ConcurrentDictionary<UUID, Group> Groups { get; } = new();

    /// <summary>Objects whose property replies should be reported as object_properties events (TesselObjects request).</summary>
    public ConcurrentDictionary<UUID, DateTime> PropsRequested { get; } = new();

    /// <summary>The API's selection (TesselObjects select), by local id.</summary>
    public ConcurrentDictionary<uint, byte> Selected { get; } = new();

    public readonly List<PendingCreate> PendingCreates = new();
    /// <summary>The region's latest alert (a refused rez says why there), for ops that otherwise only time out.</summary>
    public (DateTime T, string Message)? LastAlert;
    public readonly HashSet<uint> Claimed = new();

    /// <summary>A camera set by LLAgent setCameraParams (null: the default follow camera).</summary>
    public (Vector3 Pos, Vector3 Focus)? CameraOverride { get; set; }
    public Vector3? CameraFocusOffset { get; set; }

    /// <summary>Viewer settings tools read/write through LLViewerControl (CameraAngle, RenderFarClip, ...).</summary>
    public ConcurrentDictionary<string, System.Text.Json.Nodes.JsonNode?> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);

    public readonly Stopwatch Clock = Stopwatch.StartNew();

    public int SpentThisSession { get; set; }

    /// <summary>Clears what belongs to one login (objects, selection, pending creates); IMs and settings stay.</summary>
    public void ResetForLogin()
    {
        Flags.Clear();
        Selected.Clear();
        lock (PendingCreates)
        {
            foreach (var p in PendingCreates) p.Done.TrySetCanceled();
            PendingCreates.Clear();
            Claimed.Clear();
        }
        CameraOverride = null;
        foreach (var im in Ims.Values) im.Ready = im.Type == "p2p";
    }

    public void Dispose() { }
}
