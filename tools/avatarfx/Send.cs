using LibreMetaverse;
using LibreMetaverse.Packets;

static partial class Tool
{
    /// <summary>Logs in to the local grid as another person (never the main grid) and sends viewer effects as the
    /// official viewer would while editing, one stage at a time, printing "stage NAME" as each begins so a test can
    /// take its shots: pointat (a PointAt at a point in front, re-sent each second as the viewer re-sends), beam (an edit
    /// beam to the target avatar, magenta, re-sent every half second), spiral, clear (the PointAt cleared), reset (its own
    /// ResetSkeleton). Prints "reset-received FROM" for every ResetSkeleton effect it hears (the test's own, relayed).
    ///   send URI FIRST LAST PASSWORD START TARGET_AVATAR [SECONDS_PER_STAGE]</summary>
    public static async Task<int> Send(string[] a)
    {
        if (a.Length < 6) { Console.WriteLine("send URI FIRST LAST PASSWORD START TARGET [SECONDS]"); return 1; }
        var (uri, first, last, password, start) = (a[0], a[1], a[2], a[3], a[4]);
        if (!uri.Contains("127.0.0.1") && !uri.Contains("localhost")) { Console.WriteLine("the local grid only"); return 1; }
        var target = new UUID(a[5]);
        double stage = a.Length > 6 ? double.Parse(a[6], System.Globalization.CultureInfo.InvariantCulture) : 8;
        var c = new GridClient();
        c.Network.RegisterCallback(PacketType.ViewerEffect, (_, e) =>
        {
            foreach (var b in ((ViewerEffectPacket)e.Packet).Effect)
                if (b.Type == 19) Console.WriteLine($"reset-received {b.AgentID} anims {(b.TypeData is { Length: 17 } d ? d[16] : -1)}");
        });
        var p = c.Network.DefaultLoginParams(first, last, password, "avatarfx", "1.0");
        p.URI = uri;
        p.Start = start;
        if (!await c.Network.LoginAsync(p)) { Console.WriteLine($"login failed: {c.Network.LoginMessage}"); return 1; }
        await Task.Delay(1500);
        Console.WriteLine($"logged in {c.Self.AgentID}");
        var at = c.Self.SimPosition;
        Console.WriteLine($"at {at.X:F1} {at.Y:F1} {at.Z:F1}");
        try { await c.Appearance.RequestSetAppearance(); } catch (Exception e) { Console.WriteLine($"appearance: {e.Message}"); }
        var me = c.Self.AgentID;
        UUID pointId = UUID.Random(), beamId = UUID.Random(), spiralId = UUID.Random();

        void Put(UUID id, byte type, float duration, byte[] color, byte[] data) => c.Network.SendPacket(new ViewerEffectPacket
        {
            AgentData = { AgentID = me, SessionID = c.Self.SessionID },
            Effect = [new ViewerEffectPacket.EffectBlock { ID = id, AgentID = me, Type = type, Duration = duration, Color = color, TypeData = data }],
        });
        byte[] Spiral(UUID src, UUID tgt, Vector3d pos)
        {
            var d = new byte[56];
            src.ToBytes(d, 0);
            tgt.ToBytes(d, 16);
            pos.ToBytes(d, 32);
            return d;
        }
        byte[] PointAt(UUID tgt, Vector3d pos, byte type)
        {
            var d = new byte[57];
            me.ToBytes(d, 0);
            tgt.ToBytes(d, 16);
            pos.ToBytes(d, 32);
            d[56] = type;
            return d;
        }
        byte[] white = [255, 255, 255, 255], magenta = [255, 0, 255, 255];
        async Task During(string name, Func<Task> each, double every)
        {
            Console.WriteLine($"stage {name}");
            var end = DateTime.UtcNow.AddSeconds(stage);
            while (DateTime.UtcNow < end)
            {
                await each();
                await Task.Delay(TimeSpan.FromSeconds(every));
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(stage));   // stand and be seen
        Console.WriteLine("stage idle");
        await Task.Delay(TimeSpan.FromSeconds(stage));
        // a point 0.6 m ahead, 0.3 m to the left and at chest height, as a global position
        var g = c.Self.GlobalPosition;
        var r = c.Self.SimRotation;
        var ahead = g + new Vector3d(new Vector3(0.6f, 0.3f, 0.3f) * r);
        await During("pointat", () => { Put(pointId, 15, float.MaxValue / 4, white, PointAt(UUID.Zero, ahead, 1)); return Task.CompletedTask; }, 1);
        await During("beam", () => { Put(beamId, 7, 0.7f, magenta, Spiral(me, target, Vector3d.Zero)); return Task.CompletedTask; }, 0.5);
        Put(pointId, 15, 0, white, PointAt(UUID.Zero, Vector3d.Zero, 0));
        await During("spiral", () => { Put(spiralId, 12, 2f, magenta, Spiral(me, UUID.Zero, Vector3d.Zero)); return Task.CompletedTask; }, 1.5);
        Console.WriteLine("stage clear");
        await Task.Delay(TimeSpan.FromSeconds(stage));
        Console.WriteLine("stage reset");
        var rd = new byte[17];
        me.ToBytes(rd, 0);
        Put(UUID.Random(), 19, 1, [0, 0, 0, 255], rd);
        await Task.Delay(TimeSpan.FromSeconds(stage * 2));   // the test's own reset arrives here meanwhile
        Console.WriteLine("stage done");
        await c.Network.LogoutAsync();
        return 0;
    }
}
