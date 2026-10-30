using System.Text.Json.Nodes;
using Sojourn.Session.Assistant;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

namespace Sojourn.Session.Private;

/// <summary>
/// SojournPrivate: incognito on the agent API (docs/assistant.md > Incognito). Everything here answers in general
/// terms by construction, so all of it stays open while incognito is on. Turning it off is the person's own: the
/// wrist token, the settings menu, the desktop's key; no caller of the agent API can turn it off for them. The sidecar
/// proves itself with <see cref="Sidecar.Token"/> only to pass on dictation.
/// </summary>
public sealed class PrivateApi : LeapApi
{
    readonly Situation _situation;

    public override string Name => "SojournPrivate";
    public override string Desc => "Incognito: nothing from the time it's on is kept; unless the person lets the assistant see, only general things about what's going on are told (situation), never what's said, who or where";
    public override bool NeedsSession => false;

    /// <summary>The person's dictated words, from the sidecar (Whisper on this PC): the client shows them as a draft
    /// to send into chat or an IM, with where it goes shown only to the person. Set by the dictation UI.</summary>
    public Action<string>? Dictated { get; set; }

    /// <summary>"Always private with that person" / "here": who (an avatar id, or null), whether "here", on or off;
    /// returns a refusal or null. Set by the triggers.</summary>
    public Func<LibreMetaverse.UUID?, bool, bool, string?>? Remember { get; set; }

    public PrivateApi(AgentSession s, Situation situation) : base(s)
    {
        _situation = situation;
        Op("status", "-> {on, minutes, why, sees}: whether incognito is on, for how long, what turned it on (menu, wrist, key, asked, api, "
            + "auto), and whether the person lets the assistant see specifics during it", _ =>
            new JsonObject { ["on"] = S.Private.On, ["minutes"] = Math.Round(S.Private.Minutes, 1), ["why"] = S.Private.WhyForCallers, ["sees"] = S.Private.Sees });
        Op("situation", "-> {summary, ...counts}: the general picture, built by the client from counts and states (how many people are talking nearby, "
            + "new IMs and from how many people, someone speaking, whose land, how long incognito): never names, words or where. Conversations and the "
            + "pointer come as handles (chat-..., person-..., thing-...) that ops accept while incognito", (_, ct) => _situation.BuildAsync(ct));
        Op("set", "{on: bool} -> {on}: on turns incognito on (when the person asks); off is the person's own (the token at their wrist, the settings "
            + "menu, the desktop's key)", a =>
        {
            if (!a.Bool("on", true))
                throw new LeapError("incognito is turned off by the person themselves: the token at their left wrist, the settings menu or the desktop's key");
            S.Private.Set(true, "asked");
            return new JsonObject { ["on"] = S.Private.On };
        });
        Op("draft", "{text, token}: the sidecar's dictation: the person's words, heard on this PC, put up for them to send", a =>
        {
            if (!Sidecar.IsToken(a.Str("token"))) throw new LeapError("only the client's own sidecar passes on dictation");
            var text = (a.Str("text") ?? "").Trim();
            if (text.Length == 0) throw new LeapError("no words");
            if (Dictated is not { } d) throw new LeapError("dictation isn't available");
            d(text.Length > 1000 ? text[..1000] : text);
            return new JsonObject();
        });
        Op("always", "{person?: a person handle or id, here?: true, on=true} -> {}: incognito comes on by itself with that person nearby or in an IM, "
            + "or at this place (the parcel), until the person takes it back (on: false)", a =>
        {
            if (Remember is not { } remember) throw new LeapError("not available");
            var who = a.Uuid("person");
            bool here = a.Bool("here", false);
            if (who is null && !here) throw new LeapError("give person (a handle or id) or here: true");
            if (remember(who, here, a.Bool("on", true)) is { } why) throw new LeapError(why);
            return new JsonObject();
        });
    }
}
