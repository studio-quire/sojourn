using LibreMetaverse;

namespace Sojourn.Core.Agents;

/// <summary>Batch B2's parts of the session (people and me): friends and contact sets, presence (away, Do Not
/// Disturb, typing, hover height), places (teleport history, offered places), experiences; and since 2026-10-28 the
/// reject switches, a group's chat (snooze, off, moderation), money notices, the display name and notes. Claude Opus 5.5.</summary>
public sealed partial class AgentSession
{
    PeopleStore? _peopleStore;
    PeopleOps? _people;
    Presence? _presence;
    Places? _places;
    ExperienceOps? _experiences;
    Rejects? _rejects;
    GroupChat? _groupChat;
    Money? _money;
    NamesAndNotes? _namesAndNotes;

    public PeopleStore PeopleStore => _peopleStore ??= new PeopleStore(this);
    public PeopleOps People => _people ??= new PeopleOps(this);
    public Presence Presence => _presence ??= new Presence(this);
    public Places Places => _places ??= new Places(this);
    public ExperienceOps Experiences => _experiences ??= new ExperienceOps(this);
    public Rejects Rejects => _rejects ??= new Rejects(this);
    public GroupChat GroupChat => _groupChat ??= new GroupChat(this);
    public Money Money => _money ??= new Money(this);
    public NamesAndNotes NamesAndNotes => _namesAndNotes ??= new NamesAndNotes(this);

    /// <summary>OnIM's question, after blocks, antispam and Do Not Disturb: true when the person's switches took the IM
    /// (dropped, answered or accepted; Rejects.cs, GroupChat.cs) and it goes no further.</summary>
    bool PeopleTake(GridClient c, InstantMessage im) => Rejects.Takes(c, im) || GroupChat.Takes(c, im);

    /// <summary>Whether the person won't see an IM because of their switches (no side effects): for other listeners,
    /// such as the new-IM sound and the feed's typing dots.</summary>
    public bool PeopleHide(GridClient c, InstantMessage im) => Rejects.Hides(c, im) || GroupChat.Hides(im);

    void WirePeople(GridClient c)
    {
        Presence.Wire(c);
        Places.Wire(c);
        Money.Wire(c);
        NamesAndNotes.Wire(c);
    }

    void PeopleLoggedIn(GridClient c)
    {
        Presence.OnLoggedIn(c);
        Places.OnLoggedIn(c);
        Rejects.OnLoggedIn();
        GroupChat.OnLoggedIn();
        Money.OnLoggedIn();
        NamesAndNotes.OnLoggedIn(c);
    }
}
