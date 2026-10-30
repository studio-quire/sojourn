using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.Imaging.Skia;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;
using SkiaSharp;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;
using Sojourn.Session.Assistant;
using Sojourn.Core.Rendering;

namespace Sojourn.Session.Control;

/// <summary>
/// SojournReport: the viewer's Report Abuse (llfloaterreporter.cpp) for the person, through the assistant. A report is
/// drafted first, as the viewer's floater is opened (the picture of the view is taken then, before anything changes),
/// with the person's own words, who or what it's about and a category from the region's list; then sent, which the
/// confirmation gate holds for the person's own press on the panel, showing exactly what goes to Linden Lab. Nothing
/// on the API can send one without that press. Sent as the viewer sends it: the region's SendUserReportWithScreenshot
/// (the report, then the picture's J2C to the uploader it returns) or SendUserReport, else the UserReport message
/// (OpenSim has no caps for it; no picture there). Claude Opus 5.5, 2026-10-26.
/// </summary>
public sealed class ReportApi : LeapApi
{
    public sealed record Draft(string Id, int Category, string CategoryLabel, UUID Abuser, string AbuserName, UUID Object,
        string ObjectName, string OwnerName, Vector3 Position, string RegionName, string Where, string Summary, string Details,
        byte[]? Picture, int PictureW, int PictureH, DateTime At);

    // the text has to fit the confirmation whole (its detail shows 10 lines): the person confirms all of what's sent
    public const int MaxSummary = 100, MaxDetails = 280;
    // the viewer never sends these (llfloaterreporter.cpp onClickSend): copyright and IP go through LL's DMCA process
    const int IpContentRemoval = 66, IpPermissionsExploit = 37;
    const string CopyrightHelp = "copyright and intellectual property complaints aren't abuse reports: Linden Lab takes them by its DMCA "
        + "process (secondlife.com/corporate/dmca); tell the person";

    // the viewer's own list (floater_report_abuse.xml), for regions without the AbuseCategories cap
    static readonly (int Value, string Label)[] BuiltIn =
    {
        (31, "Age > Age play"), (35, "Assault > Shooting, pushing, or shoving another Resident in a Safe Area"),
        (39, "Disclosure > Real world information"), (43, "Disturbing the peace > Excessive scripted objects"),
        (44, "Disturbing the peace > Object littering"), (45, "Disturbing the peace > Repetitive spam"),
        (50, "Fraud > L$ or USD $"), (55, "Harassment > Targeted behavior intended to disrupt"),
        (57, "Indecency > Broadly offensive content or conduct"), (59, "Indecency > Inappropriate avatar name"),
        (60, "Indecency > Inappropriate content or conduct for Region Rating"), (61, "Intolerance"),
        (63, "Land > Encroachment > Objects or textures"), (67, "Skill Gaming Policy Violation"),
    };

    readonly LiveSnapshots _snapshots;
    readonly ConcurrentDictionary<string, Draft> _drafts = new();
    (ulong Region, (int, string)[] List)? _categories;

    public override string Name => "SojournReport";
    public override string Desc => "abuse reports to Linden Lab, as the viewer's Report Abuse: drafted in the person's words, sent only at their own press";

    public ReportApi(AgentSession s, LiveSnapshots snapshots) : base(s)
    {
        _snapshots = snapshots;
        Op("categories", "-> {categories: [{value, label}], source: region|viewer}: what a report can be about, the region's own list", async (_, ct) =>
        {
            var (list, source) = await CategoriesAsync(ct);
            return new JsonObject
            {
                ["categories"] = new JsonArray(list.Select(c => (JsonNode)new JsonObject { ["value"] = c.Value, ["label"] = c.Label }).ToArray()),
                ["source"] = source,
            };
        });
        Op("draft", $"{{about?: avatar id, object?: object id, category: a value or label from categories, summary: one line in the person's "
            + $"words (up to {MaxSummary} characters), details?: what happened, in their words (up to {MaxDetails}), where?: where it happened "
            + "if not here, picture=true} -> {draft, ...as it will be sent}: the picture is their view now (without the panel). Nothing is sent "
            + "until send", DraftAsync);
        Op("send", "{draft} -> {sent, picture, via}: sends a draft to Linden Lab once the person has confirmed it with their own press (the panel "
            + "shows them all of it); a refusal means they didn't", SendAsync);
    }

    /// <summary>The draft a send would send (the confirmation's words come from it), or null.</summary>
    public Draft? Find(JsonObject a) => a.Str("draft") is { } id && _drafts.TryGetValue(id, out var d) ? d : null;

    /// <summary>What the person is shown before a send: everything that goes to Linden Lab.</summary>
    public static (string Title, string Detail) Describe(Draft d)
    {
        string who = d.Object != UUID.Zero ? $"\"{d.ObjectName}\"{(d.OwnerName.Length > 0 ? $" (owned by {d.OwnerName})" : "")}"
            : d.Abuser != UUID.Zero ? d.AbuserName : "what happened";
        var detail = new List<string> { d.CategoryLabel, $"\"{d.Summary}\"" };
        if (d.Details.Length > 0) detail.Add(d.Details);
        detail.Add(d.Picture != null ? "With a picture of your view from when you asked." : "No picture.");
        return ($"Report {who} to Linden Lab", string.Join("\n", detail));
    }

    async Task<((int Value, string Label)[] List, string Source)> CategoriesAsync(CancellationToken ct)
    {
        var c = S.Client ?? throw new NotConnected("not logged in");
        var sim = c.Network.CurrentSim ?? throw new LeapError("not in a region yet");
        if (_categories is { } cached && cached.Region == sim.Handle) return (cached.List, "region");
        if (sim.Caps?.CapabilityURI("AbuseCategories") is not { } cap) return (BuiltIn, "viewer");
        try
        {
            var (_, data) = await c.HttpCapsClient.GetAsync(cap, ct);
            if (data != null && OSDParser.Deserialize(data) is OSDMap m && m["categories"] is OSDArray arr && arr.Count > 0)
            {
                var list = arr.OfType<OSDMap>()
                    .Select(r => (Value: r["category"].AsInteger(), Label: r["description_localized"].AsString()))
                    .Where(r => r.Label.Length > 0).ToArray();
                if (list.Length > 0)
                {
                    _categories = (sim.Handle, list);
                    return (list, "region");
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException) { }
        return (BuiltIn, "viewer");
    }

    async Task<JsonNode?> DraftAsync(JsonObject a, CancellationToken ct)
    {
        var c = S.Client ?? throw new NotConnected("not logged in");
        var w = S.Region ?? throw new LeapError("not in a region yet");
        var summary = (a.Str("summary") ?? "").Trim();
        var details = (a.Str("details") ?? "").Trim();
        if (summary.Length == 0) throw new LeapError("summary: one line, in the person's words");
        if (summary.Length > MaxSummary) throw new LeapError($"summary: {MaxSummary} characters at most, so the person sees all of it before it's sent");
        if (details.Length > MaxDetails) throw new LeapError($"details: {MaxDetails} characters at most, so the person sees all of it before it's sent; say less");

        var (list, _) = await CategoriesAsync(ct);
        var cat = a["category"] switch
        {
            JsonValue v when v.TryGetValue<int>(out var n) => list.FirstOrDefault(x => x.Value == n),
            JsonValue v when v.TryGetValue<string>(out var t) && int.TryParse(t, out var n) => list.FirstOrDefault(x => x.Value == n),
            JsonValue v when v.TryGetValue<string>(out var t) => list.FirstOrDefault(x => x.Label.Equals(t.Trim(), StringComparison.OrdinalIgnoreCase))
                is { Label: not null } exact ? exact : list.Where(x => x.Label.Contains(t.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray() is { Length: 1 } one ? one[0] : default,
            _ => default,
        };
        if (cat.Label is null) throw new LeapError("category: one of SojournReport categories (its value or label)");
        if (cat.Value is IpContentRemoval or IpPermissionsExploit
            || $"{summary} {details}".Contains("copyright", StringComparison.OrdinalIgnoreCase))
            throw new LeapError(CopyrightHelp);

        // who or what: an avatar, or an object (its owner named too), as the viewer's pickers fill them in
        UUID abuser = UUID.Zero, obj = UUID.Zero;
        string abuserName = "", objName = "", ownerName = "";
        var pos = c.Self.SimPosition;
        if (a.Uuid("about") is { } who)
        {
            abuser = who;
            abuserName = await NameAsync(who, ct);
        }
        if (a.Uuid("object") is { } oid)
        {
            var prim = w.PrimById(oid) ?? throw new LeapError("object: not one in this region");
            obj = oid;
            pos = w.RegionPosition(prim);
            var got = await S.Api.CallAsync("TesselObjects", new JsonObject { ["op"] = "get", ["ids"] = new JsonArray(oid.ToString()), ["fresh"] = true, ["timeout"] = 3 },
                true, TimeSpan.FromSeconds(4), ct) as JsonObject;
            var o = (got?["objects"] as JsonArray)?.FirstOrDefault() as JsonObject;
            objName = o?.Str("name") is { Length: > 0 } n ? n : "an object";
            if (o?.Uuid("owner_id") is { } owner && owner != UUID.Zero)
            {
                ownerName = await NameAsync(owner, ct);
                if (abuser == UUID.Zero) (abuser, abuserName) = (owner, ownerName);   // the viewer's picker fills the abuser in with the owner
            }
        }

        byte[]? picture = null;
        int pw = 0, ph = 0;
        if (a.Bool("picture", true))
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"sojourn-report-{Guid.NewGuid():N}.png");
            try
            {
                var r = await _snapshots.RenderAsync(new SnapshotRequest
                {
                    Agent = S, World = w, CameraPos = default, Focus = default, FileName = tmp, Width = 1024, Height = 1024, Tool = "report",
                }, ct);
                if (r.Ok) (picture, pw, ph) = Encode(tmp);
            }
            finally { try { File.Delete(tmp); } catch (IOException) { } }
        }

        var where = a.Str("where") is { Length: > 0 } wh ? wh.Trim() : $"{w.Name} ({pos.X:0}, {pos.Y:0}, {pos.Z:0})";
        var d = new Draft(Guid.NewGuid().ToString("N")[..8], cat.Value, cat.Label, abuser, abuserName, obj, objName, ownerName, pos,
            w.Name ?? "", where, summary, details, picture, pw, ph, DateTime.UtcNow);
        _drafts[d.Id] = d;
        foreach (var old in _drafts.Values.Where(x => DateTime.UtcNow - x.At > TimeSpan.FromHours(1)).ToList()) _drafts.TryRemove(old.Id, out _);
        var (title, detail) = Describe(d);
        return new JsonObject
        {
            ["draft"] = d.Id, ["category"] = cat.Label, ["about"] = abuser != UUID.Zero ? abuserName : null,
            ["object"] = obj != UUID.Zero ? objName : null, ["where"] = where, ["picture"] = picture != null,
            ["the_person_will_see"] = $"{title}\n{detail}",
        };
    }

    async Task<string> NameAsync(UUID id, CancellationToken ct)
    {
        if (S.Region?.Avatars.Values.FirstOrDefault(av => av.ID == id) is { Name.Length: > 0 } av) return av.Name;
        if (S.Names.Legacy(id) is { Length: > 0 } legacy) return legacy;
        try
        {
            var got = await S.Api.CallAsync("TesselAgent", new JsonObject { ["op"] = "names", ["ids"] = new JsonArray(id.ToString()) }, true, TimeSpan.FromSeconds(6), ct);
            if ((got as JsonObject)?["names"] is JsonObject names && names[id.ToString()] is JsonObject e && (e.Str("username") ?? e.Str("display_name")) is { Length: > 0 } n)
                return n;
        }
        catch (Exception e) when (e is not OperationCanceledException) { }
        return $"someone ({id.ToString()[..8]})";
    }

    /// <summary>The picture as an upload: J2C at a power of two each way, rounded up from three quarters of the next
    /// (the viewer's biasedScaleToPowerOfTwo), 1024 at most.</summary>
    static (byte[], int, int) Encode(string png)
    {
        using var src = SKBitmap.Decode(png) ?? throw new LeapError("the picture couldn't be read");
        static int Pow2(int v)
        {
            int p = 16;
            while (p * 2 <= v) p *= 2;
            return Math.Min(1024, v >= p * 2 * 0.75 ? p * 2 : p);
        }
        int w = Pow2(src.Width), h = Pow2(src.Height);
        using var scaled = src.Resize(new SKImageInfo(w, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
        var tex = new AssetTexture(SkiaTextureCodec.ToManagedImage(scaled));
        tex.Encode();
        return (tex.AssetData, w, h);
    }

    async Task<JsonNode?> SendAsync(JsonObject a, CancellationToken ct)
    {
        var d = Find(a) ?? throw new LeapError("no such draft (draft one first)");
        var c = S.Client ?? throw new NotConnected("not logged in");
        var sim = c.Network.CurrentSim ?? throw new LeapError("not in a region");
        // as the viewer's gatherReport: the summary line staff see first ("Preview" off the production grid), and the
        // details with what the pickers filled in
        string summary = (IsMainGrid ? "" : "Preview ") + $" |{sim.Name}| ({d.Where}) [{d.CategoryLabel}]  {{{d.AbuserName}}}  \"{d.Summary}\"";
        var details = new System.Text.StringBuilder();
        details.Append($"V{S.Host.Version}\n\n");
        if (d.Object != UUID.Zero && d.OwnerName.Length > 0) details.Append($"Object: {d.ObjectName}\nOwner: {d.OwnerName}\n");
        details.Append($"Abuser name: {d.AbuserName} \nAbuser location: {d.Where} \n{d.Details}");
        var report = new OSDMap
        {
            ["report-type"] = OSD.FromInteger(3),   // complaint
            ["category"] = OSD.FromInteger(d.Category),
            ["position"] = OSD.FromVector3(d.Position),
            ["check-flags"] = OSD.FromInteger(0),
            ["screenshot-id"] = OSD.FromUUID(UUID.Zero),
            ["object-id"] = OSD.FromUUID(d.Object),
            ["abuser-id"] = OSD.FromUUID(d.Abuser),
            ["abuse-region-name"] = OSD.FromString(""),
            ["abuse-region-id"] = OSD.FromUUID(UUID.Zero),
            ["summary"] = OSD.FromString(summary),
            ["version-string"] = OSD.FromString($"{S.Host.Channel} {S.Host.Version} Win"),
            ["details"] = OSD.FromString(details.ToString()),
        };
        _drafts.TryRemove(d.Id, out _);

        if (d.Picture != null && sim.Caps?.CapabilityURI("SendUserReportWithScreenshot") is { } shotCap)
        {
            // the viewer's LLARScreenShotUploader: the report with the picture's asset id, then the J2C to the uploader
            var asset = UUID.Combine(UUID.Random(), c.Self.SecureSessionID);
            report["screenshot-id"] = OSD.FromUUID(asset);
            try
            {
                var (_, first) = await c.HttpCapsClient.PostAsync(shotCap, OSDFormat.Xml, report, ct);
                if (Map(first) is { } st && st["state"].AsString() == "upload" && st["uploader"].AsString() is { Length: > 0 } up)
                {
                    var (_, second) = await c.HttpCapsClient.PostAsync(new Uri(up), "application/octet-stream", d.Picture, ct);
                    if (Map(second) is { } done && done["state"].AsString() == "complete")
                        return Sent(true, "SendUserReportWithScreenshot");
                }
            }
            catch (Exception e) when (e is not OperationCanceledException) { }
            report["screenshot-id"] = OSD.FromUUID(UUID.Zero);   // the picture didn't go: the words still do
        }
        if (sim.Caps?.CapabilityURI("SendUserReport") is { } cap)
        {
            try
            {
                var (resp, _) = await c.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, report, ct);
                if (resp?.IsSuccessStatusCode != false) return Sent(false, "SendUserReport");
            }
            catch (Exception e) when (e is not OperationCanceledException) { }
        }
        c.Network.SendPacket(new UserReportPacket
        {
            AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
            ReportData =
            {
                ReportType = 3, Category = (byte)d.Category, Position = d.Position, CheckFlags = 0, ScreenshotID = UUID.Zero,
                ObjectID = d.Object, AbuserID = d.Abuser, AbuseRegionName = Utils.StringToBytes(""), AbuseRegionID = UUID.Zero,
                Summary = Utils.StringToBytes(summary), Details = Utils.StringToBytes(details.ToString()),
                VersionString = Utils.StringToBytes($"{S.Host.Channel} {S.Host.Version} Win"),
            },
        }, sim);
        return Sent(false, "UserReport message");

        JsonObject Sent(bool pic, string via) => new() { ["sent"] = true, ["picture"] = pic, ["via"] = via };
    }

    bool IsMainGrid => S.Host.LoginUriFor(S.Config).Contains("login.agni.", StringComparison.OrdinalIgnoreCase);

    static OSDMap? Map(byte[]? data)
    {
        try { return data is { Length: > 0 } ? OSDParser.Deserialize(data) as OSDMap : null; }
        catch (Exception) { return null; }
    }
}
