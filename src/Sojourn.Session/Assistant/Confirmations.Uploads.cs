using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

namespace Sojourn.Session.Assistant;

/// <summary>
/// Uploads where they cost L$ (TesselAgent upload, uploadMesh and uploadClips, whoever calls): before the person is
/// asked, the fee is worked out from the file itself (a picture by its size as uploaded, a sound's clips by how many it
/// makes) or, for a model, asked of the grid (the official uploader's "Calculate weights and fee"), and pinned on the op
/// as `max_cost`, which the op holds to (for clips, the whole set's); a file that can't go up, or a fee over the
/// balance, is refused in words without asking. What's asked names the fee and what the file becomes ("Upload Sunset, a
/// picture, for L$10" / "Resized from 1000 by 750 to 1024 by 512 ..."), a model's land impact too, a sound's clips their
/// names and folder, once for the set. Claude Opus 5.5, 2026-11-01.
/// </summary>
public sealed partial class Confirmations
{
    readonly ConditionalWeakTable<JsonObject, Tuple<string, string>> _uploadAsks = new();

    /// <summary>Whether an upload costs here: on a grid with fees, or a test's pretend fee for its kind.</summary>
    bool UploadCosts(string op, JsonObject a)
    {
        if (a.Bool("quote", false)) return false;
        if (_uploadsCost) return true;
        if (UploadOps.TestFees is not { } fees) return false;
        if (op == "uploadClips") return fees.ContainsKey("clip") || fees.ContainsKey("sound");
        var kind = op == "uploadMesh" ? "mesh" : a.Str("type") ?? UploadOps.KindOf(a.Str("path") ?? "");
        return fees.ContainsKey(kind is "image" or "picture" ? "texture" : kind);
    }

    /// <summary>The fee found and pinned, and what the person will be asked; or why it's refused without asking.</summary>
    async Task<string?> UploadFeeAsync(string op, JsonObject a)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        int fee;
        string title, detail;
        var file = a.Str("path") is { Length: > 0 } p ? Path.GetFileName(p) : Lang.T("the file");
        try
        {
            if (op == "upload")
            {
                var look = await _s.Uploads.LookAsync(a, cts.Token) as JsonObject ?? new JsonObject();
                fee = look.Int("fee", 0);
                var name = a.Str("name") is { Length: > 0 } n ? n : look.Str("name") ?? file;
                title = look.Str("type") switch
                {
                    "texture" => Lang.F("Upload {0}, a picture, for L${1}", name, fee),
                    "sound" => Lang.F("Upload {0}, a sound, for L${1}", name, fee),
                    _ => Lang.F("Upload {0}, an animation, for L${1}", name, fee),
                };
                detail = look.Str("type") switch
                {
                    "texture" when look.Bool("resized", false) =>
                        Lang.F("From {0}, resized from {1} by {2} pixels to {3} by {4}.", file, look.Int("width", 0), look.Int("height", 0), look.Int("upload_width", 0), look.Int("upload_height", 0)),
                    "texture" => Lang.F("From {0}, {1} by {2} pixels.", file, look.Int("width", 0), look.Int("height", 0)),
                    _ => Secs(file, look.Num("seconds", 0)),
                };
            }
            else if (op == "uploadClips")
            {
                var q = a.DeepClone().AsObject();
                q.Remove("max_cost");
                q["quote"] = true;
                var r = await _s.Uploads.UploadClipsAsync(q, cts.Token) as JsonObject ?? new JsonObject();
                var plan = r["plan"] as JsonObject ?? new JsonObject();
                fee = r.Int("cost", 0);
                int n = r.Int("count", 0), each = r.Int("cost_each", 0);
                var names = plan["names"] as JsonArray;
                string first = names?.FirstOrDefault()?.ToString() ?? "", last = names?.LastOrDefault()?.ToString() ?? "";
                title = Lang.F("Upload {0} as {1} clips, for L${2}", r.Str("name") ?? file, n, fee);
                detail = Join(Lang.F("L${0} each.", each), Lang.F("From {0}, {1}, cut into clips of up to {2} seconds: {3} to {4}.", file,
                    UploadWords.Seconds(r.Num("seconds", 0)), One(plan.Num("clip_seconds", 0)), first, last));
                if (plan.Str("cuts") == "quiet") detail = Join(detail, Lang.T("Cut at the quietest moment near each mark."));
                var into = a.Uuid("folder_id") ?? _s.Inventory.FolderFor(FolderType.Sound);
                var parent = _s.Inventory.Node(into)?.Name ?? Lang.T("your inventory");
                detail = Join(detail, a.Bool("new_folder", true) ? Lang.F("Into a new folder, {0}, in {1}.", r.Str("name") ?? "", parent) : Lang.F("Into {0}.", parent));
                if (a.Bool("notecard", false)) detail = Join(detail, Lang.F("With a notecard, {0}, listing them in order.", plan.Str("notecard_name") ?? ""));
                a["max_cost"] = fee;
                if (UploadBalance() is { } bal && bal < fee)
                    return $"Not enough L$: the {n} clips cost L${fee} in all (L${each} each), and your balance is L${bal}. Nothing was uploaded.";
                _uploadAsks.AddOrUpdate(a, Tuple.Create(title, Join(detail, UploadBalanceLine())));
                return null;
            }
            else
            {
                var q = a.DeepClone().AsObject();
                q.Remove("max_cost");
                q["quote"] = true;
                var r = await _s.Uploads.UploadMeshAsync(q, cts.Token) as JsonObject ?? new JsonObject();
                if (r.Str("refusal") is { Length: > 0 } no) return no;
                fee = r.Int("cost", 0);
                var name = r.Str("name") ?? file;
                title = Lang.F("Upload {0}, a model, for L${1}", name, fee);
                detail = Lang.F("From {0}.", file);
                if (r["weights"] is JsonObject w)
                    detail = Join(detail, Lang.F("Land impact {0} when rezzed (download {1}, physics {2}, server {3}).",
                        One(w.Num("land_impact", 0)), One(w.Num("streaming", 0)), One(w.Num("physics", 0)), One(w.Num("server", 0))));
            }
        }
        catch (LeapError e) { return e.Message; }
        catch (NotConnected) { return "not in world; nothing was uploaded"; }
        catch (OperationCanceledException) { return "working out the fee took too long; nothing was uploaded"; }
        a["max_cost"] = fee;
        if (UploadBalance() is { } balance && balance < fee)
            return $"Not enough L$: the upload costs L${fee}, and your balance is L${balance}. Nothing was uploaded.";
        if (a.Uuid("folder_id") is { } fid && _s.Inventory.Node(fid) is { } folder) detail = Join(detail, Lang.F("Into {0}.", folder.Name));
        _uploadAsks.AddOrUpdate(a, Tuple.Create(title, Join(detail, UploadBalanceLine())));
        return null;
    }

    /// <summary>The balance an upload is checked against (a test's pretend one where it sets one).</summary>
    int? UploadBalance() => _s.Uploads.Balance;

    string UploadBalanceLine() => UploadBalance() is { } b ? Lang.F("Your balance: L${0}.", b) : "";

    static string One(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>A sound's or an animation's file and length.</summary>
    static string Secs(string file, double s) =>
        Lang.F(s == 1 ? "From {0}, {1} second long." : "From {0}, {1} seconds long.", file, s.ToString(s < 10 ? "0.#" : "0", CultureInfo.InvariantCulture));

    /// <summary>What UploadFeeAsync found to ask, else the plain words.</summary>
    (string Title, string Detail) UploadAsk(JsonObject a) =>
        _uploadAsks.TryGetValue(a, out var t) ? (t.Item1, t.Item2)
            : (a.Str("path") is { Length: > 0 } path ? Lang.F("Upload {0}", Path.GetFileName(path)) : Lang.T("Upload a file"), Join(Lang.T("Uploads cost L$ on this grid."), Balance()));
}
