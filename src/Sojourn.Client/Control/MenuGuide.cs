namespace Sojourn.Client.Control;

/// <summary>
/// Every settings-menu row's tip (what it does, in plain words, shown when the pointer rests on it) and its level: S
/// rows show in the simple menu (the default), A rows only with "advanced menu" on. The test for S: would a person using the
/// assistant need to change this by hand, rather than just asking? (the user, 2026-10-29: Sojourn is assistant first;
/// graphics are for asking, draw distance excepted.) Hiding a row changes only the menu, never the setting or what the
/// assistant can reach. A section with no S rows isn't shown in the simple menu; a group marked S shows all its rows.
/// Paths: "section" (its tab), "section/label", a group's own row "section/group/" (a row and a group can share a
/// label), a row in it "section/group/label"; a * stands for a part of a label that changes (a name, a
/// count). A row missing here is advanced and has no tip.
/// </summary>
public static class MenuGuide
{
    static readonly (string Path, bool Simple, string Tip)[] Rows =
    {
        // --- tabs
        A("moving", "How you move, turn and stand, and what others see of you."),
        A("assistant", "The assistant: who answers, how it looks and sounds, what it remembers."),
        A("incognito", "Times when nothing is kept and the assistant sees only a general picture."),
        A("you", "How you show to others, who can reach you, your animations, and fixes for your avatar."),
        A("social", "Names, chat and alerts about the people around you."),
        A("voice", "Talking with people by voice."),
        A("sound", "Volumes, music, speakers, and web pages and videos on objects."),
        A("screen reader", "What your screen reader reads out, if you use one."),
        A("safety", "Comfort, being pushed around, aiming, and things that flood or take over."),
        A("view", "How far and how well you see, and the menus themselves."),

        // --- moving
        S("moving/posture", "Standing or seated. Seated, sit up straight and press Y to set your height."),
        A("moving/eye height", "See from your own height, or from your avatar's eyes."),
        S("moving/turning", "How the right stick turns you: smoothly, or in quick steps."),
        A("moving/turn speed", "How fast smooth turning goes."),
        A("moving/snap angle", "How far each quick turn goes."),
        A("moving/moving toward", "The left stick walks you where you look, or where your left hand points."),
        A("moving/movement", "Server follows your avatar where the region puts it, a moment behind. Predicted moves you at once and lets the region correct you."),
        A("moving/putting you back", "When your avatar can't follow you, it's teleported to you, or your view jumps or slides back to it."),
        A("moving/point to teleport", "Hold the left trigger on open ground to teleport there."),
        A("moving/look around", "Look around away from your avatar, which stays where it is. Holding Y starts it too."),
        A("moving/your body", "Shows your avatar's body when you look down."),
        A("moving/tracking", "Your avatar's arms follow your hands. Auto lets dances and poses from things around you take over."),
        A("moving/pointer", "The ray from your right hand that touches, sits and presses. A quick press of A turns it on or off."),
        A("moving/your gaze", "What others see of where you look: your head turning toward who you look at, only the direction, or nothing."),

        // --- assistant
        S("assistant/openrouter model", "Which model OpenRouter runs. Claude keeps the usual models; another maker's model answers everything itself and may not do all Claude does. The provider and its key are set in the launcher."),
        S("assistant/model","Who answers first. Haiku is quickest and opus the most careful; the quicker ones hand big jobs to opus."),
        A("assistant/haiku effort", "How hard haiku thinks before acting. More is slower and surer."),
        A("assistant/sonnet effort", "How hard sonnet thinks before acting. More is slower and surer."),
        A("assistant/opus effort", "How hard opus thinks before acting. More is slower and surer."),
        A("assistant/look", "The look of the assistant's panel and the menus."),
        A("assistant/adaptive ink", "Text turns light over dark places and dark over bright ones, so clear glass stays readable."),
        A("assistant/spoken replies", "The assistant's replies are read aloud as well as shown."),
        S("assistant/memory", "The assistant keeps notes about you between sessions. Only you can turn this on or off."),
        A("assistant/hearing", "What turns your speech into words. Parakeet and whisper run on this PC; soniox is an online service."),
        S("assistant/forget memory", "Clears everything the assistant remembers about you. Press twice to be sure."),
        A("assistant/called *", "The assistant goes by the name you gave it."),
        A("assistant/its character", "The assistant takes on the character you described."),
        S("assistant/ask before touching others' things", "The assistant waits for your press before it touches or answers menus on things that aren't yours."),
        S("assistant/advanced menu", "Shows every setting, for experienced Second Life players. Either way, the assistant can change any of them when you ask."),

        // --- incognito (by hand on purpose: some of it is better not said aloud)
        S("incognito/incognito", "Nothing from now on is kept, and the assistant sees only a general picture. Only you can end it."),
        S("incognito/let the assistant see", "During incognito the assistant sees and helps as usual. Still nothing is kept."),
        S("incognito/always here", "Incognito comes on by itself whenever you're on this land."),
        S("incognito/always at *", "Incognito comes on by itself at this place. Off takes the place off the list."),
        S("incognito/always with *", "Incognito comes on by itself when this person is near."),
        S("incognito/while an IM is open", "Incognito is on while a private conversation is open."),

        // --- you
        A("you/do not disturb", "Others see you as busy. Messages wait for you, and each person who writes gets your reply once."),
        A("you/away", "Shows you to others as away."),
        A("you/away after", "How long without you before you show as away by yourself."),
        A("you/hover height", "Raises or lowers your avatar so its feet meet the floor."),
        A("you/show typing", "Others see when you're typing."),
        A("you/turn away/", "Who can message, call, befriend, invite or teleport you."),
        A("you/turn away/only friends IM me", "Messages from anyone who isn't your friend are turned away."),
        A("you/turn away/teleports", "Turn away teleport offers: none, those not from friends, or all."),
        A("you/turn away/friendships", "Turn away friendship offers."),
        A("you/turn away/group invites", "Turn away invitations to groups."),
        A("you/turn away/one-to-one calls", "Turn away voice calls from one person."),
        A("you/turn away/group calls", "Turn away voice calls to your groups."),
        A("you/turn away/conference calls", "Turn away voice calls with several people."),
        A("you/turn away calls from/", "Turn away one group's voice calls and let the others ring."),
        A("you/turn away calls from/*", "On: this group's voice calls are turned away before they ring."),
        A("you/camera", "Takes out a camera. The right trigger takes a picture of what's in its frame."),
        A("you/AO/", "Your animation overrider: your own stands, walks and sits."),
        A("you/AO/AO", "Plays your own stands, walks, sits and the rest instead of the usual ones."),
        A("you/AO/set", "Which set of animations it plays."),
        A("you/AO/stands", "Your stands are replaced too, not only walks and sits."),
        A("you/AO/OpenCollar", "An OpenCollar you wear can switch the AO."),
        A("you/AO/LockMeister", "LockMeister items you wear can switch the AO."),
        A("you/AO/lock its folder", "The AO's folder can't be moved, renamed or deleted by mistake."),
        A("you/skeleton", "Fixes your avatar when its shape looks stretched or bent wrong."),
        A("you/attachments", "Reloads what you're wearing when something is missing or out of place."),
        A("you/animations", "Restarts your animations when others see you stuck or out of step."),

        // --- social
        A("social/name tags", "Names over people's heads."),
        A("social/chat feed", "Nearby chat, messages and notices shown on the left."),
        A("social/hover text", "Floating text over objects, like prices and signs."),
        A("social/chat bubbles", "What people say shows over their heads for a while."),
        A("social/tags through walls", "Names show through walls and things in front of people."),
        A("social/group titles", "People's group titles over their names."),
        A("social/friend colour", "Your friends' names are ringed in colour."),
        A("social/hover tips", "Resting the pointer on something shows its name, and its price if it's for sale, after this long."),
        A("social/alerts/", "When chat mentions you or your keywords."),
        A("social/alerts/mentions", "A line that mentions you is lit, with a sound."),
        A("social/alerts/keywords", "A line with one of your keywords is lit, with a sound."),
        A("social/alerts/sound", "Mentions and keywords play a sound."),
        A("social/radar/", "Being told when people come and go around you."),
        A("social/radar/chat range", "Tells you when people come within chat range, leave it, or both."),
        A("social/radar/draw distance", "Tells you when people come within sight, leave it, or both."),
        A("social/radar/region", "Tells you when people arrive in the region, leave it, or both."),
        A("social/radar/new accounts", "Tells you when an account this new comes near."),

        // --- voice
        A("voice/voice chat", "Hear and talk with people by voice."),
        A("voice/talking", "Hold the left grip to talk, or keep the mic open and mute it when you like."),
        S("voice/mic on", "Your microphone is live to the people around you. Only you can turn it on."),
        A("voice/volume", "How loud people's voices are."),
        A("voice/hear from", "Hear voices from where your head is, where your avatar is, or between the two."),

        // --- sound
        A("sound/volume", "How loud everything is."),
        A("sound/sounds", "How loud sounds from the world are."),
        A("sound/music", "The land's music stream."),
        A("sound/music volume", "How loud the music is."),
        A("sound/speakers", "Which speakers or headphones Sojourn plays through."),
        A("sound/microphone", "Which microphone Sojourn listens to."),
        A("sound/media autoplay", "Which web pages and videos on objects play by themselves: yours, everyone's, or none until you touch them."),
        A("sound/media volume", "How loud web pages and videos are."),
        A("sound/media first click", "Whose web pages and videos your first click goes to, rather than to the object."),
        A("sound/zoom to media", "Clicking a web page or video brings it up close."),

        // --- screen reader
        A("screen reader/read out", "What arrives is read out through your screen reader. Auto does it whenever one is running."),
        A("screen reader/nearby chat", "Read out what people nearby say."),
        A("screen reader/objects", "Read out what objects say."),
        A("screen reader/IMs", "Read out private messages."),
        A("screen reader/group chat", "Read out group chat."),
        A("screen reader/notices", "Read out offers, menus, invitations and alerts."),
        A("screen reader/the assistant", "Read out the assistant's replies."),
        A("screen reader/music titles", "Read out each song's title as the music changes."),

        // --- safety
        A("safety/comfort vignette", "The edges of the view darken while you move: never, only when something else moves you, or always."),
        A("safety/vignette strength", "How dark the edges get."),
        A("safety/keep me in place", "When something pushes you away, you're put back where you were."),
        A("safety/aim", "Aiming for weapons and games. Auto aims whenever a script asks for it."),
        A("safety/aim with", "Aim where you look, or with your right hand's ray."),
        A("safety/particles", "Particle effects like sparkles, smoke and fire. Off hides them all."),
        A("safety/antispam", "Blocks people and objects that flood you with messages, offers or menus."),
        S("safety/controls a script took", "Takes back the controls a script has taken over, like your movement."),
        S("safety/* hidden", "Shows again everything you've hidden from view."),

        // --- view
        A("view/menu size", "How big the menus and the assistant's panel are."),
        A("view/menu positions", "Brings every menu you've moved back in front of you, following you again."),
        S("view/draw distance", "How far you can see. Further shows more, but loads more and can slow things down."),
        S("view/quality", "Light, balanced or full: shadows, reflections, detail and distance set together, as in the launcher. Yours: changed by hand."),
        A("view/graphics/", "Shadows, reflections, detail and the rest of how the world is drawn."),
        A("view/graphics/shadows", "Shadows from the sun, moon and lights. Off is easier on the computer."),
        A("view/graphics/reflections", "Shiny surfaces reflect what's around them. Off, they reflect only the sky."),
        A("view/graphics/mirrors", "Mirrors people have built show reflections, avatars too."),
        A("view/graphics/flexible prims", "Old-style hair, skirts, flags and plants sway and hang. Off, they stay still."),
        A("view/graphics/detail", "How far away things keep their full shape. Higher looks better and costs more."),
        A("view/graphics/load governor", "When things run slow, loading slows and far things get simpler for a while, to keep the view smooth."),
        A("view/graphics/see-through layers", "How many layers of glass, leaves and flowers are drawn behind one another. Fewer is faster."),
        A("view/graphics/see-through light", "Glass and leaves lit fully, or more simply and faster."),
        A("view/graphics/shadow & ssao size", "Sun shadows and soft shading in corners worked out at full or half size. Half is faster, a little softer."),
        A("view/graphics/far shadow margin", "Far shadows drawn a little larger so they can be reused while you move. Faster, a little softer far away."),
        A("view/graphics/far shadows", "Shadows carried on far away, softer, so they don't stop in a ring around you. Off: they end as in other viewers."),
        A("view/graphics/small shadow casters", "Skips shadows from tiny things. Faster, with almost no change you'd see."),
        A("view/graphics/g-buffer (on restart)", "Narrow is lighter on the computer, with a faint change on shiny curves. Applies next time Sojourn starts."),
        A("view/graphics/resolution", "How sharp the view is drawn. Auto follows what the computer can keep up with."),
        A("view/graphics/depth reprojection", "When a picture is late, the headset uses depth to shift the last one more accurately as you move."),
        A("view/graphics/load all around", "Things behind you load as soon as those in front, at the cost of more network traffic."),
        A("view/graphics/test lag", "For testing: adds lag on purpose."),
        A("view/graphics/desktop mirror", "For testing: the copy of the view on the PC's window."),
        A("view/avatars/", "How other people's avatars are drawn."),
        A("view/avatars/full avatars", "How many of the nearest avatars are drawn whole. The rest are lighter stand-ins."),
        A("view/avatars/complexity limit", "Avatars heavier than this show as plain shapes, to keep things smooth."),
        A("view/avatars/avatar physics", "Bodies sway and bounce as their owners set them up."),
        A("view/avatars/lip sync", "People's mouths move when they speak by voice."),
        A("view/avatars/selection beams", "Beams from people's hands to what they point at or build."),
        A("view/sky", "The region's own sky, or a time of day just for you."),
        A("view/time of day", "Moves the sun earlier or later, just for you. Press the sky again to put it back."),
        A("view/location line", "Where you are, above your left wrist, with a lag meter if you like. Press it for the map."),
        A("view/beacons/", "Markers on objects around you of the kinds you choose."),
        A("view/beacons/beacons", "Shows markers on the kinds of objects below."),
        A("view/beacons/scripted", "Mark objects with scripts."),
        A("view/beacons/touch", "Mark objects that do something when touched."),
        A("view/beacons/physical", "Mark objects that move under physics."),
        A("view/beacons/sounds", "Mark objects playing sounds."),
        A("view/beacons/particles", "Mark objects giving off particles."),
        A("view/beacons/media", "Mark objects with web pages or video."),
        A("view/beacons/lines", "A line from you to each marked object."),
        A("view/beacons/line width", "How thick those lines are."),
        A("view/beacons/highlights", "Marked objects glow as well."),
        A("view/beacons/sun", "A marker toward the sun."),
        A("view/beacons/moon", "A marker toward the moon."),
        A("view/beacons/highlight transparent", "Tints every see-through surface, to find invisible things."),
        A("view/beacons/tracking beacon", "A marker toward a place or person you're tracking on the map."),
        A("view/beacons/tracking beep", "A beep that quickens as you near what you're tracking."),
        A("view/parcel lines/", "Lines at land borders and where you can't go."),
        A("view/parcel lines/property lines", "Lines on the ground along land borders, in their owners' colours."),
        A("view/parcel lines/ban lines", "Walls at land you can't enter: hidden, when you bump into them, or when you're near."),
        A("view/maturity", "The highest content rating of places you'll go to."),
        A("view/menus/", "How menus meet walls, and whether they show on the PC's window."),
        A("view/menus/menus over walls", "Menus always show, even with a wall between you and them."),
        A("view/menus/wall test", "How walls hide menus. Exact is Sojourn's own and the most precise."),
        A("view/menus/frost bias", "Fine-tunes where walls hide the blur behind menus."),
        A("view/menus/menus on desktop", "The menus show on the PC's window too."),
    };

    static (string, bool, string) S(string path, string tip) => (path, true, tip);
    static (string, bool, string) A(string path, string tip) => (path, false, tip);

    static readonly Dictionary<string, (bool Simple, string Tip)> Exact = ExactRows();

    static Dictionary<string, (bool, string)> ExactRows()
    {
        var d = new Dictionary<string, (bool, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, simple, tip) in Rows) if (!path.Contains('*')) d[path] = (simple, tip);
        return d;
    }
    static readonly (string Before, string After, bool Simple, string Tip)[] Patterns = Rows.Where(r => r.Path.Contains('*'))
        .Select(r => (r.Path[..r.Path.IndexOf('*')], r.Path[(r.Path.IndexOf('*') + 1)..], r.Simple, r.Tip)).ToArray();

    static (bool Simple, string Tip)? Find(string path)
    {
        if (Exact.TryGetValue(path, out var e)) return e;
        foreach (var p in Patterns)
            if (path.Length > p.Before.Length + p.After.Length && path.StartsWith(p.Before, StringComparison.OrdinalIgnoreCase)
                && path.EndsWith(p.After, StringComparison.OrdinalIgnoreCase)) return (p.Simple, p.Tip);
        return null;
    }

    static string PathOf(string section, string? group, MenuRow? row) =>
        row == null ? section
        : row.Rows != null ? $"{section}/{row.Label}/"
        : group == null ? $"{section}/{row.Label}" : $"{section}/{group}/{row.Label}";

    /// <summary>The tip for a tab (row null), a section's row (group null), or a row in an open group.</summary>
    public static string? Tip(string section, string? group, MenuRow? row) => Find(PathOf(section, group, row))?.Tip;

    /// <summary>Whether a section's row (or group) shows in the simple menu.</summary>
    public static bool Simple(string section, MenuRow row) => Find(PathOf(section, null, row))?.Simple ?? false;
}
