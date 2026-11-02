using LibreMetaverse;
using Sojourn.Access.Walk;
using Sojourn.Session.Social;

namespace Sojourn.Access.Sound;

/// <summary>
/// A short sound per kind of thing arriving (M4, docs/access-app.md > Earcons), so kinds can be told apart before the
/// screen reader reaches the words, or without them while read-out is muted (F6 mutes the words, never these). Each
/// kind is off or at a level of its own (earcon_KIND); by default only what's addressed to the person and waits on
/// them sounds (an IM, a mention, a script's menu or question, an offer), since every line is read out anyway. Nearby
/// chat, objects' chat and people coming and going sound from where the speaker is (two metres from the head that way,
/// quieter the further they are), the rest centred. Nothing for the conversation the person is in, or a source muted
/// there (Shift+F6). In Sojourn Access these stand in for the viewer's new-IM and alert sounds (AudioWiring
/// .ConversationSound, SocialWiring.AppAlertSound), so a line never sounds twice. Every one has its words: the line
/// itself, read out and in its history. Someone given a sound of their own (ImSounds) rings it in place of the IM's (a
/// group: the group chat's). Written by Claude Opus 5.5, 2026-10-29.
/// </summary>
public sealed class Earcons
{
    readonly AccessSettings _set;
    readonly CueSounds _sounds;
    readonly WalkMode _walk;
    readonly Func<FeedEntry, bool> _quiet;

    /// <param name="quiet">True for a line the person is reading where it is (the conversation in focus) or has muted.</param>
    public Earcons(AccessSettings set, WalkMode walk, Func<FeedEntry, bool> quiet)
    {
        _set = set;
        _walk = walk;
        _sounds = walk.Sounds;
        _quiet = quiet;
    }

    static readonly HashSet<string> Menus = new() { "ScriptDialog", "ScriptDialogGroup", "ScriptQuestion", "ScriptQuestionCaution", "ScriptQuestionExperience" };
    static readonly HashSet<string> Offers = new()
    {
        "TeleportOffered", "TeleportRequest", "OfferFriendship", "OfferFriendshipNoMessage", "UserGiveItem", "ObjectGiveItem",
        "OwnObjectGiveItem", "JoinGroup", "LoadWebPage", "GotoURL", "PlaceOffered", "InvOfferAutoAcceptUser", "InvOfferAutoAcceptObject",
    };

    /// <summary>The kind an entry sounds as and its sound, or null for none (pure: tests and the status op).</summary>
    public static (string Kind, CueSounds.Cue Cue, bool Placed)? Classify(FeedEntry e)
    {
        if (e.Mine) return e.Kind is FeedKind.Nearby or FeedKind.Im or FeedKind.Group ? ("sent", CueSounds.Cue.EarSent, false) : null;
        return e.Kind switch
        {
            FeedKind.Nearby when e.Style == FeedStyle.Object => ("objects", CueSounds.Cue.EarObject, true),
            FeedKind.Nearby => ("nearby", CueSounds.Cue.EarNearby, true),
            FeedKind.Im => ("im", CueSounds.Cue.EarIm, false),
            FeedKind.Group => ("group", CueSounds.Cue.EarGroup, false),
            FeedKind.Notice => e.NoticeName switch
            {
                { } n when Menus.Contains(n) => ("menu", CueSounds.Cue.EarMenu, false),
                { } n when Offers.Contains(n) => ("offer", CueSounds.Cue.EarOffer, false),
                "FriendOnlineOffline" => ("friends", e.Text.Contains("offline") ? CueSounds.Cue.EarFriendOff : CueSounds.Cue.EarFriendOn, false),
                "RadarReport" => ("radar", e.Text.StartsWith("left") ? CueSounds.Cue.EarLeave : CueSounds.Cue.EarArrive, true),
                _ => ("notice", CueSounds.Cue.EarNotice, false),
            },
            _ => null,
        };
    }

    readonly Dictionary<CueSounds.Cue, long> _last = new();

    /// <summary>One of a kind at a time: offline IMs arriving together at login, or a busy group, ring once, not per line.</summary>
    bool Due(CueSounds.Cue cue)
    {
        long now = Environment.TickCount64;
        if (_last.TryGetValue(cue, out var at) && now - at < 350) return false;
        _last[cue] = now;
        return true;
    }

    /// <summary>An entry joined the feed (the window's thread).</summary>
    public void Arrived(FeedEntry e)
    {
        if (_quiet(e)) return;
        var own = OwnSound(e);
        // an IM from someone given a sound of their own: that sound, even when it names you (an IM is to you anyway)
        if (own is { Kind: "im" } && Ring(own.Value)) return;
        // a mention or keyword sounds as one, whatever chat it's in; with that kind off, as its own kind
        if (!e.Mine && e.Alert && _set.EarconLevel("mention") > 0)
        {
            if (Due(CueSounds.Cue.EarMention)) _sounds.Play(CueSounds.Cue.EarMention, _set.EarconLevel("mention"), null);
            return;
        }
        if (own is { Kind: "group" } && Ring(own.Value)) return;
        if (Classify(e) is not { } c) return;
        float level = _set.EarconLevel(c.Kind);
        if (level <= 0 || !Due(c.Cue)) return;
        if (c.Placed && _walk.Toward(e.From) is { } at)
        {
            // within chat range: at most a third quieter far off, so a far speaker is still heard
            _sounds.Play(c.Cue, level * (1 - 0.35f * Math.Clamp(at.Distance / 20f, 0, 1)), at.At);
            return;
        }
        _sounds.Play(c.Cue, level, null);
    }

    /// <summary>The sound the person gave who an IM is from, or a group's chat (ImSounds), with the kind whose level it
    /// plays at; null for none.</summary>
    (string Kind, CueSounds.Cue Cue)? OwnSound(FeedEntry e)
    {
        if (e.Mine) return null;
        var (kind, id) = e.Kind switch
        {
            FeedKind.Im when e.Style != FeedStyle.Object => ("im", e.From),
            FeedKind.Group when UUID.TryParse(e.Session, out var g) => ("group", g),
            _ => ("", UUID.Zero),
        };
        return id != UUID.Zero && ImSounds.CueOf(_set.ImSoundOf(id)) is { } cue ? (kind, cue) : null;
    }

    /// <summary>Plays someone's own sound at their kind's level, one at a time with that kind's usual sound (offline IMs
    /// from several people at login ring once); false when the kind is off, so it goes on as if they had none.</summary>
    bool Ring((string Kind, CueSounds.Cue Cue) own)
    {
        float level = _set.EarconLevel(own.Kind);
        if (level <= 0) return false;
        if (Due(own.Kind == "im" ? CueSounds.Cue.EarIm : CueSounds.Cue.EarGroup)) _sounds.Play(own.Cue, level, null);
        return true;
    }

    /// <summary>A sound being chosen for someone (as she moves through the list): played at the IM's (or group chat's)
    /// level, or medium while that kind is off, so it can be heard to choose.</summary>
    public void Try(CueSounds.Cue cue, bool group)
    {
        float level = _set.EarconLevel(group ? "group" : "im");
        _sounds.Play(cue, level > 0 ? level : 0.45f, null);
    }

    /// <summary>The sound a setting's row stands for, to play when the person changes it (Alt+0), so each can be learned.</summary>
    public static CueSounds.Cue? SoundOf(string setting) => setting switch
    {
        "earcon_im" => CueSounds.Cue.EarIm, "earcon_mention" => CueSounds.Cue.EarMention, "earcon_menu" => CueSounds.Cue.EarMenu,
        "earcon_offer" => CueSounds.Cue.EarOffer, "earcon_group" => CueSounds.Cue.EarGroup, "earcon_nearby" => CueSounds.Cue.EarNearby,
        "earcon_objects" => CueSounds.Cue.EarObject, "earcon_friends" => CueSounds.Cue.EarFriendOn, "earcon_radar" => CueSounds.Cue.EarArrive,
        "earcon_notice" => CueSounds.Cue.EarNotice, "earcon_sent" => CueSounds.Cue.EarSent, "earcon_mic" => CueSounds.Cue.MicOn,
        "earcon_deleted" => CueSounds.Cue.EarDeleted,
        "walk_steps_volume" => CueSounds.Cue.StepStone, "walk_walls_volume" => CueSounds.Cue.Wall, "walk_openings_volume" => CueSounds.Cue.Opening,
        "walk_edges_volume" => CueSounds.Cue.Edge, "walk_turns_volume" => CueSounds.Cue.Turn, "walk_sonar_volume" => CueSounds.Cue.PingMid,
        "walk_vehicle_volume" => CueSounds.Cue.Wall, "walk_beacon_volume" => CueSounds.Cue.BeaconAhead, "walk_marks_volume" => CueSounds.Cue.MarkSeat,
        "access_sounds_volume" => CueSounds.Cue.EarIm,
        _ => null,
    };

    /// <summary>The friends online at login, said as one line: their sound once.</summary>
    public void FriendsOnline()
    {
        float level = _set.EarconLevel("friends");
        if (level > 0 && Due(CueSounds.Cue.EarFriendOn)) _sounds.Play(CueSounds.Cue.EarFriendOn, level, null);
    }

    /// <summary>Something of the person's deleted (a confirmed delete that went through).</summary>
    public void Deleted()
    {
        float level = _set.EarconLevel("deleted");
        if (level > 0) _sounds.Play(CueSounds.Cue.EarDeleted, level, null);
    }

    /// <summary>Voice chat's mic opening or closing (push to talk, an open mic's mute): a short click, centred.</summary>
    public void Mic(bool open)
    {
        float level = _set.EarconLevel("mic");
        if (level > 0) _sounds.Play(open ? CueSounds.Cue.MicOn : CueSounds.Cue.MicOff, level, null);
    }

    /// <summary>Plays a setting's sound at its level now (after the person changed it); false when it's off or has none.</summary>
    public bool Preview(string setting)
    {
        if (SoundOf(setting) is not { } cue) return false;
        float level = setting.StartsWith("earcon_") ? _set.EarconLevel(setting[7..])
            : setting.StartsWith("walk_") ? _set.WalkLevel(setting[5..^7])
            : 0.45f;
        if (level <= 0) return false;
        _sounds.Play(cue, level, null);
        return true;
    }
}
