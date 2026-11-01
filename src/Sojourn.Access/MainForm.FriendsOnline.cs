using System.Runtime.CompilerServices;
using Sojourn.Core.Access;
using Sojourn.Session.Social;

namespace Sojourn.Access;

/// <summary>
/// Friends online at login, as one line: the grid tells of every friend online as the session starts, and read one by
/// one they ran to 23 lines in the same second for the blind tester (2026-10-31). Those arriving while logging in, and
/// until 30 s after arriving (or after logging in again), are gathered, with any that follow within 3 s of the last,
/// and said together once they stop ("23 friends online: Ann, Bob, Cat and 20 more; the chat history names them
/// all."), the history line naming every one, with one friends sound; one alone is said as before. After that each
/// arrival is its own line, as always. Firestorm reports the count of friends online at login in the same spirit.
/// </summary>
public sealed partial class MainForm
{
    readonly object _friendsLock = new();
    readonly ConditionalWeakTable<FeedEntry, object> _friendsDecided = new();
    readonly List<string> _friendsBurst = new();
    long _friendsUntil = long.MaxValue;   // open from the start: logging in
    long _friendsLast;
    bool _arrived, _wasOnline;

    void FriendsWindowOpens()
    {
        lock (_friendsLock) _friendsUntil = Environment.TickCount64 + 30_000;
    }

    static readonly object Gathered = new(), Single = new();

    /// <summary>Whether a line is a friend coming online that's gathered into the login's one line (decided once per
    /// entry, whichever of the reader, the history or the earcons asks first).</summary>
    bool InFriendsBurst(FeedEntry e)
    {
        if (e.Kind != FeedKind.Notice || e.NoticeName != "FriendOnlineOffline" || !e.Text.Contains("online")) return false;
        lock (_friendsLock)
        {
            if (_friendsDecided.TryGetValue(e, out var d)) return d == Gathered;
            long now = Environment.TickCount64;
            bool gather = now < _friendsUntil || (_friendsBurst.Count > 0 && now - _friendsLast < 3000);
            if (gather)
            {
                var name = e.Name.Length > 0 ? e.Name : "A friend";
                if (!_friendsBurst.Contains(name)) _friendsBurst.Add(name);
                _friendsLast = now;
            }
            _friendsDecided.Add(e, gather ? Gathered : Single);
            return gather;
        }
    }

    /// <summary>The slow tick: once the gathered ones have stopped coming (and the person has arrived), they're said.</summary>
    void FriendsBurstSaid()
    {
        if (Host is not { } h) return;
        bool online = h.Session.Online;
        if (online && !_wasOnline && _arrived)
        {
            // logged in again (after a stall): the grid tells of every friend again
            FriendsWindowOpens();
            OutfitMoving(login: true);
        }
        _wasOnline = online;
        List<string> names;
        lock (_friendsLock)
        {
            if (_friendsBurst.Count == 0 || !_arrived || Environment.TickCount64 - _friendsLast < 2000) return;
            names = _friendsBurst.ToList();
            _friendsBurst.Clear();
        }
        var (said, whole) = FriendsLines(names);
        AppendChat(whole);
        _earcons?.FriendsOnline();
        if (_set.MuteAll || _set.IsMuted("notices")) return;
        Reader?.Say(AnnounceKind.Notices, said, from: LineFrom.App);
    }

    /// <summary>What's said and what the history keeps: one friend as any arrival is; a few named; many, the first
    /// three and how many more, the history naming every one.</summary>
    static (string Said, string Whole) FriendsLines(List<string> names)
    {
        if (names.Count == 1) return ($"{names[0]} is online.", $"{names[0]} is online");
        string All(IEnumerable<string> n) { var l = n.ToList(); return string.Join(", ", l.Take(l.Count - 1)) + " and " + l[^1]; }
        var whole = $"{names.Count} friends online: {All(names)}.";
        if (names.Count <= 3) return (whole, whole);
        return ($"{names.Count} friends online: {string.Join(", ", names.Take(3))} and {names.Count - 3} more; the chat history names them all.", whole);
    }
}
