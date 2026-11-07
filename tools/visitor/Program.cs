// Visitor: a second avatar on the local grid that does things to the client under test, on command.
// Copied from Radegast Legacy's contrib tools (projects/radegast/tools/Visitor, written by Claude Opus 5.5) for Sojourn
// Access's checks (tools/access.py), and extended there: Access Visitor by default, what it hears and is sent kept for
// asking, a scripted fixture box, notecard offers, facing and walking.
//
//   Visitor [--port 9886] [--first Access] [--last Visitor] [--password sojourn-local] [--start Atelier/68/38/24]
//           [--login http://127.0.0.1:9700/] [--accept-friends]   (the last: takes any friendship offered)
// Commands, one per TCP connection (QuireUia send --port 9886 ... works as a client):
//   say TEXT | shout TEXT | whisper TEXT | emote TEXT     local chat
//   im UUID TEXT                                          instant message
//   group create NAME | invite GROUP UUID | chat GROUP TEXT | notice GROUP SUBJECT | TEXT
//   tplure UUID TEXT | friend UUID                        teleport offer / friendship offer
//   offer UUID                                            offer it the notecard "Access Card" from the visitor's inventory
//   rez NAME X Y Z                                        a 0.5 m box with that name at region coordinates
//   chest NAME X Y Z                                      a linked set with a touch script in a part "Latch"
//   treasure [NAME] X Y Z                                 a chest (default "Treasure Chest") whose root does nothing on a
//                                                         touch; only its unnamed lid answers ("chest lid touched by ...")
//   tipjar [NAME] X Y Z                                   a 0.4 m box (default "Access Tip Jar") that takes money: pay buttons
//                                                         L$10 and L$50 or any amount, left click set to pay, and says "paid N by NAME"
//   forsale NAME X Y Z PRICE                              a 0.4 m box for sale as a copy at that price; everyone may copy it
//   facebox [NAME] X Y Z                                  a 0.5 m box (default name "Access Facebox") whose script says
//                                                         "NAME touched face F st <s, t, 0>" for each touch
//   fixture [NAME] X Y Z                                  the Access fixture (default name "Access Fixture"): a box
//                                                         holding the notecard and a script that obeys the visitor on
//                                                         channel 7 (it must stay within 20 m of the visitor)
//   fmenu UUID | ftext UUID | fperm UUID | fgive UUID     the fixture sends that avatar a script menu (Red, Green,
//                                                         Blue; says "picked X"), a text box ("typed X"), a request
//                                                         to animate it ("permission N") or the notecard
//   embedcard NAME UUID                                   a notecard NAME holding a landmark ("NAME Landmark", its place
//                                                         in the text's second line) and a notecard ("NAME Inner", at the
//                                                         end), full rights, in the viewer's own format, offered to UUID
//   conferenceline UUID TEXT                              TEXT to UUID as a conference's line, as Second Life sends one
//   fmenu12 UUID                                          a script menu of twelve (One ... Ten, Next, Back)
//   fdance UUID                                           a dance machine's menu (Waltz, Tango): each pick says
//                                                         "danced X" and sends the menu again
//   heard [N] | events [N] [since S]                      the last N chat lines heard / other things received (script
//                                                         dialogs, permission questions, IMs and offers), as JSON lines
//                                                         with a running number "n"; "since S" keeps those after n=S
//   dreply LABEL                                          answer the newest script dialog: a button, or a text box's text
//   qreply yes|no                                         answer the newest permission question
//   face DEGREES | goto X Y Z                             turn to a heading (0 east, 90 north) / walk to a spot
//   tp REGION X Y Z | fly on|off                        teleport (waits for the arrival) / fly or land
//   sofa [NAME] X Y Z                                     a seat (default "Access Sofa") that renames itself NAME for
//                                                         two while sat on or after a touch, back on standing or the
//                                                         next touch ("NAME renamed to N")
//   cart [NAME] X Y Z                                     a cart to drive seated (default "Walk Cart"): takes the sitter's
//                                                         movement keys, moves on forward and back, turns on left and right
//   kart [NAME] X Y Z [facing DEG] [train SECS] [sound UUID] [throttle STEP] [endx X] [hover 1] [lose N] [start P] [quiet 1]
//                                                         a physical car vehicle (default "Walk Kart") for seated
//                                                         crossings, heading east or DEG (0 east, 90 north); with train,
//                                                         the forward key starts it driving on its own for SECS, keys or
//                                                         not, as a train does; with a sound, it loops it while sat on;
//                                                         with throttle, a throttle train: each forward press opens it
//                                                         STEP% (of 10 m/s), each back press closes it STEP%, on past 0
//                                                         into reverse, each change said ("throttle 35"); with endx, its
//                                                         track ends once it's east of X: "sensor : No further track
//                                                         guide detected", then twice 2 s apart while still moving
//                                                         "no_sensor : No Guide found" (Access's stop at a track's end);
//                                                         hover 1: shows "Throttle N%" and its speed as floating text;
//                                                         lose N: every Nth press (forward or back) is ignored, as one
//                                                         that never reached the script; start P: the throttle at P%
//                                                         once sat on; quiet 1: the throttle isn't said aloud (its
//                                                         changes always go to the visitor by llOwnerSay)
//   switch X Y Z [CHANNEL] [long]                         a rail switch as the SLRR standard describes one (Sojourn
//                                                         Access's rail switches): a "Switch Stand" whose description is
//                                                         "Walk Junction.N~Main~Walk Halt~North Station~East Yard~N",
//                                                         1.8 m north of X Y, a touch part over it, and two guide rails
//                                                         10 m long running east, the main one 3 m north and the branch 4.2; a
//                                                         touch, or "Main" or "Branch" on CHANNEL (133), sets it (says
//                                                         "switch Main" / "switch Branch"); long: each rail four sections
//                                                         of their own 14 m apart, the branch's parting north to 10 m, its
//                                                         furthest 43 m from the stand (as the Linden railroad's reach on
//                                                         past their stands, 2026-11-02)
//   rails Z KIND,X,Y,DEG,LEN;...                          guide rails laid as given, all at height Z, for a track
//                                                         (Sojourn Access's rail switches followed along the track):
//                                                         each a 0.5 m square box LEN long along its x axis, facing DEG
//                                                         (0 east, 90 north); KIND G a "Guide", solid, A an "Alternate
//                                                         Guide", phantom (no scripts: they don't change)
//   stand X Y Z DESC                                      a phantom "Switch Stand" with that description and no script
//   cleanup                                               take every object it rezzed in this run to its trash
//   derez NAME                                            take every object it rezzed with that name to its trash
//   land show X Y | land divide W S E N | land sell X Y PRICE BUYER_UUID | land pass X Y PRICE HOURS | land tidy
//                                                         the region owner's chores (run as Estate Keeper) for About
//                                                         Land's step: a parcel divided off and sold to one buyer, passes
//                                                         sold on one, and tidy: every parcel not its own reclaimed and
//                                                         the region joined into one again
//   pos | whereis | quit                                  "REGION <x, y, z> heading DEG" (as the region has it)
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using LibreMetaverse;
using LibreMetaverse.Packets;

static class Program
{
    const int FixtureChannel = 7;
    const string CardName = "Access Card";
    const string CardText = "Access Card\n\nA notecard from the Access Visitor, for Sojourn Access's checks.\nIts second line.\n";

    static GridClient client;
    static readonly ConcurrentDictionary<UUID, bool> joinedGroupChats = new();
    static readonly List<string> heard = new(), events = new();
    static int counter;
    static ScriptDialogEventArgs lastDialog;
    static ScriptQuestionEventArgs lastQuestion;
    static readonly List<uint> rezzed = new();
    static readonly Dictionary<uint, string> named = new();   // what we named the objects we rezzed, for derez NAME
    static InventoryItem card;

    static string Opt(List<string> a, string name, string dflt)
    {
        int i = a.IndexOf(name);
        return i >= 0 && i + 1 < a.Count ? a[i + 1] : dflt;
    }

    static async Task<int> Main(string[] argv)
    {
        var a = argv.ToList();
        int port = int.Parse(Opt(a, "--port", "9886"));
        var first = Opt(a, "--first", "Access");
        var last = Opt(a, "--last", "Visitor");
        var pw = Opt(a, "--password", "sojourn-local");
        var start = Opt(a, "--start", "Atelier/68/38/24").Split('/');
        var login = Opt(a, "--login", "http://127.0.0.1:9700/");

        client = new GridClient();
        var lp = client.Network.DefaultLoginParams(first, last, pw, "AccessVisitor", "1.0");
        lp.URI = login;
        lp.Start = NetworkManager.StartLocation(start[0], int.Parse(start[1]), int.Parse(start[2]), int.Parse(start[3]));
        Hook();
        // --accept-friends: a friend for the test's friends-online-at-login check, taking any friendship offered
        if (a.Contains("--accept-friends"))
            client.Friends.FriendshipOffered += (s, e) => client.Friends.AcceptFriendship(e.AgentID, e.SessionID);
        if (!await client.Network.LoginAsync(lp, CancellationToken.None))
        {
            Console.WriteLine("login failed: " + client.Network.LoginMessage);
            return 1;
        }
        Console.WriteLine($"logged in as {client.Self.Name} {client.Self.AgentID} at {client.Network.CurrentSim?.Name} {client.Self.SimPosition}");

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var quit = new CancellationTokenSource();
        while (!quit.IsCancellationRequested)
        {
            using var c = await listener.AcceptTcpClientAsync();
            var s = c.GetStream();
            var line = await new StreamReader(s, Encoding.UTF8).ReadLineAsync() ?? "";
            string reply;
            try { reply = await Command(line.Trim(), quit); }
            catch (Exception e) { reply = "ERR " + e.Message; }
            Console.WriteLine($"> {line.Trim()}\n< {reply}");
            var b = Encoding.UTF8.GetBytes(reply + "\n");
            await s.WriteAsync(b);
        }
        await client.Network.LogoutAsync(CancellationToken.None);
        return 0;
    }

    // ---- what arrives ----

    static string J(string s) => s == null ? "null" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "").Replace("\t", "\\t") + "\"";

    static void Keep(List<string> into, string kind, string fields)
    {
        lock (into)
        {
            var line = $"{{\"n\":{Interlocked.Increment(ref counter)},\"t\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"kind\":{J(kind)},{fields}}}";
            into.Add(line);
            if (into.Count > 500) into.RemoveAt(0);
            Console.WriteLine(line);
        }
    }

    static void Hook()
    {
        client.Self.ChatFromSimulator += (s, e) =>
        {
            if (e.Type == ChatType.StartTyping || e.Type == ChatType.StopTyping || e.Type == ChatType.Debug) return;
            if (string.IsNullOrEmpty(e.Message)) return;
            Keep(heard, "chat", $"\"type\":{J(e.Type.ToString())},\"source\":{J(e.SourceType.ToString())},\"from\":{J(e.FromName)}," +
                $"\"id\":{J(e.SourceID.ToString())},\"msg\":{J(e.Message)}");
        };
        client.Self.IM += (s, e) =>
        {
            var im = e.IM;
            if (im.Dialog == InstantMessageDialog.StartTyping || im.Dialog == InstantMessageDialog.StopTyping) return;
            Keep(events, "im", $"\"dialog\":{J(im.Dialog.ToString())},\"from\":{J(im.FromAgentName)},\"id\":{J(im.FromAgentID.ToString())}," +
                $"\"msg\":{J(im.Message)}");
        };
        client.Self.ScriptDialog += (s, e) =>
        {
            lastDialog = e;
            Keep(events, "dialog", $"\"object\":{J(e.ObjectName)},\"objectId\":{J(e.ObjectID.ToString())},\"channel\":{e.Channel}," +
                $"\"msg\":{J(e.Message)},\"buttons\":[{string.Join(",", e.ButtonLabels.Select(J))}]");
        };
        client.Self.ScriptQuestion += (s, e) =>
        {
            lastQuestion = e;
            Keep(events, "question", $"\"object\":{J(e.ObjectName)},\"owner\":{J(e.ObjectOwnerName)},\"taskId\":{J(e.TaskID.ToString())}," +
                $"\"perms\":{(int)e.Questions},\"names\":{J(e.Questions.ToString())}");
        };
        // Offers stay unanswered (declined when they time out); the IM above has logged them
        client.Inventory.InventoryObjectOffered += (s, e) => e.Accept = false;
    }

    static string Recent(List<string> from, string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        int since = -1;
        int si = parts.IndexOf("since");
        if (si >= 0 && si + 1 < parts.Count) { since = int.Parse(parts[si + 1]); parts.RemoveRange(si, 2); }
        int n = parts.Count > 0 ? int.Parse(parts[0]) : 20;
        lock (from)
        {
            var lines = from.Where(l => since < 0 || int.Parse(Regex.Match(l, "\"n\":(\\d+)").Groups[1].Value) > since).ToList();
            return lines.Count == 0 ? "none" : string.Join("\n", lines.Skip(Math.Max(0, lines.Count - n)));
        }
    }

    // ---- commands ----

    static (string head, string rest) Split(string s)
    {
        int i = s.IndexOf(' ');
        return i < 0 ? (s, "") : (s.Substring(0, i), s.Substring(i + 1).Trim());
    }

    static async Task<string> Command(string line, CancellationTokenSource quit)
    {
        var (verb, rest) = Split(line);
        switch (verb)
        {
            case "say": client.Self.Chat(rest, 0, ChatType.Normal); return "ok";
            case "shout": client.Self.Chat(rest, 0, ChatType.Shout); return "ok";
            case "whisper": client.Self.Chat(rest, 0, ChatType.Whisper); return "ok";
            case "emote": client.Self.Chat("/me " + rest, 0, ChatType.Normal); return "ok";
            case "im":
                {
                    var (to, text) = Split(rest);
                    client.Self.InstantMessage(UUID.Parse(to), text);
                    return "ok";
                }
            case "tplure":
                {
                    var (to, text) = Split(rest);
                    client.Self.SendTeleportLure(UUID.Parse(to), text);
                    return "ok";
                }
            case "friend": client.Friends.OfferFriendship(UUID.Parse(rest)); return "ok";
            case "offer":
                {
                    var item = await Card();
                    client.Inventory.GiveItem(item.UUID, item.Name, AssetType.Notecard, UUID.Parse(rest), false);
                    return "offered " + item.Name;
                }
            case "embedcard": return await EmbedCard(rest);
            // a line of a conference as SL's event queue sends one: dialog 0, the session not the pair's. The session is the
            // visitor's own id, so a reply into it (a session's message, addressed to the session's id) reaches the visitor;
            // the packet by hand, as LibreMetaverse's InstantMessage turns that session into the pair's
            case "conferenceline":
                {
                    var words = rest.Split(' ', 2);
                    var packet = new ImprovedInstantMessagePacket();
                    packet.AgentData.AgentID = client.Self.AgentID;
                    packet.AgentData.SessionID = client.Self.SessionID;
                    packet.MessageBlock.Dialog = (byte)InstantMessageDialog.MessageFromAgent;
                    packet.MessageBlock.FromAgentName = Utils.StringToBytes(client.Self.Name);
                    packet.MessageBlock.ID = client.Self.AgentID;
                    packet.MessageBlock.ToAgentID = UUID.Parse(words[0]);
                    packet.MessageBlock.Message = Utils.StringToBytes(words[1]);
                    packet.MessageBlock.BinaryBucket = Encoding.UTF8.GetBytes("Visitor's Conference\0");
                    packet.MessageBlock.RegionID = client.Network.CurrentSim.ID;
                    client.Network.SendPacket(packet);
                    return "ok";
                }
            case "pos": case "whereis":
                {
                    client.Self.SimRotation.GetEulerAngles(out _, out _, out var yaw);
                    return $"{client.Network.CurrentSim?.Name} {client.Self.SimPosition} heading {Math.Round(yaw * 180 / Math.PI)}";
                }
            case "quit": quit.Cancel(); return "bye";
            case "group": return await Group(rest);
            case "rez": return await Rez(rest);
            case "chest": return await Chest(rest);
            case "treasure": return await Treasure(rest);
            case "tipjar": return await TipJar(rest);
            case "forsale": return await ForSale(rest);
            case "fixture": return await Fixture(rest);
            case "facebox": return await Facebox(rest);
            case "sofa": return await Sofa(rest);
            case "cart": return await Cart(rest);
            case "kart": return await Kart(rest);
            case "switch": return await Switch(rest);
            case "rails": return await Rails(rest);
            case "stand": return await Stand(rest);
            case "fmenu": return Tell("menu", rest);
            case "fmenu12": return Tell("menu12", rest);
            case "fdance": return Tell("dance", rest);
            case "ftext": return Tell("text", rest);
            case "fperm": return Tell("perm", rest);
            case "fgive": return Tell("give", rest);
            case "heard": return Recent(heard, rest);
            case "events": return Recent(events, rest);
            case "dreply":
                {
                    var d = lastDialog;
                    if (d == null) return "ERR no dialog yet";
                    int idx = Math.Max(0, d.ButtonLabels.IndexOf(rest));
                    client.Self.ReplyToScriptDialog(d.Channel, idx, rest, d.ObjectID);
                    return $"replied {rest} to {d.ObjectName} on {d.Channel}";
                }
            case "qreply":
                {
                    var q = lastQuestion;
                    if (q == null) return "ERR no question yet";
                    client.Self.ScriptQuestionReply(q.Simulator, q.ItemID, q.TaskID, rest == "yes" ? q.Questions : ScriptPermission.None);
                    return $"answered {rest} to {q.ObjectName}";
                }
            case "face":
                {
                    var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(double.Parse(rest) * Math.PI / 180));
                    client.Self.Movement.BodyRotation = rot;
                    client.Self.Movement.HeadRotation = rot;
                    client.Self.Movement.SendUpdate(true);
                    return "ok";
                }
            case "goto":
                {
                    var p = rest.Split(' ').Select(float.Parse).ToArray();
                    client.Self.AutoPilotLocal((int)p[0], (int)p[1], p.Length > 2 ? p[2] : client.Self.SimPosition.Z);
                    return "walking";
                }
            case "tp":
                {
                    // tp REGION X Y Z (the region's name may have spaces: the last three words are the place)
                    var w = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (w.Length < 4) return "ERR tp REGION X Y Z";
                    var region = string.Join(" ", w[..^3]);
                    var at = new Vector3(float.Parse(w[^3]), float.Parse(w[^2]), float.Parse(w[^1]));
                    var from = client.Network.CurrentSim;
                    bool ok = await client.Self.TeleportAsync(region, at);
                    // LibreMetaverse reports a local teleport failed now and then as it arrives (the app's
                    // AgentSession.TeleportAsync has the same check): there within 3 s, it arrived
                    if (!ok && string.Equals(from?.Name, region, StringComparison.OrdinalIgnoreCase))
                        for (int i = 0; i < 30 && !ok; i++)
                        {
                            ok = ReferenceEquals(client.Network.CurrentSim, from)
                                && Vector2.Distance(new Vector2(client.Self.SimPosition.X, client.Self.SimPosition.Y), new Vector2(at.X, at.Y)) < 5;
                            if (!ok) await Task.Delay(100);
                        }
                    if (!ok) return $"ERR the teleport failed ({client.Self.TeleportMessage})";
                    // to another region: the position is the old region's until the new one's movement completes
                    for (int i = 0; i < 100 && !ReferenceEquals(client.Network.CurrentSim, from) && client.Network.CurrentSim is { AgentMovementComplete: false }; i++)
                        await Task.Delay(100);
                    return $"arrived {client.Network.CurrentSim?.Name} {client.Self.SimPosition}";
                }
            case "fly":
                client.Self.Fly(rest.Trim() != "off");
                return "ok";
            case "cleanup": return Cleanup();
            case "derez": return Derez(rest);
            case "land": return await Land(rest);
            default: return "ERR unknown command";
        }
    }

    // ---- land, as the region's owner ----

    static async Task<Parcel> ParcelAt(float x, float y)
    {
        var sim = client.Network.CurrentSim;
        var got = new TaskCompletionSource<Parcel>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ParcelPropertiesEventArgs> h = (_, e) =>
        {
            if (e.Simulator == sim && e.Parcel is { } p && p.AABBMin.X <= x && p.AABBMin.Y <= y && p.AABBMax.X >= x && p.AABBMax.Y >= y) got.TrySetResult(p);
        };
        client.Parcels.ParcelProperties += h;
        try
        {
            client.Parcels.RequestParcelProperties(sim, y + 1, x + 1, y - 1, x - 1, 0, false);
            return await Task.WhenAny(got.Task, Task.Delay(8000)) == got.Task ? got.Task.Result : null;
        }
        finally { client.Parcels.ParcelProperties -= h; }
    }

    static string ParcelWords(Parcel p) =>
        $"parcel {p.LocalID} owner {p.OwnerID} area {p.Area} sale {((p.Flags & ParcelFlags.ForSale) != 0 ? p.SalePrice.ToString() : "no")} name {p.Name}";

    static async Task<string> Land(string rest)
    {
        var (sub, args) = Split(rest);
        var a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        float F(int i) => float.Parse(a[i], System.Globalization.CultureInfo.InvariantCulture);
        var sim = client.Network.CurrentSim;
        switch (sub)
        {
            case "show":
            {
                var p = await ParcelAt(F(0), F(1));
                return p == null ? "ERR the region didn't describe it" : ParcelWords(p);
            }
            case "divide":
            {
                client.Parcels.ParcelSubdivide(sim, F(0), F(1), F(2), F(3));
                await Task.Delay(2500);
                var p = await ParcelAt((F(0) + F(2)) / 2, (F(1) + F(3)) / 2);
                return p == null ? "ERR the region didn't describe it" : ParcelWords(p);
            }
            case "sell" or "pass":
            {
                var p = await ParcelAt(F(0), F(1));
                if (p == null) return "ERR the region didn't describe it";
                if (sub == "sell")
                {
                    p.Flags |= ParcelFlags.ForSale;
                    p.SalePrice = int.Parse(a[2]);
                    p.AuthBuyerID = UUID.Parse(a[3]);
                }
                else
                {
                    p.Flags |= ParcelFlags.UsePassList;
                    p.PassPrice = int.Parse(a[2]);
                    p.PassHours = F(3);
                }
                p.Update(client, sim, false);
                await Task.Delay(1500);
                var after = await ParcelAt(F(0), F(1));
                return after == null ? "ERR the region didn't describe it" : ParcelWords(after);
            }
            case "tidy":
            {
                // a fresh login's list of the region's parcels is the region's as it is now
                using (var cts = new CancellationTokenSource(15000))
                    try { await client.Parcels.RequestAllSimParcelsAsync(sim, cts.Token); } catch (OperationCanceledException) { }
                var parcels = sim.Parcels.Values.ToList();
                int reclaimed = 0;
                foreach (var p in parcels.Where(p => p.OwnerID != client.Self.AgentID))
                {
                    client.Parcels.Reclaim(sim, p.LocalID);
                    reclaimed++;
                }
                if (reclaimed > 0) await Task.Delay(2500);
                if (parcels.Count > 1)
                {
                    client.Parcels.ParcelJoin(sim, 0, 0, sim.SizeX, sim.SizeY);
                    await Task.Delay(3000);
                }
                var whole = await ParcelAt(sim.SizeX / 2f, sim.SizeY / 2f);
                return $"tidied: {parcels.Count} parcels, {reclaimed} reclaimed; now {(whole == null ? "not known" : ParcelWords(whole))}";
            }
        }
        return "ERR land show|divide|sell|pass|tidy";
    }

    static string Tell(string what, string who)
    {
        if (!UUID.TryParse(who, out var id)) return "ERR f" + what + " UUID";
        client.Self.Chat(what + " " + id, FixtureChannel, ChatType.Normal);
        return "ok";
    }

    static async Task<string> Group(string rest)
    {
        var (sub, args) = Split(rest);
        switch (sub)
        {
            case "create":
                {
                    var done = new TaskCompletionSource<string>();
                    EventHandler<GroupCreatedReplyEventArgs> h = (s, e) =>
                        done.TrySetResult(e.Success ? e.GroupID.ToString() : "ERR " + e.Message);
                    client.Groups.GroupCreatedReply += h;
                    var g = new LibreMetaverse.Group { Name = args, Charter = "Quire test group", OpenEnrollment = false, ShowInList = false };
                    client.Groups.RequestCreateGroup(g);
                    var r = await Task.WhenAny(done.Task, Task.Delay(15000));
                    client.Groups.GroupCreatedReply -= h;
                    return r == done.Task ? done.Task.Result : "ERR timeout";
                }
            case "invite":
                {
                    var (group, who) = Split(args);
                    client.Groups.Invite(UUID.Parse(group), new List<UUID> { UUID.Zero }, UUID.Parse(who));
                    return "ok";
                }
            case "chat":
                {
                    var (group, text) = Split(args);
                    var id = UUID.Parse(group);
                    if (!joinedGroupChats.ContainsKey(id))
                    {
                        var joined = new TaskCompletionSource<bool>();
                        EventHandler<GroupChatJoinedEventArgs> h = (s, e) => { if (e.SessionID == id) joined.TrySetResult(e.Success); };
                        client.Self.GroupChatJoined += h;
                        client.Self.RequestJoinGroupChat(id);
                        await Task.WhenAny(joined.Task, Task.Delay(10000));
                        client.Self.GroupChatJoined -= h;
                        joinedGroupChats[id] = true;
                    }
                    client.Self.InstantMessageGroup(id, text);
                    return "ok";
                }
            case "notice":
                {
                    var (group, body) = Split(args);
                    int bar = body.IndexOf(" | ");
                    var subject = bar < 0 ? body : body.Substring(0, bar);
                    var text = bar < 0 ? "" : body.Substring(bar + 3);
                    client.Groups.SendGroupNotice(UUID.Parse(group), new GroupNotice { Subject = subject, Message = text, OwnerID = client.Self.AgentID });
                    return "ok";
                }
            default: return "ERR group create|invite|chat|notice";
        }
    }

    // ---- objects ----

    static (string name, Vector3 pos)? NameAndPos(string rest, string dfltName)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null;
        var name = parts.Length > 3 ? string.Join(" ", parts.Take(parts.Length - 3)) : dfltName;
        if (name == null) return null;
        return (name, new Vector3(float.Parse(parts[^3]), float.Parse(parts[^2]), float.Parse(parts[^1])));
    }

    static async Task<string> Rez(string rest)
    {
        // rez NAME X Y Z [size SX SY SZ]: a box, half a metre by default
        var scale = new Vector3(0.5f);
        if (rest.IndexOf(" size ", StringComparison.Ordinal) is var at and >= 0)
        {
            var s = rest[(at + 6)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (s.Length != 3) return "ERR rez NAME X Y Z size SX SY SZ";
            scale = new Vector3(float.Parse(s[0]), float.Parse(s[1]), float.Parse(s[2]));
            rest = rest[..at];
        }
        if (NameAndPos(rest, null) is not var (name, pos)) return "ERR rez NAME X Y Z [size SX SY SZ]";
        var prim = await RezBox(pos, scale);
        if (prim == null) return "ERR no object came back";
        var sim = client.Network.CurrentSim;
        Named(prim.LocalID, name);
        client.Objects.DeselectObject(sim, prim.LocalID);
        return $"rezzed {name} local {prim.LocalID} id {prim.ID}";
    }

    static async Task<Primitive> RezBox(Vector3 pos, Vector3 scale)
    {
        var got = new TaskCompletionSource<Primitive>();
        EventHandler<PrimEventArgs> h = (s, e) =>
        {
            if (e.IsNew && e.Prim.ParentID == 0 && Vector3.Distance(e.Prim.Position, pos) < 1f)
                got.TrySetResult(e.Prim);
        };
        client.Objects.ObjectUpdate += h;
        client.Objects.AddPrim(client.Network.CurrentSim, ObjectManager.BuildBasicShape(PrimType.Box), UUID.Zero, pos, scale, Quaternion.Identity);
        var r = await Task.WhenAny(got.Task, Task.Delay(10000));
        client.Objects.ObjectUpdate -= h;
        if (r != got.Task) return null;
        lock (rezzed) rezzed.Add(got.Task.Result.LocalID);
        return got.Task.Result;
    }

    static async Task<InventoryItem> UploadScript(string name, string src)
    {
        var folder = client.Inventory.FindFolderForType(FolderType.LSLText);
        var item = await client.Inventory.CreateItemAsync(folder, name, "", AssetType.LSLText, UUID.Zero,
            InventoryType.LSL, PermissionMask.All);
        if (item == null) throw new Exception("couldn't create the script");
        var up = await client.Inventory.RequestUpdateScriptAgentInventoryAsync(Encoding.UTF8.GetBytes(src), item.UUID, true);
        // OpenSim reports compileSuccess false for scripts in agent inventory; it compiles them in the task
        if (!up.uploadSuccess)
            throw new Exception("script upload: " + up.uploadStatus + " " + string.Join("; ", up.compileMessages ?? new List<string>()));
        return item;
    }

    static async Task<string> Chest(string rest)
    {
        if (NameAndPos(rest, null) is not var (name, pos)) return "ERR chest NAME X Y Z";
        var sim = client.Network.CurrentSim;
        var root = await RezBox(pos, new Vector3(0.8f, 0.5f, 0.1f));
        var plain = await RezBox(pos - new Vector3(0, 0, 0.3f), new Vector3(0.8f, 0.5f, 0.5f));
        var latch = await RezBox(pos + new Vector3(0.45f, 0, -0.1f), new Vector3(0.1f, 0.1f, 0.1f));
        if (root == null || plain == null || latch == null) return "ERR a part didn't come back";
        Named(root.LocalID, name);
        client.Objects.SetName(sim, latch.LocalID, "Latch");
        var item = await UploadScript("Latch touch",
            "default { touch_start(integer n) { llSay(0, llGetObjectName() + \" touched by \" + llDetectedName(0) + \", link \" + (string)llDetectedLinkNumber(0)); } }");
        client.Inventory.CopyScriptToTask(latch.LocalID, item, true);
        await Task.Delay(1500);

        // ObjectLink: the first one becomes the root
        client.Objects.LinkPrims(sim, new List<uint> { root.LocalID, plain.LocalID, latch.LocalID });
        await Task.Delay(1500);
        client.Objects.DeselectObjects(sim, new[] { root.LocalID, plain.LocalID, latch.LocalID });
        return $"chest {name}: root local {root.LocalID}, plain {plain.LocalID}, latch {latch.LocalID}";
    }

    /// <summary>The Linden sunken city's Treasure Chest 6 as a blind tester met it: a chest whose root does
    /// nothing when touched; only a child prim, its lid, unnamed as builders leave parts, answers ("chest lid touched by
    /// NAME, link N, face F"). Also a named latch in front (no script) and an unnamed foot below, so parts by name, by
    /// what and where they are, and the one that takes touches can all be told apart.</summary>
    static async Task<string> Treasure(string rest)
    {
        if (NameAndPos(rest, "Treasure Chest") is not var (name, pos)) return "ERR treasure [NAME] X Y Z";
        var sim = client.Network.CurrentSim;
        var body = await RezBox(pos, new Vector3(0.8f, 0.5f, 0.4f));
        var lid = await RezBox(pos + new Vector3(0, 0, 0.24f), new Vector3(0.8f, 0.5f, 0.08f));
        var latch = await RezBox(pos + new Vector3(0, -0.28f, 0.1f), new Vector3(0.1f, 0.06f, 0.1f));
        var foot = await RezBox(pos + new Vector3(0.3f, 0.15f, -0.24f), new Vector3(0.1f, 0.1f, 0.08f));
        if (body == null || lid == null || latch == null || foot == null) return "ERR a part didn't come back";
        Named(body.LocalID, name);
        client.Objects.SetName(sim, latch.LocalID, "Latch");
        var script = await UploadScript("Chest lid",
            "default { state_entry() { llSay(0, \"chest lid ready\"); } "
            + "touch_start(integer n) { llSay(0, \"chest lid touched by \" + llDetectedName(0) + \", link \" + (string)llDetectedLinkNumber(0) "
            + "+ \", face \" + (string)llDetectedTouchFace(0)); } }");
        int mark = counter;
        client.Inventory.CopyScriptToTask(lid.LocalID, script, true);
        bool ready = false;
        for (int i = 0; i < 40 && !ready; i++)
        {
            await Task.Delay(250);
            lock (heard)
                ready = heard.Any(l => l.Contains("chest lid ready") && int.Parse(Regex.Match(l, "\"n\":(\\d+)").Groups[1].Value) > mark);
        }
        if (!ready) return $"ERR treasure {name}: the lid's script didn't say it was ready";
        // ObjectLink: the first one becomes the root
        client.Objects.LinkPrims(sim, new List<uint> { body.LocalID, lid.LocalID, latch.LocalID, foot.LocalID });
        await Task.Delay(1500);
        client.Objects.DeselectObjects(sim, new[] { body.LocalID, lid.LocalID, latch.LocalID, foot.LocalID });
        return $"treasure {name} id {body.ID}: lid {lid.ID}, latch {latch.ID}, foot {foot.ID}";
    }

    /// <summary>A tip jar, as the blind tester met them at a club (2026-11-05): pays, never touches.</summary>
    static async Task<string> TipJar(string rest)
    {
        if (NameAndPos(rest, "Access Tip Jar") is not var (name, pos)) return "ERR tipjar [NAME] X Y Z";
        var sim = client.Network.CurrentSim;
        var jar = await RezBox(pos, new Vector3(0.4f, 0.4f, 0.4f));
        if (jar == null) return "ERR the jar didn't come back";
        Named(jar.LocalID, name);
        var script = await UploadScript("Tip jar",
            "default { state_entry() { llSetPayPrice(PAY_DEFAULT, [10, 50, PAY_HIDE, PAY_HIDE]); llSay(0, \"tip jar ready\"); } "
            + "money(key who, integer n) { llSay(0, \"paid \" + (string)n + \" by \" + llKey2Name(who)); } }");
        int mark = counter;
        client.Inventory.CopyScriptToTask(jar.LocalID, script, true);
        bool ready = false;
        for (int i = 0; i < 40 && !ready; i++)
        {
            await Task.Delay(250);
            lock (heard)
                ready = heard.Any(l => l.Contains("tip jar ready") && int.Parse(Regex.Match(l, "\"n\":(\\d+)").Groups[1].Value) > mark);
        }
        if (!ready) return $"ERR tipjar {name}: its script didn't say it was ready";
        client.Network.SendPacket(new ObjectClickActionPacket
        {
            AgentData = { AgentID = client.Self.AgentID, SessionID = client.Self.SessionID },
            ObjectData = new[] { new ObjectClickActionPacket.ObjectDataBlock { ObjectLocalID = jar.LocalID, ClickAction = (byte)ClickAction.Pay } },
        }, sim);
        await Task.Delay(500);
        client.Objects.DeselectObject(sim, jar.LocalID);
        return $"tipjar {name} id {jar.ID}";
    }

    static async Task<string> ForSale(string rest)
    {
        int cut = rest.TrimEnd().LastIndexOf(' ');
        if (cut < 0 || !int.TryParse(rest.TrimEnd()[(cut + 1)..], out int price) || NameAndPos(rest[..cut], "Access Vendor") is not var (name, pos))
            return "ERR forsale NAME X Y Z PRICE";
        var sim = client.Network.CurrentSim;
        var box = await RezBox(pos, new Vector3(0.4f, 0.4f, 0.4f));
        if (box == null) return "ERR the box didn't come back";
        Named(box.LocalID, name);
        client.Objects.SetPermissions(sim, new List<uint> { box.LocalID }, PermissionWho.NextOwner, PermissionMask.Copy | PermissionMask.Modify | PermissionMask.Transfer, true);
        client.Objects.SetPermissions(sim, new List<uint> { box.LocalID }, PermissionWho.Everyone, PermissionMask.Copy, true);
        client.Objects.SetSaleInfo(sim, box.LocalID, SaleType.Copy, price);
        await Task.Delay(500);
        client.Objects.DeselectObject(sim, box.LocalID);
        return $"forsale {name} id {box.ID}";
    }

    const string FixtureScript = """
        // Access fixture (projects/sojourn/tools/visitor): on its owner's word on channel 7, "menu|text|perm|give KEY",
        // it sends that avatar a script menu, a text box, a request to animate them or its notecard, and says what came back.
        integer chMenu;
        integer chText;
        integer chDance;
        default
        {
            state_entry()
            {
                chMenu = -70000 - (integer)llFrand(100000.0);
                chText = chMenu - 1;
                chDance = chMenu - 2;
                llListen(7, "", llGetOwner(), "");
                llListen(chMenu, "", NULL_KEY, "");
                llListen(chText, "", NULL_KEY, "");
                llListen(chDance, "", NULL_KEY, "");
                llSay(0, "Access fixture ready");
            }
            on_rez(integer p) { llResetScript(); }
            listen(integer c, string name, key id, string msg)
            {
                if (c == chMenu) { llSay(0, "picked " + msg); return; }
                if (c == chText) { llSay(0, "typed " + msg); return; }
                // a dance machine's way: each pick answered, and the menu sent again for the next
                if (c == chDance) { llSay(0, "danced " + msg); llDialog(id, "Access Dance: pick a dance.", ["Waltz", "Tango"], chDance); return; }
                list w = llParseString2List(msg, [" "], []);
                string cmd = llList2String(w, 0);
                key who = (key)llList2String(w, 1);
                if (cmd == "menu") llDialog(who, "Access Menu: pick one.", ["Red", "Green", "Blue"], chMenu);
                else if (cmd == "menu12") llDialog(who, "Access Long Menu: pick one.", ["One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Next", "Back"], chMenu);
                else if (cmd == "dance") llDialog(who, "Access Dance: pick a dance.", ["Waltz", "Tango"], chDance);
                else if (cmd == "text") llTextBox(who, "Access Box: type a word.", chText);
                else if (cmd == "perm") llRequestPermissions(who, PERMISSION_TRIGGER_ANIMATION);
                else if (cmd == "give") llGiveInventory(who, "Access Card");
            }
            run_time_permissions(integer perm) { llSay(0, "permission " + (string)perm); }
        }
        """;

    static async Task<string> Fixture(string rest)
    {
        if (NameAndPos(rest, "Access Fixture") is not var (name, pos)) return "ERR fixture [NAME] X Y Z";
        var sim = client.Network.CurrentSim;
        var box = await RezBox(pos, new Vector3(0.5f));
        if (box == null) return "ERR no object came back";
        Named(box.LocalID, name);
        var nc = await Card();
        client.Inventory.UpdateTaskInventory(box.LocalID, nc, sim, false);
        await Task.Delay(1000);
        var script = await UploadScript("Access fixture", FixtureScript);
        int mark = counter;
        client.Inventory.CopyScriptToTask(box.LocalID, script, true);
        client.Objects.DeselectObject(sim, box.LocalID);
        // the script says it's ready from state_entry once it runs in the box
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(250);
            lock (heard)
                if (heard.Any(l => l.Contains("Access fixture ready") && int.Parse(Regex.Match(l, "\"n\":(\\d+)").Groups[1].Value) > mark))
                    return $"fixture {name} local {box.LocalID} id {box.ID}";
        }
        return $"ERR fixture {name} (local {box.LocalID}) rezzed but its script didn't say it was ready";
    }

    /// <summary>A cart to drive (Sojourn Access's walk mode, seated): a box with a sit target whose script takes the
    /// sitter's movement keys and moves itself 0.3 m along its facing for each forward or back control event, turns 6
    /// degrees for each left or right, and says the first press of each ("cart: forward", "cart: left").</summary>
    static async Task<string> Cart(string rest)
    {
        if (NameAndPos(rest, "Walk Cart") is not var (name, pos)) return "ERR cart [NAME] X Y Z";
        var sim = client.Network.CurrentSim;
        var box = await RezBox(pos, new Vector3(1.6f, 1.0f, 0.4f));
        if (box == null) return "ERR no object came back";
        Named(box.LocalID, name);
        var script = await UploadScript("Walk cart",
            "integer K; default { state_entry() { llSitTarget(<0, 0, 0.7>, ZERO_ROTATION); "
            + "K = CONTROL_FWD | CONTROL_BACK | CONTROL_ROT_LEFT | CONTROL_ROT_RIGHT | CONTROL_LEFT | CONTROL_RIGHT | CONTROL_UP | CONTROL_DOWN | CONTROL_LBUTTON; "
            + "llSay(0, llGetObjectName() + \" ready\"); } "
            + "changed(integer c) { if (c & CHANGED_LINK) { key who = llAvatarOnSitTarget(); "
            + "if (who != NULL_KEY) llRequestPermissions(who, PERMISSION_TAKE_CONTROLS); else llSay(0, \"cart empty\"); } } "
            + "run_time_permissions(integer p) { if (p & PERMISSION_TAKE_CONTROLS) { llTakeControls(K, TRUE, FALSE); llSay(0, \"cart controls taken\"); } } "
            + "control(key id, integer level, integer edge) { vector f = <1, 0, 0> * llGetRot(); "
            + "if (level & CONTROL_FWD) llSetPos(llGetPos() + f * 0.3); if (level & CONTROL_BACK) llSetPos(llGetPos() - f * 0.3); "
            + "if (level & (CONTROL_ROT_LEFT | CONTROL_LEFT)) llSetRot(llGetRot() * llEuler2Rot(<0, 0, 6> * DEG_TO_RAD)); "
            + "if (level & (CONTROL_ROT_RIGHT | CONTROL_RIGHT)) llSetRot(llGetRot() * llEuler2Rot(<0, 0, -6> * DEG_TO_RAD)); "
            + "if (edge & level & CONTROL_FWD) llSay(0, \"cart: forward\"); if (edge & level & CONTROL_ROT_LEFT) llSay(0, \"cart: left\"); "
            + "if (edge & level & CONTROL_LBUTTON) llSay(0, \"cart: click\"); if (edge & ~level & CONTROL_LBUTTON) llSay(0, \"cart: click up\"); } }");
        int mark = counter;
        client.Inventory.CopyScriptToTask(box.LocalID, script, true);
        client.Objects.DeselectObject(sim, box.LocalID);
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(250);
            lock (heard)
                if (heard.Any(l => l.Contains(name + " ready") && int.Parse(Regex.Match(l, "\"n\":(\\d+)").Groups[1].Value) > mark))
                    return $"cart {name} local {box.LocalID} id {box.ID}";
        }
        return $"ERR cart {name} (local {box.LocalID}) rezzed but its script didn't say it was ready";
    }

    /// <summary>A physical kart (an LSL car vehicle) for seated border crossings: its script takes the sitter's keys,
    /// turns physical while sat on, drives forward at up to 10 m/s and steers (or, as a train, drives itself once started);
    /// "kart controls taken" when it has them.</summary>
    static async Task<string> Kart(string rest)
    {
        const string usage = "ERR kart [NAME] X Y Z [facing DEG] [train SECS] [sound UUID] [throttle STEP] [endx X] [hover 1] [lose N] [start P] [quiet 1]";
        // the options after the place, in any order
        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        string Option(string key)
        {
            int i = words.IndexOf(key);
            if (i < 0 || i + 1 >= words.Count) return null;
            var value = words[i + 1];
            words.RemoveRange(i, 2);
            return value;
        }
        var loop = Option("sound") ?? "";
        if (loop != "" && !UUID.TryParse(loop, out _)) return usage;
        float facing = 0;
        if (Option("facing") is { } fs && !float.TryParse(fs, out facing)) return usage;
        int train = 0;
        if (Option("train") is { } ts && !int.TryParse(ts, out train)) return usage;
        int step = 0;
        if (Option("throttle") is { } st && (!int.TryParse(st, out step) || step <= 0 || step > 100)) return usage;
        float endx = 0;
        if (Option("endx") is { } ex && !float.TryParse(ex, out endx)) return usage;
        // hover 1: a throttle train shows its throttle and speed as floating text, as the blind tester's partner's train does
        bool hover = Option("hover") == "1";
        // for Access's stop: presses lost on the way (lose N), a throttle opened before the arrows (start P), and nothing
        // said aloud of it (quiet 1)
        int lose = 0, start = 0;
        if (Option("lose") is { } ls && (!int.TryParse(ls, out lose) || lose < 0)) return usage;
        if (Option("start") is { } ss && !int.TryParse(ss, out start)) return usage;
        bool quiet = Option("quiet") == "1";
        rest = string.Join(" ", words);
        if (NameAndPos(rest, "Walk Kart") is not var (name, pos)) return usage;
        var sim = client.Network.CurrentSim;
        var box = await RezBox(pos, new Vector3(2.0f, 1.2f, 0.5f));
        if (box == null) return "ERR no object came back";
        Named(box.LocalID, name);
        if (facing != 0) client.Objects.SetRotation(sim, box.LocalID, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, facing * MathF.PI / 180));
        var script = await UploadScript("Walk kart",
            "integer K; string LOOP = \"" + loop + "\"; integer TRAIN = " + train + "; integer ticks; "
            + "integer STEP = " + step + "; float ENDX = " + endx.ToString(System.Globalization.CultureInfo.InvariantCulture) + "; "
            + "integer thr; integer ended; integer nags; integer nagTicks; integer HOVER = " + (hover ? 1 : 0) + "; integer hoverTicks; "
            + "integer LOSE = " + lose + "; integer START = " + start + "; integer QUIET = " + (quiet ? 1 : 0) + "; integer presses; "
            + "default { state_entry() { llSitTarget(<0, 0, 0.75>, ZERO_ROTATION); "
            + "if (HOVER) llSetText(\"Throttle 0%\\nSpeed 0 km/h\", <1, 1, 1>, 1.0); else llSetText(\"\", ZERO_VECTOR, 0); "
            + "K = CONTROL_FWD | CONTROL_BACK | CONTROL_ROT_LEFT | CONTROL_ROT_RIGHT | CONTROL_LEFT | CONTROL_RIGHT; "
            + "llSetVehicleType(VEHICLE_TYPE_CAR); llSetVehicleFloatParam(VEHICLE_LINEAR_MOTOR_TIMESCALE, 0.4); "
            + "llSetVehicleFloatParam(VEHICLE_LINEAR_MOTOR_DECAY_TIMESCALE, 0.6); llSetVehicleFloatParam(VEHICLE_ANGULAR_MOTOR_TIMESCALE, 0.2); "
            + "llSetVehicleFloatParam(VEHICLE_ANGULAR_MOTOR_DECAY_TIMESCALE, 0.3); llSetVehicleVectorParam(VEHICLE_LINEAR_FRICTION_TIMESCALE, <100, 0.5, 100>); "
            + "llSay(0, llGetObjectName() + \" ready\"); } "
            + "changed(integer c) { if (c & CHANGED_LINK) { key who = llAvatarOnSitTarget(); "
            + "if (who != NULL_KEY) { llSetStatus(STATUS_PHYSICS, TRUE); llRequestPermissions(who, PERMISSION_TAKE_CONTROLS); if (LOOP != \"\") llLoopSound(LOOP, 1.0); "
            + "if (STEP) { thr = START; if (START) llOwnerSay(\"throttle \" + (string)thr); llSetTimerEvent(0.1); } } "
            + "else { llSetStatus(STATUS_PHYSICS, FALSE); llStopSound(); if (STEP) llSetTimerEvent(0); llSay(0, \"kart empty\"); } } } "
            + "run_time_permissions(integer p) { if (p & PERMISSION_TAKE_CONTROLS) { llTakeControls(K, TRUE, FALSE); llSay(0, \"kart controls taken\"); } } "
            // a train drives on its own once started, whatever the keys (ticks of 0.1 s; the motor set again each, as it decays)
            // a throttle train: the motor at the throttle each tick; past ENDX the track ends, said as the blind tester's train says it
            + "timer() { if (STEP) { llSetVehicleVectorParam(VEHICLE_LINEAR_MOTOR_DIRECTION, <thr * 0.1, 0, 0>); "
            + "if (ENDX > 0 && !ended && llGetPos().x > ENDX) { ended = TRUE; nags = 2; nagTicks = 0; llSay(0, \"sensor : No further track guide detected\"); } "
            + "else if (ended && nags > 0 && ++nagTicks >= 20) { nagTicks = 0; if (llVecMag(llGetVel()) > 0.5) { nags--; llSay(0, \"no_sensor : No Guide found\"); } } "
            + "if (HOVER && ++hoverTicks >= 10) { hoverTicks = 0; llSetText(\"Throttle \" + (string)thr + \"%\\nSpeed \" + (string)llRound(llVecMag(llGetVel()) * 3.6) + \" km/h\", <1, 1, 1>, 1.0); } "
            + "return; } "
            + "if (--ticks > 0) llSetVehicleVectorParam(VEHICLE_LINEAR_MOTOR_DIRECTION, <10, 0, 0>); "
            + "else { llSetTimerEvent(0); llSetVehicleVectorParam(VEHICLE_LINEAR_MOTOR_DIRECTION, ZERO_VECTOR); llSay(0, \"kart stopped\"); } } "
            + "control(key id, integer level, integer edge) { if (STEP) { integer was = thr; "
            // (LSL's && evaluates both sides: a modulo by 0 would stop the script)
            + "if (LOSE) if (level & edge & (CONTROL_FWD | CONTROL_BACK)) if (++presses % LOSE == 0) return; "
            + "if (level & edge & CONTROL_FWD) thr += STEP; if (level & edge & CONTROL_BACK) thr -= STEP; "
            + "if (thr > 100) thr = 100; if (thr < -100) thr = -100; "
            + "if (thr != was) { if (!QUIET) llSay(0, \"throttle \" + (string)thr); llOwnerSay(\"throttle \" + (string)thr); } return; } "
            + "if (TRAIN) { if ((level & edge & CONTROL_FWD) && ticks <= 0) { ticks = TRAIN * 10; "
            + "llSetTimerEvent(0.1); llSay(0, \"kart driving\"); } return; } "
            + "vector m = ZERO_VECTOR; if (level & CONTROL_FWD) m.x = 10; if (level & CONTROL_BACK) m.x = -5; "
            + "llSetVehicleVectorParam(VEHICLE_LINEAR_MOTOR_DIRECTION, m); float t = 0; if (level & (CONTROL_ROT_LEFT | CONTROL_LEFT)) t = 1.5; "
            + "if (level & (CONTROL_ROT_RIGHT | CONTROL_RIGHT)) t = -1.5; llSetVehicleVectorParam(VEHICLE_ANGULAR_MOTOR_DIRECTION, <0, 0, t>); } }");
        int mark = counter;
        client.Inventory.CopyScriptToTask(box.LocalID, script, true);
        client.Objects.DeselectObject(sim, box.LocalID);
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(250);
            lock (heard)
                if (heard.Any(l => l.Contains(name + " ready") && int.Parse(Regex.Match(l, "\"n\":(\\d+)").Groups[1].Value) > mark))
                    return $"kart {name} local {box.LocalID} id {box.ID}";
        }
        return $"ERR kart {name} (local {box.LocalID}) rezzed but its script didn't say it was ready";
    }

    /// <summary>A rail switch as the Second Life Railroad's standard has one (the SL wiki's "Second Life Railroad/SLRR
    /// Switch"): a phantom root named "Switch Stand" whose description is "Identifier~Status~Heading Name~Mainline
    /// Name~Branch Name~channel", a part over it taking touches, and invisible guide rails, the one in use named "Guide"
    /// and solid, the other "Alternate Guide" and phantom. Here the rails are objects of their own, set by the stand on a
    /// private channel (the standard doesn't say how its stands reach theirs). A touch flips it; "Main" or "Branch" on
    /// its channel (133 unless given) sets it; each change is said on channel 0 ("switch Branch") for the test.</summary>
    static async Task<string> Switch(string rest)
    {
        var w = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        bool lengthy = w.Remove("long");
        int channel = 133;
        if (w.Count < 3 || !float.TryParse(w[0], out var x) || !float.TryParse(w[1], out var y) || !float.TryParse(w[2], out var z)
            || (w.Count > 3 && !int.TryParse(w[3], out channel)))
            return "ERR switch X Y Z [CHANNEL] [long]";
        var sim = client.Network.CurrentSim;
        var stand = await RezBox(new Vector3(x, y + 1.8f, z + 0.6f), new Vector3(0.3f, 0.3f, 1.2f));
        var touch = await RezBox(new Vector3(x, y + 1.8f, z + 0.8f), new Vector3(0.8f, 0.8f, 1.6f));
        if (stand == null || touch == null) return "ERR a part didn't come back";
        // the rails: one section each, or four (east of the stand, 14 m apart, the branch parting north)
        var rails = new List<(Primitive P, bool Main)>();
        foreach (var (dx, branchY) in lengthy ? new[] { (0f, 4.2f), (14f, 5.5f), (28f, 7.5f), (42f, 10f) } : new[] { (0f, 4.2f) })
        {
            var m = await RezBox(new Vector3(x + dx, y + 3, z + 0.25f), new Vector3(10f, 0.5f, 0.5f));
            var br = await RezBox(new Vector3(x + dx, y + branchY, z + 0.25f), new Vector3(10f, 0.5f, 0.5f));
            if (m == null || br == null) return "ERR a rail didn't come back";
            rails.Add((m, true));
            rails.Add((br, false));
        }
        Named(stand.LocalID, "Switch Stand");
        client.Objects.SetName(sim, touch.LocalID, "Switch Touch");
        client.Objects.SetDescription(sim, stand.LocalID, $"Walk Junction.{channel}~Main~Walk Halt~North Station~East Yard~{channel}");
        foreach (var (r, isMain) in rails)
        {
            Named(r.LocalID, isMain ? "Guide" : "Alternate Guide");
            client.Objects.SetDescription(sim, r.LocalID, isMain ? "main" : "branch");
        }
        await Task.Delay(1000);
        var guide = await UploadScript("Walk guide",
            "string track; integer CH = " + channel + "; set(string s) { integer mine = llToLower(s) == track; "
            + "if (mine) llSetObjectName(\"Guide\"); else llSetObjectName(\"Alternate Guide\"); llSetPrimitiveParams([PRIM_PHANTOM, !mine]); } "
            + "default { state_entry() { track = llGetObjectDesc(); set(\"main\"); llListen(-7133 - CH, \"\", NULL_KEY, \"\"); } "
            + "listen(integer c, string n, key id, string m) { if (llGetOwnerKey(id) == llGetOwner()) set(m); } }");
        foreach (var (r, _) in rails) client.Inventory.CopyScriptToTask(r.LocalID, guide, true);
        var toucher = await UploadScript("Walk switch touch",
            "default { touch_start(integer n) { llMessageLinked(LINK_ROOT, 0, \"toggle\", llDetectedKey(0)); } }");
        client.Inventory.CopyScriptToTask(touch.LocalID, toucher, true);
        var script = await UploadScript("Walk switch stand",
            "string status = \"Main\"; integer CH = " + channel + "; "
            + "set(string s) { status = s; list f = llParseStringKeepNulls(llGetObjectDesc(), [\"~\"], []); "
            + "llSetObjectDesc(llDumpList2String(llListReplaceList(f, [s], 1, 1), \"~\")); llRegionSay(-7133 - CH, s); llSay(0, \"switch \" + s); } "
            + "default { state_entry() { llSetPrimitiveParams([PRIM_PHANTOM, TRUE]); llListen(CH, \"\", NULL_KEY, \"\"); llSay(0, \"switch ready\"); } "
            + "link_message(integer l, integer n, string m, key id) { if (m == \"toggle\") { if (status == \"Main\") set(\"Branch\"); else set(\"Main\"); } } "
            + "listen(integer c, string n, key id, string m) { if (llToLower(m) == \"main\") set(\"Main\"); else if (llToLower(m) == \"branch\") set(\"Branch\"); } }");
        int mark = counter;
        client.Inventory.CopyScriptToTask(stand.LocalID, script, true);
        await Task.Delay(1500);
        // ObjectLink: the first one becomes the root
        client.Objects.LinkPrims(sim, new List<uint> { stand.LocalID, touch.LocalID });
        await Task.Delay(1500);
        client.Objects.DeselectObjects(sim, new[] { stand.LocalID, touch.LocalID }.Concat(rails.Select(r => r.P.LocalID)).ToArray());
        for (int i = 0; i < 40; i++)
        {
            lock (heard)
                if (heard.Any(l => l.Contains("switch ready") && int.Parse(Regex.Match(l, "\"n\":(\\d+)").Groups[1].Value) > mark))
                    return $"switch stand {stand.ID} touch {touch.ID} rails {rails.Count}";
            await Task.Delay(250);
        }
        return $"ERR switch (stand {stand.ID}) rezzed but its script didn't say it was ready";
    }

    /// <summary>Guide rails laid as given, for a track to follow (Sojourn Access's rail switches, 2026-11-03): each a
    /// box 0.5 m square and LEN long along its x axis, turned to DEG, named "Guide" and solid or "Alternate Guide" and
    /// phantom, as the SLRR standard has the rails in use and not.</summary>
    static async Task<string> Rails(string rest)
    {
        const string usage = "ERR rails Z KIND,X,Y,DEG,LEN;...";
        var w = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (w.Length < 2 || !float.TryParse(w[0], out var z)) return usage;
        var sim = client.Network.CurrentSim;
        int n = 0;
        foreach (var spec in w[1].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = spec.Split(',');
            if (f.Length != 5 || (f[0] != "G" && f[0] != "A") || !float.TryParse(f[1], out var x) || !float.TryParse(f[2], out var y)
                || !float.TryParse(f[3], out var deg) || !float.TryParse(f[4], out var len)) return usage;
            var box = await RezBox(new Vector3(x, y, z), new Vector3(len, 0.5f, 0.5f));
            if (box == null) return $"ERR rail {n} didn't come back";
            Named(box.LocalID, f[0] == "G" ? "Guide" : "Alternate Guide");
            if (deg != 0) client.Objects.SetRotation(sim, box.LocalID, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, deg * MathF.PI / 180));
            if (f[0] == "A") client.Objects.SetFlags(sim, box.LocalID, false, false, true, false);
            client.Objects.DeselectObject(sim, box.LocalID);
            n++;
        }
        return $"rails {n}";
    }

    /// <summary>A switch stand with no script: a phantom "Switch Stand" whose description is as given.</summary>
    static async Task<string> Stand(string rest)
    {
        var w = rest.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        if (w.Length < 4 || !float.TryParse(w[0], out var x) || !float.TryParse(w[1], out var y) || !float.TryParse(w[2], out var z))
            return "ERR stand X Y Z DESC";
        var sim = client.Network.CurrentSim;
        var box = await RezBox(new Vector3(x, y, z + 0.6f), new Vector3(0.3f, 0.3f, 1.2f));
        if (box == null) return "ERR no object came back";
        Named(box.LocalID, "Switch Stand");
        client.Objects.SetDescription(sim, box.LocalID, w[3]);
        client.Objects.SetFlags(sim, box.LocalID, false, false, true, false);
        client.Objects.DeselectObject(sim, box.LocalID);
        return $"stand {box.ID} local {box.LocalID}";
    }

    /// <summary>A self-naming seat, as a blind tester met one (2026-10-31: a sofa that renamed itself as people sat,
    /// and Access's objects list kept the old name): a box with a sit target whose script renames it NAME + " for two"
    /// while someone sits on it or after a touch, and back to NAME when they stand or on the next touch, saying "NAME
    /// renamed to N" each time.</summary>
    static async Task<string> Sofa(string rest)
    {
        if (NameAndPos(rest, "Access Sofa") is not var (name, pos)) return "ERR sofa [NAME] X Y Z";
        var sim = client.Network.CurrentSim;
        var box = await RezBox(pos, new Vector3(1.6f, 0.7f, 0.5f));
        if (box == null) return "ERR no object came back";
        Named(box.LocalID, name);
        var script = await UploadScript("Access sofa",
            "string baseName; integer taken; "
            + "rename(integer v) { taken = v; if (taken) llSetObjectName(baseName + \" for two\"); else llSetObjectName(baseName); "
            + "llSay(0, baseName + \" renamed to \" + llGetObjectName()); } "
            + "default { state_entry() { baseName = llGetObjectName(); llSitTarget(<0, 0, 0.6>, ZERO_ROTATION); llSay(0, baseName + \" ready\"); } "
            + "touch_start(integer n) { rename(!taken); } "
            + "changed(integer c) { if (c & CHANGED_LINK) rename(llAvatarOnSitTarget() != NULL_KEY); } }");
        int mark = counter;
        client.Inventory.CopyScriptToTask(box.LocalID, script, true);
        client.Objects.DeselectObject(sim, box.LocalID);
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(250);
            lock (heard)
                if (heard.Any(l => l.Contains(name + " ready") && int.Parse(Regex.Match(l, "\"n\":(\\d+)").Groups[1].Value) > mark))
                    return $"sofa {name} local {box.LocalID} id {box.ID}";
        }
        return $"ERR sofa {name} (local {box.LocalID}) rezzed but its script didn't say it was ready";
    }

    static async Task<string> Facebox(string rest)
    {
        if (NameAndPos(rest, "Access Facebox") is not var (name, pos)) return "ERR facebox [NAME] X Y Z";
        var sim = client.Network.CurrentSim;
        var box = await RezBox(pos, new Vector3(0.5f));
        if (box == null) return "ERR no object came back";
        Named(box.LocalID, name);
        var script = await UploadScript("Access facebox",
            "default { state_entry() { llSay(0, llGetObjectName() + \" ready\"); } "
            + "touch_start(integer n) { llSay(0, llGetObjectName() + \" touched face \" + (string)llDetectedTouchFace(0) + \" st \" + (string)llDetectedTouchST(0)); } }");
        int mark = counter;
        client.Inventory.CopyScriptToTask(box.LocalID, script, true);
        client.Objects.DeselectObject(sim, box.LocalID);
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(250);
            lock (heard)
                if (heard.Any(l => l.Contains(name + " ready") && int.Parse(Regex.Match(l, "\"n\":(\\d+)").Groups[1].Value) > mark))
                    return $"facebox {name} local {box.LocalID} id {box.ID}";
        }
        return $"ERR facebox {name} (local {box.LocalID}) rezzed but its script didn't say it was ready";
    }

    // The notecard "Access Card", made once per run: an empty notecard item, then its text through the
    // UpdateNotecardAgentInventory capability (LibreMetaverse has no call for it; it's the script upload's protocol)
    static async Task<InventoryItem> Card()
    {
        if (card != null) return card;
        var folder = client.Inventory.FindFolderForType(FolderType.Notecard);
        var item = await client.Inventory.CreateItemAsync(folder, CardName, "Sojourn Access test notecard", AssetType.Notecard, UUID.Zero,
            InventoryType.Notecard, PermissionMask.All);
        if (item == null) throw new Exception("couldn't create the notecard");
        var body = Encoding.UTF8.GetBytes(CardText);
        var asset = Encoding.UTF8.GetBytes("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length " + body.Length + "\n")
            .Concat(body).Concat(Encoding.UTF8.GetBytes("}\n")).ToArray();
        await UploadCard(item, asset);
        card = item;
        return item;
    }

    // A notecard holding two items as the official viewer writes them (LLInventoryItem::exportLegacyStream, by hand:
    // LibreMetaverse's Encode isn't the same text), each in the text at its private-use place character
    static async Task<string> EmbedCard(string rest)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var name = string.Join(' ', parts[..^1]);
        var to = UUID.Parse(parts[^1]);
        var landmark = await client.Inventory.CreateItemAsync(client.Inventory.FindFolderForType(FolderType.Landmark), name + " Landmark", "a place", AssetType.Landmark, UUID.Zero,
            InventoryType.Landmark, PermissionMask.All) ?? throw new Exception("couldn't create the landmark");
        var inner = await client.Inventory.CreateItemAsync(client.Inventory.FindFolderForType(FolderType.Notecard), name + " Inner", "", AssetType.Notecard, UUID.Zero,
            InventoryType.Notecard, PermissionMask.All) ?? throw new Exception("couldn't create the inner notecard");
        await UploadCard(inner, Encoding.UTF8.GetBytes("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length 12\ninner words\n}\n"));
        static string Block(int index, InventoryItem it, string type) =>
            "{\next char index " + index + "\n\tinv_item\t0\n\t{\n\t\titem_id\t" + it.UUID + "\n\t\tparent_id\t" + UUID.Zero + "\n\tpermissions 0\n\t{\n"
            + "\t\tbase_mask\t7fffffff\n\t\towner_mask\t7fffffff\n\t\tgroup_mask\t00000000\n\t\teveryone_mask\t00000000\n\t\tnext_owner_mask\t7fffffff\n"
            + "\t\tcreator_id\t" + client.Self.AgentID + "\n\t\towner_id\t" + client.Self.AgentID + "\n\t\tlast_owner_id\t" + client.Self.AgentID + "\n\t\tgroup_id\t" + UUID.Zero + "\n\t}\n"
            + "\t\tasset_id\t" + it.AssetUUID + "\n\t\ttype\t" + type + "\n\t\tinv_type\t" + type + "\n\t\tflags\t00000000\n"
            + "\tsale_info\t0\n\t{\n\t\tsale_type\tnot\n\t\tsale_price\t10\n\t}\n\t\tname\t" + it.Name + "|\n\t\tdesc\t" + it.Description + "|\n\t\tcreation_date\t1700000000\n\t}\n}\n";
        var inv = inner;
        var text = "Welcome to the harbour.\nGo there: " + char.ConvertFromUtf32(0x100000) + " today.\nAnd read this: " + char.ConvertFromUtf32(0x100001) + "\n";
        var asset = "Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 2\n" + Block(0, landmark, "landmark") + Block(1, inv, "notecard") + "}\nText length "
                    + Encoding.UTF8.GetByteCount(text) + "\n" + text + "}\n";
        var outer = await client.Inventory.CreateItemAsync(client.Inventory.FindFolderForType(FolderType.Notecard), name, "embeds two", AssetType.Notecard, UUID.Zero,
            InventoryType.Notecard, PermissionMask.All) ?? throw new Exception("couldn't create the notecard");
        await UploadCard(outer, Encoding.UTF8.GetBytes(asset));
        client.Inventory.GiveItem(outer.UUID, outer.Name, AssetType.Notecard, to, false);
        return "offered " + name;
    }

    static async Task UploadCard(InventoryItem item, byte[] asset)
    {
        var cap = client.Network.CurrentSim.Caps.CapabilityURI("UpdateNotecardAgentInventory");
        if (cap != null)
        {
            using var http = new HttpClient();
            var req = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><llsd><map><key>item_id</key><uuid>{item.UUID}</uuid></map></llsd>";
            var first = await (await http.PostAsync(cap, new StringContent(req, Encoding.UTF8, "application/llsd+xml"))).Content.ReadAsStringAsync();
            var uploader = Regex.Match(first, "<key>uploader</key>\\s*<(?:uri|string)>([^<]+)<").Groups[1].Value;
            if (uploader.Length > 0)
            {
                var done = await (await http.PostAsync(WebUtility.HtmlDecode(uploader), new ByteArrayContent(asset))).Content.ReadAsStringAsync();
                if (!done.Contains("complete")) Console.WriteLine("notecard text upload: " + done);
                if (Regex.Match(done, "<key>new_asset</key>\\s*<uuid>([^<]+)<") is { Success: true } made) item.AssetUUID = new UUID(made.Groups[1].Value);
            }
            else Console.WriteLine("notecard text upload refused: " + first);
        }
    }

    static string Cleanup()
    {
        var sim = client.Network.CurrentSim;
        var trash = client.Inventory.FindFolderForType(FolderType.Trash);
        List<uint> ids;
        lock (rezzed) { ids = rezzed.ToList(); rezzed.Clear(); }
        foreach (var id in ids)
            client.Inventory.RequestDeRezToInventory(id, DeRezDestination.AgentInventoryTake, trash, UUID.Random());
        return $"took {ids.Count}";
    }

    static void Named(uint localId, string name)
    {
        client.Objects.SetName(client.Network.CurrentSim, localId, name);
        lock (rezzed) named[localId] = name;
    }

    // By the name we gave it, not the world's copy: object updates carry no owner and names only come with properties
    // asked for, so matching those found nothing and things meant to be gone stayed out for later steps (a sofa left
    // 10 m behind the sound-as-a-map marks, its seat cue rightly from behind).
    static string Derez(string name)
    {
        var trash = client.Inventory.FindFolderForType(FolderType.Trash);
        List<uint> ids;
        lock (rezzed)
        {
            ids = named.Where(kv => kv.Value == name).Select(kv => kv.Key).ToList();
            foreach (var id in ids) { named.Remove(id); rezzed.Remove(id); }
        }
        foreach (var id in ids)
            client.Inventory.RequestDeRezToInventory(id, DeRezDestination.AgentInventoryTake, trash, UUID.Random());
        return $"derezzed {ids.Count}";
    }
}
