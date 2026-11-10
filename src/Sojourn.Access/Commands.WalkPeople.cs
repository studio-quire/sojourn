using LibreMetaverse;

namespace Sojourn.Access;

/// <summary>
/// A walk and the people standing where it would go (Claude Fable 5.2, 2026-11-09; the user left the choice of remedy
/// to us that day, and this is ours: docs/access-app.md at //go). A sighted resident walks round people without
/// thinking, and a blind one can't unless the app does it, so a `//go` or a Walk to plans round everyone on their feet
/// (PathPlanner's people), watches the next few metres as it goes for anyone who has stepped in since, and plans
/// afresh when someone has. Where the only way is through someone it stops short of them and says who: never the
/// teleport a walk with no way at all falls back to, since a person in a doorway is gone in a minute. People sitting
/// aren't counted: nobody is walked into there (a seated avatar has no body to push, on OpenSim; Second Life untried).
/// </summary>
public sealed partial class Commands
{
    /// <summary>The people one walk goes round.</summary>
    sealed class Bystanders(UUID to)
    {
        /// <summary>Who the walk is to, if it's to a person: never in their own walk's way.</summary>
        public readonly UUID To = to;
        /// <summary>Those the way was last planned round, in the planner's order, and where each stood then: what the
        /// way already allows for. Anyone else in it, or one of these somewhere else, has stepped in since.</summary>
        public readonly List<UUID> Given = new();
        public readonly Dictionary<UUID, Vector3> Stood = new();
        /// <summary>Someone in the way at the last look, and since when: said only once they've been there a moment
        /// (a person crossing the line is out of it again before she gets there).</summary>
        public (UUID Id, long Since)? Seen;
        /// <summary>Who a stretch ended for (Leg.Met; a straight walk's watch returning early).</summary>
        public UUID? Met;
        /// <summary>She set out from among people with no step to take that kept off them all (a landing point's pile,
        /// a knot of people talking): those who stood against her then. While she's still beside them they're left
        /// out of this walk's ways, so it leaves through them as every walk did before 2026-11-09, and everyone else
        /// is still gone round. Refusing there ("Not walking ...") left her where she was for people she was
        /// touching already.</summary>
        public readonly HashSet<UUID> Beside = new();
    }

    /// <summary>How many times one walk plans afresh round people stepping into its way; after that it keeps to the way
    /// it has (a crowd on the move: every new way would be stepped into as well).</summary>
    const int Dodges = 5;
    /// <summary>How far ahead a walk looks for someone who has stepped in, at least (further at speed: Stepped), and
    /// how near its line counts as in it (the planner's own ways keep 0.75 m or more from someone's middle).</summary>
    const float LookAhead = 4f, InLine = 0.7f;
    /// <summary>Walks go round people (the walk_round_people setting, on unless she turns it off: `//walkround`, World's
    /// menu, Alt+0's Walking). Off, walks take no notice of people, as before 2026-11-09: it's new and untried on
    /// Second Life, and she may simply prefer straight walks.</summary>
    public Func<bool> RoundPeople { get; set; } = () => true;
    /// <summary>SOJOURN_WALK_PEOPLE=0: off whatever the setting, for seeing what a grid does when one avatar walks into
    /// another (Second Life's answer isn't known: docs/access-testing.md).</summary>
    static readonly bool HeedByEnv = Environment.GetEnvironmentVariable("SOJOURN_WALK_PEOPLE") != "0";
    bool HeedPeople => HeedByEnv && RoundPeople();

    /// <summary>A way round people that's less than this much longer than the one it replaces (the straight line as a
    /// walk sets out, what was left of the old way when it changes) is the same walk to her: the plain "Walking ..."
    /// line, no count of stretches and no line for each. One person on open ground is always this (the planner
    /// asked directly: 0.05 to 0.85 m longer, the most for someone a metre dead ahead), and it was said as "around
    /// NAME: 2 stretches." and "2 of 2: ...", for a first stretch of 0.4 m when they stood beside her. People with
    /// walls about them, or several across the way, still make a way round that's said as one.</summary>
    const float Aside = 1f;
    /// <summary>A way planned afresh mid-walk that's this much longer than what was left says how far it is now: she
    /// was told the walk's length as it set out, and it isn't that any more.</summary>
    const float Further = 3f;

    /// <summary>Whether a way <paramref name="more"/> metres longer than the one before it is a step aside for people
    /// and nothing else (things in the way too, a goal out of reach or a way cut short of someone have their own words).</summary>
    static bool SameWalk(Route r, float more) => more < Aside && r is { Around.Count: > 0, Moved: false, InWay: null };

    /// <summary>Everyone on their feet in this region but her (and <paramref name="except"/>), as the region last had them.</summary>
    List<(UUID Id, Vector3 Pos, Vector3 Vel)> OnFoot(UUID except)
    {
        var list = new List<(UUID, Vector3, Vector3)>();
        if (_s.Region is not { } w || _ops.Client is not { } c) return list;
        foreach (var (_, av) in w.Avatars)
            if (av.ID != c.Self.AgentID && av.ID != except && av.ParentID == 0) list.Add((av.ID, av.Position, av.Velocity));
        return list;
    }

    /// <summary>The people a way from <paramref name="from"/> is planned round: everyone standing still, and anyone
    /// walking who's near enough to be about there still as she passes. Notes them in <paramref name="people"/>.</summary>
    List<System.Numerics.Vector3>? ToGoRound(Bystanders people, Vector3 from)
    {
        people.Given.Clear();
        people.Stood.Clear();
        people.Seen = null;
        if (!HeedPeople) return null;
        List<System.Numerics.Vector3>? at = null;
        foreach (var p in OnFoot(people.To))
        {
            if (new Vector2(p.Vel.X, p.Vel.Y).Length() > 0.3f && Flat(p.Pos, from) > 6) continue;
            people.Stood[p.Id] = p.Pos;
            // (those she set out from among, while she's beside them: allowed for, so not said as stepping in, but not gone round)
            if (people.Beside.Contains(p.Id) && Flat(p.Pos, from) < AtHerSide) continue;
            people.Given.Add(p.Id);
            (at ??= new()).Add(new(p.Pos.X, p.Pos.Y, p.Pos.Z));
        }
        return at;
    }

    /// <summary>Someone who has stepped into the next few metres of the line from <paramref name="at"/> to
    /// <paramref name="to"/> since the way was planned, and stayed a moment; null otherwise. Beside her or behind
    /// (someone walking along with her) isn't in it, nor is someone ahead walking the same way as fast as she is (the
    /// person she's going somewhere with: she isn't gaining on them, and each step of theirs read as stepping in
    /// again). It looks two seconds of her own going ahead, LookAhead at least: seeing someone takes a moment here
    /// and the new way another, and at a run 4 m is gone in under a second.</summary>
    UUID? Stepped(Bystanders people, Vector3 at, Vector3 to)
    {
        var line = new Vector2(to.X - at.X, to.Y - at.Y);
        float len = line.Length();
        if (len < 0.5f) return null;
        var dir = line / len;
        var mine = _ops.Client?.Self.Velocity ?? Vector3.Zero;
        float speed = Vector2.Dot(new Vector2(mine.X, mine.Y), dir);
        float ahead = Math.Min(len, Math.Max(LookAhead, speed * 2));
        UUID? found = null;
        float nearest = float.MaxValue;
        foreach (var p in OnFoot(people.To))
        {
            if (Math.Abs(p.Pos.Z - at.Z) > 2) continue;
            if (people.Stood.TryGetValue(p.Id, out var was) && Flat(was, p.Pos) < 0.4f) continue;
            var rel = new Vector2(p.Pos.X - at.X, p.Pos.Y - at.Y);
            float along = Vector2.Dot(rel, dir);
            if (along < 0.3f || along > ahead + InLine || along >= nearest) continue;
            if ((rel - dir * Math.Min(along, ahead)).Length() >= InLine) continue;
            float theirs = Vector2.Dot(new Vector2(p.Vel.X, p.Vel.Y), dir);
            if (theirs > 1 && theirs > speed - 0.5f) continue;
            (found, nearest) = (p.Id, along);
        }
        long now = Environment.TickCount64;
        if (found is not { } id) { people.Seen = null; return null; }
        if (people.Seen is not { } seen || seen.Id != id) { people.Seen = (id, now); return null; }
        return now - seen.Since >= 250 ? id : null;
    }

    /// <summary>Someone she's up against on the way toward <paramref name="to"/>: on their feet within 1.2 m, ahead of
    /// her (within 60 degrees of the way she's heading: someone at her side isn't what a walk has stuck on).</summary>
    UUID? Against(Bystanders people, Vector3 at, Vector3 to)
    {
        var line = new Vector2(to.X - at.X, to.Y - at.Y);
        if (!HeedPeople || line.Length() < 0.1f) return null;
        var dir = Vector2.Normalize(line);
        UUID? found = null;
        float nearest = 1.2f;
        foreach (var p in OnFoot(people.To))
        {
            var rel = new Vector2(p.Pos.X - at.X, p.Pos.Y - at.Y);
            if (Math.Abs(p.Pos.Z - at.Z) > 2 || rel.Length() >= nearest || Vector2.Dot(rel, dir) <= 0.5f * rel.Length()) continue;
            (found, nearest) = (p.Id, rel.Length());
        }
        return found;
    }

    /// <summary>How near counts as standing against her: the room a way keeps from people where it can.</summary>
    const float AtHerSide = 1.1f;

    /// <summary>Notes those standing against her (on their feet within AtHerSide) as the people she sets out from
    /// among, when there are two or more; false when there aren't. One person can't hem her in on open ground; with
    /// walls they can (a doorway they stand in), and that's the way being through them, which this isn't for.</summary>
    bool SetsOutAmong(Bystanders people, Vector3 at)
    {
        var beside = OnFoot(people.To).Where(p => Flat(p.Pos, at) < AtHerSide && Math.Abs(p.Pos.Z - at.Z) < 2).Select(p => p.Id).ToList();
        if (!HeedPeople || beside.Count < 2) return false;
        people.Beside.UnionWith(beside);
        return true;
    }

    /// <summary>A person's name as names are said here (so it can be typed back), or "someone".</summary>
    async Task<string> NameOfAsync(UUID id) => (await _ops.PeopleAsync(96)).FirstOrDefault(p => p.Id == id)?.Name ?? "someone";

    static string Cap(string s) => s.Length > 0 && char.IsLower(s[0]) ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    /// <summary>"Kestrel", "Kestrel and Mira", "3 people".</summary>
    async Task<string> NamesAsync(IReadOnlyList<UUID> ids) =>
        ids.Count == 1 ? await NameOfAsync(ids[0])
        : ids.Count == 2 ? $"{await NameOfAsync(ids[0])} and {await NameOfAsync(ids[1])}"
        : $"{ids.Count} people";

    /// <summary>Whether someone stands on the very spot a walk is to (within a metre), not merely on the way to it.</summary>
    bool StandsAt(UUID who, Vector3 target) => _s.Region?.AvatarById(who) is { ParentID: 0 } av && Flat(av.Position, target) < 1;

    /// <summary>"Kestrel is in the way, 2 metres, twelve o'clock" (where they are now, from her); "Kestrel is standing
    /// there, ..." when it's the walk's own spot they're on.</summary>
    async Task<string> InWayAsync(UUID who, Vector3 target)
    {
        var line = $"{Cap(await NameOfAsync(who))} is {(StandsAt(who, target) ? "standing there" : "in the way")}";
        return _s.Region?.AvatarById(who) is { ParentID: 0 } av && _ops.Where(av.Position) is { } where ? $"{line}, {where}" : line;
    }

    /// <summary>The way goes only as far as someone standing in it, said as the walk sets out or changes.</summary>
    async Task<string> OnlyAsFarAsync(UUID who, Vector3 target)
    {
        var name = Cap(await NameOfAsync(who));
        return StandsAt(who, target) ? $"{name} is standing there, so only as far as them."
            : $"{name} is in the way and there's no short way round, so only as far as them.";
    }

    /// <summary>She's too near someone standing in the only way to take a step toward them: said, and no walk.</summary>
    async Task<string> NotWalkingAsync(UUID who, Vector3 target, string what) =>
        $"Not walking to {what}: {await InWayAsync(who, target)}{(StandsAt(who, target) ? "" : ", and there's no short way round")}.";

    /// <summary>A walk ended short of someone standing in its way: Radegast's line with who and where they are. True
    /// when that's within 2.5 m of where it was going all the same. <paramref name="letGo"/>: the region's autopilot is
    /// still walking her (not let go by the caller already: letting go twice is two steps on).</summary>
    async Task<bool> EndInWayAsync(UUID who, Vector3 target, bool letGo = true)
    {
        if (letGo) CancelAutoPilot();
        await Task.Delay(700, CancellationToken.None);   // (coming to a stop: the distance said is where she stands)
        if (_ops.Me() is not { } me) return false;
        double off = Flat(me.Pos, target);
        Walk.WalkMode.Log(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"walk over, someone in the way: {off:0.0} m off"));
        _say($"Finished walking, {OffWords(off)} from the destination: {await InWayAsync(who, target)}.");
        return off < 2.5;
    }

    /// <summary>How far from the destination a walk ended, as its last line says it.</summary>
    static string OffWords(double off) => off < 3 ? (off < 1 ? "under a metre" : Bearings.MetresTenths(off)) : Bearings.Metres(off);
}
