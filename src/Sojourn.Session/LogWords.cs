using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core.Access;
using Sojourn.Core.Agents;
using Sojourn.Session.Social;

namespace Sojourn.Session;

/// <summary>
/// The session log's records of words and names (SessionLog.Record), each saying whose words it carries, so a log sent
/// to the studio can leave other people's out exactly and put stand-ins for their names (LogShare.cs):
/// <c>~words</c> everything that joins the feed (nearby chat, IMs, group chat, notices), <c>~heard</c> each line the
/// screen reader was given (Announcer.Heard), <c>~name</c> each name learned (legacy, display, pseudonym; the
/// person's own marked <c>me</c>). Locally they're the person's own log, as chat in the console always was.
/// Written by Claude Opus 5.5, 2026-10-31.
/// </summary>
public static class LogWords
{
    /// <summary>Whose words a feed entry carries: the person's own, an object's, a notice's by its kind, else a person's.</summary>
    public static LineFrom FromOf(FeedEntry e) => e.Mine ? LineFrom.Me
        : e.Style == FeedStyle.Object ? LineFrom.Objects
        : e.Kind == FeedKind.Notice ? e.NoticeName is { } name ? LineSources.OfNotice(name) : e.From == UUID.Zero ? LineFrom.App : LineFrom.People
        : LineFrom.People;

    public static string Word(LineFrom f) => f switch
    {
        LineFrom.Me => "me", LineFrom.People => "people", LineFrom.Objects => "objects", LineFrom.Assistant => "assistant", _ => "app",
    };

    /// <summary>Records from here on: the feed's entries, the names the session knows now and learns, pseudonyms.</summary>
    public static void Start(AgentSession s, SocialFeed feed)
    {
        feed.Joined += e => SessionLog.Record("words", Words(e));
        var names = s.Names;
        bool told = false;
        void Name(UUID id, string legacy, string? display)
        {
            bool me = id == s.AgentId && id != UUID.Zero;
            var o = new JsonObject { ["id"] = id.ToString(), ["legacy"] = legacy };
            if (!string.IsNullOrEmpty(display)) o["display"] = display;
            if (me) o["me"] = true;
            SessionLog.Record("name", o);
            // the account's pseudonyms once it's known whose they are (its store is the account's)
            if (me && !told)
            {
                told = true;
                try { foreach (var (pid, p) in s.People.AllPseudonyms()) Pseudonym(pid, p); } catch (Exception) { }
            }
        }
        void Pseudonym(UUID id, string name) => SessionLog.Record("name", new JsonObject { ["id"] = id.ToString(), ["pseudonym"] = name });
        names.Learned += Name;
        s.People.PseudonymSet += Pseudonym;
        foreach (var (id, legacy, display) in names.All()) Name(id, legacy, display);
    }

    static JsonObject Words(FeedEntry e)
    {
        var o = new JsonObject
        {
            ["kind"] = e.Kind switch { FeedKind.Nearby => "nearby", FeedKind.Im => "im", FeedKind.Group => "group", _ => "notice" },
            ["from"] = Word(FromOf(e)),
        };
        if (e.From != UUID.Zero) o["id"] = e.From.ToString();
        if (e.Name.Length > 0) o["name"] = e.Name;
        if (e.SessionName is { Length: > 0 } sn) o["session"] = sn;
        if (e.Kind != FeedKind.Notice) o["style"] = e.Style.ToString().ToLowerInvariant();
        if (e.NoticeName is { } nn) o["notice"] = nn;
        if (e.Alert) o["alert"] = true;
        o["text"] = e.Text;
        return o;
    }

    /// <summary>A line the screen reader was given (the announcer's Heard).</summary>
    public static void Heard(string text, LineFrom from, string how, string via) =>
        SessionLog.Record("heard", new JsonObject { ["from"] = Word(from), ["how"] = how, ["via"] = via, ["text"] = text });
}
