using System.Text.Json.Nodes;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Session.Assistant;
using Sojourn.Session.Control;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

namespace Sojourn.Session.Social;

/// <summary>
/// Batch B4's wiring (the social layer), kept out of Program.cs: its settings (social-settings.json beside
/// vr-settings.json; the keywords per account), keyword alerts and mentions on the feed with the viewer's alert sound,
/// Firestorm's radar reports (Radar.cs, ticked once a second), translation's plumbing (Translation.cs), the reader's
/// links offering their places, and the agent API's SojournSocial. What it draws lives in NameTags (marks, titles,
/// colours, bubbles, walls), HoverTips and FeedPainter (alerts, links, translations). Claude Opus 5.5, 2026-10-28.
/// </summary>
public sealed class SocialWiring : IDisposable
{
    // the viewer's UISndAlert (OpenSim ships it too)
    static readonly UUID AlertSound = new("ed124764-705d-d497-167a-182cd9fa2e6c");

    readonly AgentSession _s;
    readonly SocialSettings _settings;
    readonly Audio.WorldSounds? _sounds;
    readonly ILogger _log;
    readonly string _path;
    readonly Timer _timer;
    public readonly Radar Radar;
    public readonly Translation Translation;
    public Keywords Keywords { get; private set; }
    /// <summary>An app plays its own sound for alerts (Sojourn Access's earcons): the viewer's alert sound is left out.</summary>
    public bool AppAlertSound { get; set; }
    int _alerts;

    SocialSettings St => _settings;

    /// <param name="settings">The social layer's settings, which the app's views read too (the VR client's SocialView.Settings).</param>
    /// <param name="reader">The reader, whose links offer their places (null: none).</param>
    public static SocialWiring Start(AgentSession s, SocialFeed feed, SocialSettings settings, Reader? reader, Audio.WorldSounds? sounds, string vrSettingsPath, ILogger log) =>
        new(s, feed, settings, reader, sounds, vrSettingsPath, log);

    SocialWiring(AgentSession s, SocialFeed feed, SocialSettings settings, Reader? reader, Audio.WorldSounds? sounds, string vrSettingsPath, ILogger log)
    {
        _s = s;
        _settings = settings;
        _sounds = sounds;
        _log = log;
        _path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(vrSettingsPath)) ?? ".", "social-settings.json");
        settings.CopyFrom(SocialSettings.Load(_path, m => log.LogWarning("{Msg}", m)));
        Keywords = Keywords.Load(s.StateDir);
        Radar = new Radar(s, settings);
        Translation = new Translation(feed, settings, () => s.Private.On);
        feed.Alerting = Alerting;
        feed.Joined += e =>
        {
            if (!e.Alert) return;
            Interlocked.Increment(ref _alerts);
            if (St.AlertSound && !AppAlertSound) _sounds?.Trigger(AlertSound, _s.Client?.Self.AgentID ?? UUID.Zero, 1, Audio.WorldSounds.Kind.Ui, null);
        };
        if (reader != null) reader.LinkPressed += place =>
        {
            var doc = reader.Doc;
            _s.Places.Offer(place, "notecard", doc?.Title ?? "");
        };
        s.Api.Register(new SocialApi(s, this));
        _timer = new Timer(_ => Tick(), null, 2000, 1000);
    }

    public void Dispose() => _timer.Dispose();

    void Tick()
    {
        try { Radar.Tick(); }
        catch (Exception e) { _log.LogDebug(e, "radar"); }
    }

    bool Alerting(FeedKind kind, FeedStyle style, string text, bool mention)
    {
        if (mention && St.MentionAlerts) return true;
        return St.KeywordAlerts && Keywords.Matches(kind, text);
    }

    public int Alerts => _alerts;

    public void Save() => St.Save(_path, m => _log.LogWarning("{Msg}", m));

    public void SaveKeywords(Keywords k)
    {
        Keywords = k;
        k.Save(_s.StateDir);
    }

    // --- settings ---------------------------------------------------------------------------------------------------

    public void Settle(SettingsApi api)
    {
        string OnOff(bool b) => b ? "on" : "off";
        Func<JsonNode, string?> Sw(string name, Action<bool> set) => SettingsApi.Switch(name, on => { set(on); Save(); });
        void RadarSetting(string name, string what, Func<string> get, Action<string> set) =>
            api.Add(name, new("social", $"Firestorm's radar report for {what} (off by default, as there): someone coming in (enter), going (leave) "
                + "or both, told on the feed", string.Join(", ", SocialSettings.RadarModes), () => JsonValue.Create(get()),
                SettingsApi.Choice(name, SocialSettings.RadarModes, i => { set(SocialSettings.RadarModes[i]); Save(); })));

        api.Add("chat_bubbles", new("social", "the viewer's chat bubbles: what people say nearby shown over their name tags for a while (off by default, as there)",
            "on, off", () => OnOff(St.ChatBubbles), Sw("chat_bubbles", on => St.ChatBubbles = on)));
        api.Add("tag_group_titles", new("social", "people's group titles over their names on their tags (near or looked at, as the username)", "on, off",
            () => OnOff(St.GroupTitles), Sw("tag_group_titles", on => St.GroupTitles = on)));
        api.Add("tag_friend_colour", new("social", "friends' tags ringed in the friends' colour (the viewer's NameTagShowFriends; off by default); "
            + "people in a contact set (SojournPeople contactSet) are ringed in its colour either way", "on, off",
            () => OnOff(St.FriendColours), Sw("tag_friend_colour", on => St.FriendColours = on)));
        api.Add("tags_through_walls", new("social", "name tags shown through walls and things in front of them (off: hidden behind them, as the viewer's)",
            "on, off", () => OnOff(St.TagsThroughWalls), Sw("tags_through_walls", on => St.TagsThroughWalls = on)));
        api.Add("hover_tips", new("social", "the pointer resting on a thing shows its name (and price, for sale) after hover_tip_delay (the viewer's tooltips)",
            "on, off", () => OnOff(St.HoverTips), Sw("hover_tips", on => St.HoverTips = on)));
        api.Add("hover_tip_delay", new("social", "seconds the pointer rests before a hover tip shows (the viewer's 0.7)", "0.1-3",
            () => JsonValue.Create(Math.Round(St.HoverTipDelay, 2)), SettingsApi.Number("hover_tip_delay", 0.1f, 3, " s", f => { St.HoverTipDelay = f; Save(); })));
        api.Add("mention_alerts", new("social", "a line that @mentions the person (the viewer's chat mentions) is lit on the feed with a sound", "on, off",
            () => OnOff(St.MentionAlerts), Sw("mention_alerts", on => St.MentionAlerts = on)));
        api.Add("keyword_alerts", new("social", "a line holding one of the person's keywords (SojournSocial keywords) is lit on the feed with a sound "
            + "(Firestorm's keyword alerts)", "on, off", () => OnOff(St.KeywordAlerts), Sw("keyword_alerts", on => St.KeywordAlerts = on)));
        api.Add("alert_sound", new("social", "keyword alerts and mentions play the viewer's alert sound", "on, off",
            () => OnOff(St.AlertSound), Sw("alert_sound", on => St.AlertSound = on)));
        RadarSetting("radar_chat_range", "chat range (20 m)", () => St.RadarChat, v => St.RadarChat = v);
        RadarSetting("radar_draw_distance", "the draw distance", () => St.RadarDraw, v => St.RadarDraw = v);
        RadarSetting("radar_region", "the region", () => St.RadarRegion, v => St.RadarRegion = v);
        api.Add("radar_age_alert", new("social", "someone this many days old or younger coming within draw distance is told on the feed "
            + "(Firestorm's age alert; 0: off, the default)", "0-365 days", () => JsonValue.Create(St.RadarAgeDays),
            SettingsApi.Number("radar_age_alert", 0, 365, " days", f => { St.RadarAgeDays = (int)MathF.Round(f); Save(); })));
        api.Add("translate_chat", new("social", "other people's lines on the feed translated into translate_to, under each line (the viewer's "
            + "TranslateChat; off by default). Needs a translator: which service may see people's words is the person's call", "on, off",
            () => OnOff(St.Translate), v =>
            {
                bool none = St.Translator == "none" || !Translation.Names.Contains(St.Translator, StringComparer.OrdinalIgnoreCase);
                if (v.ToString().Trim().ToLowerInvariant() is "on" or "true" && none)
                    return "translate_chat: no translator is set up (translator); which service may see people's words is the person's call";
                return Sw("translate_chat", on => St.Translate = on)(v);
            }));
        api.Add("translate_to", new("social", "the language lines are translated into (ISO 639-1, e.g. en, de, ja); empty: the system's", "a language code",
            () => JsonValue.Create(Translation.To), v =>
            {
                var code = v.ToString().Trim().ToLowerInvariant();
                if (code.Length != 0 && (code.Length is < 2 or > 3 || !code.All(char.IsAsciiLetterLower))) return "translate_to takes a language code such as en, de or ja";
                St.TranslateTo = code;
                Save();
                return null;
            }));
        api.Add("translator", new("social", "what translates the feed's lines: none (the default) or one set up on this computer", string.Join(", ", Translation.Names),
            () => JsonValue.Create(St.Translator), v =>
            {
                var names = Translation.Names.ToArray();
                return SettingsApi.Choice("translator", names, i => { St.Translator = names[i]; if (names[i] == "none") St.Translate = false; Save(); })(v);
            }));
    }
}

/// <summary>
/// SojournSocial: the social layer for callers of the agent API. Keywords (Firestorm's keyword alerts: the person's
/// words that light a line on the feed), the radar (who's in chat range, draw distance and the region now, and its
/// reports' switches), what's on the tags, translation, and a status of it all. The switches themselves are
/// SojournSettings' (section social).
/// </summary>
public sealed class SocialApi : LeapApi
{
    readonly SocialWiring _w;

    public override string Name => "SojournSocial";
    public override string Desc => "the social layer: keyword alerts, radar, name tags' marks, chat bubbles, hover tips, translation (switches in SojournSettings, section social)";
    public override bool NeedsSession => false;

    public SocialApi(AgentSession s, SocialWiring w) : base(s)
    {
        _w = w;
        Op("status", "-> {keywords, alerts, radar_reports, translation}: the social layer as it stands (its switches: SojournSettings get, section social)", _ => Status());
        Op("keywords", "{words?: [..] | add?: word | remove?: word, in_chat?, in_ims?, in_groups?, whole_words?, case_sensitive?} -> the keyword "
            + "list and how it matches: a line from someone else holding one is lit on the feed with a sound (keyword_alerts); per account", KeywordsOp);
        Op("radar", "{range?: chat|draw|region} -> {people: [{id, name, distance, chat, draw, region}]}: who the radar sees now (nearest first)", RadarOp);
        Op("translate", "{text, to?} -> {text, from}: a text translated by the person's translator (none is set up unless they chose one)",
            async (a, ct) =>
            {
                var text = a.Str("text") ?? throw new LeapError("text: what to translate");
                try
                {
                    var r = await _w.Translation.TranslateAsync(text, a.Str("to"), ct);
                    return r is { } ok ? new JsonObject { ["text"] = ok.Text, ["from"] = ok.From } : new JsonObject { ["text"] = text, ["same"] = true };
                }
                catch (InvalidOperationException e) { throw new LeapError(e.Message); }
            });
    }

    JsonObject Status()
    {
        var k = _w.Keywords;
        var (done, failed, waiting) = _w.Translation.Counts;
        return new JsonObject
        {
            ["keywords"] = KeywordsJson(k),
            ["alerts"] = _w.Alerts,
            ["radar_reports"] = _w.Radar.Reports,
            ["translation"] = new JsonObject
            {
                ["translators"] = new JsonArray(_w.Translation.Names.Select(n => (JsonNode)n).ToArray()),
                ["to"] = _w.Translation.To, ["idle"] = _w.Translation.Idle(), ["translated"] = done, ["failed"] = failed, ["waiting"] = waiting,
            },
        };
    }

    static JsonObject KeywordsJson(Keywords k) => new()
    {
        ["words"] = new JsonArray(k.Words.Select(w => (JsonNode)w).ToArray()),
        ["in_chat"] = k.InChat, ["in_ims"] = k.InIms, ["in_groups"] = k.InGroups, ["whole_words"] = k.WholeWords, ["case_sensitive"] = k.CaseSensitive,
    };

    JsonNode KeywordsOp(JsonObject a)
    {
        var have = _w.Keywords;
        var k = new Keywords
        {
            Words = have.Words.ToList(), InChat = have.InChat, InIms = have.InIms, InGroups = have.InGroups,
            WholeWords = have.WholeWords, CaseSensitive = have.CaseSensitive,
        };
        bool changed = false;
        if (a["words"] is JsonArray ws)
        {
            k.Words = ws.Select(w => w?.ToString().Trim() ?? "").Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            changed = true;
        }
        else if (a["words"] is JsonValue one && one.ToString() is { } list)
        {
            // a comma-separated list, as Firestorm keeps it
            k.Words = list.Split(',').Select(w => w.Trim()).Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            changed = true;
        }
        if (a.Str("add") is { Length: > 0 } add && !k.Words.Contains(add.Trim(), StringComparer.OrdinalIgnoreCase)) { k.Words.Add(add.Trim()); changed = true; }
        if (a.Str("remove") is { Length: > 0 } rem) changed |= k.Words.RemoveAll(w => string.Equals(w, rem.Trim(), StringComparison.OrdinalIgnoreCase)) > 0;
        if (k.Words.Count > 100 || k.Words.Any(w => w.Length > 64)) throw new LeapError("keywords: at most 100, each up to 64 characters");
        void Flag(string name, Action<bool> set) { if (a.Bool(name) is { } b) { set(b); changed = true; } }
        Flag("in_chat", b => k.InChat = b);
        Flag("in_ims", b => k.InIms = b);
        Flag("in_groups", b => k.InGroups = b);
        Flag("whole_words", b => k.WholeWords = b);
        Flag("case_sensitive", b => k.CaseSensitive = b);
        if (changed) _w.SaveKeywords(k);
        var o = KeywordsJson(changed ? k : have);
        if (changed) o["changed"] = true;
        return o;
    }

    JsonNode RadarOp(JsonObject a)
    {
        var range = a.Str("range");
        var list = new JsonArray();
        foreach (var (id, d, chat, draw, region) in _w.Radar.Now().OrderBy(n => n.D))
        {
            if (range == "chat" && !chat || range == "draw" && !draw || range == "region" && !region) continue;
            list.Add(new JsonObject
            {
                ["id"] = id.ToString(), ["name"] = S.People.NameOf(id), ["distance"] = Math.Round(d, 1), ["chat"] = chat, ["draw"] = draw, ["region"] = region,
            });
        }
        return new JsonObject { ["people"] = list };
    }
}
