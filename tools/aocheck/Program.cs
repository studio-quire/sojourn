// The AO's logic, checked without a grid (tools/aocheck/aocheck.csproj says how to run it). Claude Opus 5.5, 2026-10-29.
using LibreMetaverse;
using Sojourn.Core.Agents;
using M = Sojourn.Core.Agents.AoMotions;

int fails = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "ok   " : "FAIL ") + what); if (!ok) fails++; }

UUID U() => UUID.Random();
AoFolders.Item Anim(string name, int? order = null) => new(U(), name, order?.ToString() ?? "", U(), true);

// ---- the folder format ----
var ao = new AoFolders.Folder(U(), "#AO");
var main = new AoFolders.Folder(U(), "Main:SO:SM:**");
var standing = new AoFolders.Folder(U(), "Standing:CT5.00:CY");
var s2 = Anim("stand B", 2); var s0 = Anim("stand A", 0); var s1 = Anim("stand C", 1);
standing.Items.AddRange(new[] { s2, s0, s1 });
var pair = new AoFolders.Folder(U(), "Pair:OR5");
var p1 = Anim("pair 1", 0); var p2 = Anim("pair 2", 1);
pair.Items.AddRange(new[] { p2, p1 });
standing.Folders.Add(pair);
var breath = new AoFolders.Folder(U(), "Breath:TR:CY:CT2");
var b1 = Anim("breath 1", 0); var b2 = Anim("breath 2", 1);
breath.Items.AddRange(new[] { b1, b2 });
standing.Folders.Add(breath);
var walking = new AoFolders.Folder(U(), "Walk.N");
var w1 = Anim("walk"); walking.Items.Add(w1);
var sitting = new AoFolders.Folder(U(), "Sitting");
var si = Anim("sit"); sitting.Items.Add(si);
var sitg = new AoFolders.Folder(U(), "Sitting On Ground");
var sg = Anim("groundsit"); sitg.Items.Add(sg);
var typing = new AoFolders.Folder(U(), "Typing");
var ty = Anim("type"); typing.Items.Add(ty);
var always = new AoFolders.Folder(U(), "Always");
var al = Anim("tail"); always.Items.Add(al);
var swim = new AoFolders.Folder(U(), "Swimming Forward");
var sw = Anim("swim"); swim.Items.Add(sw);
var fly = new AoFolders.Folder(U(), "Flying");
var fl = Anim("fly"); fly.Items.Add(fl);
var land = new AoFolders.Folder(U(), "Landing");
var la = Anim("land"); land.Items.Add(la);
var bogus = new AoFolders.Folder(U(), "Dancing");
main.Folders.AddRange(new[] { standing, walking, sitting, sitg, typing, always, swim, fly, land, bogus });
var other = new AoFolders.Folder(U(), "Other");
var ostand = new AoFolders.Folder(U(), "Standing"); var os = Anim("other stand"); ostand.Items.Add(os); other.Folders.Add(ostand);
ao.Folders.AddRange(new[] { other, main });
ao.Items.Add(new AoFolders.Item(U(), "note", "", UUID.Zero, false, IsNotecard: true));

Check(AoFolders.KindOf(ao) == AoFolders.Kind.Sets, "an #AO folder reads as sets");
Check(AoFolders.KindOf(main) == AoFolders.Kind.Set, "a set folder reads as one set");
var warn = new List<string>();
var sets = AoFolders.ReadSets(ao, warn);
Check(sets.Count == 2, "two sets");
var twinAo = new AoFolders.Folder(U(), "#AO");
twinAo.Folders.Add(new AoFolders.Folder(U(), "Main"));   // an empty twin first, as an interrupted newSet leaves
twinAo.Folders.Add(main);
var twins = AoFolders.ReadSets(twinAo, new List<string>());
Check(twins.Count == 1 && twins[0].State(AoStateKind.Standing).Steps.Count == 4 && twins[0].IsDefault, "an empty twin folder doesn't hide the real set");
var set = sets.Single(s => s.Name == "Main");
Check(set.SitOverride && set.Smart && set.IsDefault && !set.MouselookStandDisable, "Main's flags :SO:SM:**");
var st = set.State(AoStateKind.Standing);
Check(st.Cycle && !st.Random && Math.Abs(st.CycleTime - 5) < 0.001, "Standing:CT5.00:CY");
Check(string.Join(",", st.Steps.Select(x => x.Name)) == "stand A,stand C,stand B,Pair", "steps in their order, the group by OR5 last: " + string.Join(",", st.Steps.Select(x => x.Name)));
Check(st.Steps[3].IsGroup && st.Steps[3].Members.Select(m => m.Name).SequenceEqual(new[] { "pair 1", "pair 2" }), "the group's members in order");
Check(st.Tracks.Count == 1 && st.Tracks[0].Cycle && st.Tracks[0].CycleTime == 2, "a track :TR:CY:CT2");
Check(set.State(AoStateKind.Walking).Steps.Count == 1, "Walk.N is Walking (ZHAO's name)");
Check(warn.Any(w => w.Contains("Dancing")), "an unknown state is warned about");
Check(AoFolders.SetFolderName(set) == "Main:SO:SM:**", "set name written back: " + AoFolders.SetFolderName(set));
Check(AoFolders.StateFolderName(st) == "Standing:CT5.00:CY", "state name written back: " + AoFolders.StateFolderName(st));
Check(AoFolders.CleanName(" a:b|cé ") == "abc", "names cleaned as Firestorm's");

// ---- ZHAO-II ----
var zitems = new[] { Anim("Z stand 1"), Anim("Z stand 2"), Anim("Z walk"), Anim("Z sit") };
var zset = AoFolders.FromZhao("My AO", U(), "# comment\n[ Standing ]Z stand 1|Z stand 2,Z walk\n[ Walking ]Z walk\n[ Sitting ]Z sit|missing\n[ Bogus ]x\nnot a line\n", zitems, warn = new());
Check(zset != null && zset.FromNotecard, "a ZHAO-II notecard reads as a set");
Check(zset!.State(AoStateKind.Standing).Steps.Count == 2 && zset.State(AoStateKind.Standing).Steps[1].IsGroup, "ZHAO steps and a ',' group");
Check(warn.Count == 3, "ZHAO warnings: missing, unknown state, no [ ]: " + string.Join(" | ", warn));

// ---- the engine ----
var sent = new List<(UUID Id, bool Start)>();
bool onObject = false, seatAnimates = false;
var e = new AoEngine { Send = (id, s) => sent.Add((id, s)), SittingOnObject = () => onObject, SeatAnimates = () => seatAnimates, Rng = new Random(1) };
bool Started(UUID id) => sent.Any(x => x.Id == id && x.Start);
bool Stopped(UUID id) => sent.Any(x => x.Id == id && !x.Start);
string Names(IEnumerable<UUID> ids) => string.Join(",", ids.Select(i => i == s0.AssetId ? "A" : i == s1.AssetId ? "C" : i == s2.AssetId ? "B" : i == p1.AssetId ? "p1" : i == p2.AssetId ? "p2" : i.ToString()[..4]));

e.Load(sets, null);
Check(e.Current?.Name == "Main", "the default set (:**) is chosen");
// disabled: the region's stand passes, but is remembered
Check(e.Override(M.Stand, true) == UUID.Zero && sent.Count == 0, "off: nothing overridden");
sent.Clear();
e.Enable(true);
// cycling state: each start goes to the next step (Firestorm's getAnimationsForState); first start: step 1 (C)
Check(Started(al.AssetId), "on: Always's animation starts");
Check(Stopped(M.Stand), "on while standing: the Linden stand stopped at the region");
Check(Started(s1.AssetId), "on while standing: the cycling Standing goes to its next step (C)");
Check(Stopped(M.Stand1) && Stopped(M.Stand4), "the stand variants stopped");
Check(Started(b1.AssetId) || Started(b2.AssetId), "the Standing track plays");
// the region echoes our stop of STAND: ignored once
sent.Clear();
Check(e.Override(M.Stand, false) == UUID.Zero && !Stopped(s1.AssetId), "the echo of stopping STAND is ignored once");

// walking: Standing's animations are cleaned up, Walking's played
sent.Clear();
var r = e.Override(M.Walk, true);
Check(r == w1.AssetId, "walk: the caller starts the walk");
Check(Stopped(s1.AssetId), "walk: the stand stopped as a stray of another state");
Check(Stopped(b1.AssetId) || Stopped(b2.AssetId), "walk: the stand's track stopped");
sent.Clear();
r = e.Override(M.Walk, false);
Check(r == w1.AssetId, "walk ends: the caller stops the walk");
// standing again: next step B
sent.Clear();
r = e.Override(M.Stand, true);
Check(r == s2.AssetId, "standing again: the next step (B) " + Names(new[] { r }));
// cycle timer: after 5 s, the next step (the group: two animations)
e.Tick(0); sent.Clear();
e.Tick(5.1);
Check(Started(p1.AssetId) && Started(p2.AssetId) && Stopped(s2.AssetId), "after its cycle time the stand cycles to the group");
// next / previous by hand
sent.Clear();
e.Cycle(AoEngine.CycleMode.Next, true);
Check(Started(s0.AssetId) && Stopped(p1.AssetId) && Stopped(p2.AssetId), "next: from the group round to A");
sent.Clear();
e.Cycle(AoEngine.CycleMode.Previous, true);
Check(Started(p1.AssetId) && Stopped(s0.AssetId), "previous: back to the group");
// typing beside the stand
sent.Clear();
r = e.Override(M.Type, true);
Check(r == ty.AssetId && !Stopped(p1.AssetId), "typing plays beside the stand");
sent.Clear();
r = e.Override(M.Type, false);
Check(r == ty.AssetId && !Stopped(p1.AssetId), "typing stops, the stand stays");

// sitting on an object, sit override on (the set's :SO): ours, with the generic sit under it
onObject = true; sent.Clear();
e.Override(M.Stand, false);
r = e.Override(M.Sit, true);
Check(r == si.AssetId && Started(M.SitGeneric), "sit override: our sit, the generic sit under it");
// smart sit: the seat plays its own animation -> ours gives way within a second
seatAnimates = true; sent.Clear();
double t = 6;
for (int i = 0; i < 12; i++) e.Tick(t += 0.1);
Check(Stopped(si.AssetId) && Stopped(M.SitGeneric), "smart sit: a seat with its own animation wins");
seatAnimates = false;
sent.Clear();
e.Override(M.Sit, false);
// a scripted seat that sits you with the ground sit: the seat's
sent.Clear();
r = e.Override(M.SitGroundConstrained, true);
Check(r == UUID.Zero && !Started(sg.AssetId), "a ground sit while on a prim stays the seat's");
e.Override(M.SitGroundConstrained, false);
// sit override off: the seat's sit plays
e.SetSitOverride(set, false); sent.Clear();
r = e.Override(M.Sit, true);
Check(r == UUID.Zero && !Started(si.AssetId), "without sit override, sits are the seat's");
e.Override(M.Sit, false);
onObject = false;
// on the ground: the ground sit is transient, played beside the Linden one
sent.Clear();
r = e.Override(M.SitGroundConstrained, true);
Check(r == UUID.Zero && Started(sg.AssetId), "ground sit: ours beside the Linden one");
sent.Clear();
r = e.Override(M.SitGroundConstrained, false);
Check(Stopped(sg.AssetId), "ground sit ends: ours stopped");
// stands off while standing: ours stops, the region's stand comes back
sent.Clear();
e.Override(M.Stand, true);
var ours = e.Current!.State(AoStateKind.Standing).Playing.ToList();
sent.Clear();
e.EnableStands(false);
Check(ours.Count > 0 && ours.All(Stopped) && Started(M.Stand), "stands off while standing: ours stopped, the Linden stand back");
e.EnableStands(true);
Check(e.Playing().Count > 0, "stands on again while standing: ours plays");
e.Override(M.Stand, false);
// stands off: standing passes through
e.EnableStands(false); sent.Clear();
r = e.Override(M.Stand, true);
Check(r == UUID.Zero && !Started(s0.AssetId) && !Started(s1.AssetId) && !Started(s2.AssetId), "stands off: the region's stand");
e.EnableStands(true);
e.Override(M.Stand, false);
// flying under water: swimming
sent.Clear();
r = e.Override(M.Fly, true);
Check(r == fl.AssetId, "flying");
sent.Clear();
e.BelowWater(true);
Check(Stopped(fl.AssetId) && Started(sw.AssetId), "under water, flying turns to swimming");
sent.Clear();
e.Override(M.Fly, false);
e.BelowWater(false);
// landing: transient
sent.Clear();
r = e.Override(M.Land, true);
Check(r == UUID.Zero && Started(la.AssetId), "landing plays beside the Linden landing");
sent.Clear();
e.Override(M.Land, false);
Check(Stopped(la.AssetId), "landing stopped with it");
// Always reasserted when missing
sent.Clear();
e.ReassertAlways(id => id != al.AssetId);
Check(Started(al.AssetId), "Always started again when the region lost it");
// region change: asks for the last motion again
e.Override(M.Stand, true);
Check(e.OnRegionChange() == M.Stand, "a region change asks for the motion again");
// off: everything of ours stopped, the Linden motion back
sent.Clear();
e.Enable(false);
Check(Stopped(al.AssetId) && Started(M.Stand) && e.Playing().Count == 0, "off: ours stopped, the Linden stand started again");
// select another set while on
e.Enable(true); sent.Clear();
e.Select(sets.Single(s => s.Name == "Other"));
Check(Started(os.AssetId) && Stopped(al.AssetId), "selecting a set: its stand plays, the old Always stops");

Console.WriteLine(fails == 0 ? "all passed" : $"{fails} failed");
return fails;
