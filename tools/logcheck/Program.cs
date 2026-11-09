// tools/logcheck: LogShare (src/Sojourn.Session/LogShare.cs) against a synthetic log, every person and word in it made
// up. Exit code: the number of failed checks. With a path: that log printed as it would be sent.
using Sojourn.Session;

if (args.Length > 0)
{
    var r = LogShare.Filter(LogShare.Read(args[0]));
    Console.WriteLine(r?.Text ?? "(from before the records: it can't be filtered, so it isn't offered)");
    return 0;
}

const string Me = "11111111-1111-1111-1111-111111111111", Kestrel = "22222222-2222-2222-2222-222222222222",
    Wren = "33333333-3333-3333-3333-333333333333", Oriole = "44444444-4444-4444-4444-444444444444";
var log = $$"""
10:00:00 ~log {"v":2}
10:00:01 info: sojourn[0] agent API on http://127.0.0.1:9725
10:00:02 ~name {"id":"{{Me}}","legacy":"Juniper Tester","me":true}
10:00:02 ~name {"id":"{{Kestrel}}","legacy":"Kestrel Tester"}
10:00:02 ~name {"id":"{{Wren}}","legacy":"wrenfeather Resident","display":"Wren Visitor"}
10:00:02 ~name {"id":"{{Wren}}","pseudonym":"Birdie"}
10:00:02 ~name {"id":"{{Oriole}}","legacy":"Oriole Quill"}
10:00:02 ~name {"id":"77777777-7777-7777-7777-777777777777","legacy":"Voice Smith","display":"Sojourn Fan"}
10:00:03 info: agent.pilot[0] logged in as Juniper Tester (juniper.tester), state C:\Users\someperson\AppData\Local\Sojourn Access\state\agni\juniper.tester
10:00:04 ~words {"kind":"nearby","from":"people","id":"{{Kestrel}}","name":"Kestrel Tester","style":"say","text":"the zebra marmalade is hidden under the old lighthouse"}
10:00:05 ~heard {"from":"people","how":"arrival","via":"NVDA","text":"Kestrel Tester: the zebra marmalade is hidden under the old lighthouse"}
10:00:06 ~words {"kind":"nearby","from":"me","id":"{{Me}}","name":"Juniper Tester","style":"say","text":"hello Kestrel, where is it?"}
10:00:07 ~words {"kind":"im","from":"people","id":"{{Wren}}","name":"Birdie","session":"Birdie","style":"say","text":"quokka velvet"}
10:00:08 ~words {"kind":"im","from":"me","id":"{{Me}}","name":"Juniper Tester","session":"Wren Visitor","style":"say","text":"ok Wren, see you"}
10:00:08 ~words {"kind":"im","from":"me","id":"{{Me}}","name":"Juniper Tester","session":"Unknown Faraway","style":"say","text":"hi there"}
10:00:09 ~words {"kind":"nearby","from":"objects","id":"55555555-5555-5555-5555-555555555555","name":"Tidewater Harp","style":"object","text":"Touch me to pick a song, Kestrel Tester."}
10:00:10 ~words {"kind":"nearby","from":"objects","id":"66666666-6666-6666-6666-666666666666","name":"Chat Relay","style":"object","text":"Kestrel Tester: platypus saffron teacup"}
10:00:11 ~words {"kind":"notice","from":"people","id":"{{Kestrel}}","name":"Kestrel Tester","notice":"TeleportOffered","text":"offers a teleport “come see the narwhal opera”"}
10:00:12 ~words {"kind":"notice","from":"app","id":"{{Oriole}}","name":"Oriole Quill","notice":"FriendOnlineOffline","text":"is online"}
10:00:12 ~words {"kind":"notice","from":"app","name":"Unlisted Stranger","notice":"InventoryAccepted","text":"took what you gave"}
10:00:12 ~words {"kind":"notice","from":"objects","name":"Tidewater Harp","notice":"ScriptDialog","text":"Pick a song."}
10:00:13 info: sojourn[0] voice: Oriole Quill is calling
10:00:14 info: sojourn[0] assistant: 10:00:14 replied (quick) to 'what did kestrel say': 'Kestrel said the zebra marmalade is hidden under the old lighthouse.'
10:00:15 ~heard {"from":"assistant","how":"arrival","via":"NVDA","text":"There's a lighthouse to the north, and Wren Visitor is dancing."}
10:00:16 ~heard {"from":"app","how":"answer","via":"NVDA","text":"Kestrel Tester is 4 metres away, two o'clock."}
10:00:17 ~heard {"from":"people","how":"answer","via":"NVDA","text":"Charter: the gilded ferret society meets nightly"}
10:00:18 ~heard {"from":"app","how":"again","via":"NVDA","text":"No older. quokka velvet"}
10:00:18 ~heard {"from":"me","how":"answer","via":"NVDA","text":"Draft to nearby: my own words stay. Enter sends, Escape cancels."}
10:00:19 info: sojourn[0] key sk-ant-abcdefghijklmnop1234 token sqr-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcd cap https://simhost-x.agni.secondlife.io:12043/cap/db1c5249-b09f-250a-b1f0-56c07d3ccef2 from 203.0.113.7, local 192.168.1.4, version 0.1.0.583, v6 2001:db8:85a3:0:0:8a2e:370:7334 and 2001:db8::7, at 10:00:19
10:00:20 ~words {"kind":"nearby","from":"objects","name":"#Firestorm LSL Bridge v2.29","style":"object","text":"<bridgeAuth>80751d6f-7d6f-e647-1f39-ac6e9eb9bb82</bridgeAuth>"}
10:00:21 ~mystery {"text":"pelican quartz"}
10:00:22 ~words {"text":"broken record, mongoose
10:00:23 warn: sojourn[0] something failed
   at Foo.Bar() in C:\Users\someperson\src\x.cs
17:04 IM Kestrel Tester: hippo lantern
10:00:24 info: sojourn[0] people: {{Kestrel}} seen, and Wren Visitor (wrenfeather), and wrenfeather.resident
10:00:25 ~words {"kind":"group","from":"people","id":"{{Kestrel}}","name":"Kestrel Tester","session":"Builders","style":"say","text":"axolotl umbrella"}
10:00:26 ~words {"kind":"nearby","from":"people","id":"{{Wren}}","name":"Wren Visitor","style":"emote","text":"waves at the iguana carnival","alert":true}
screen reader: a bare line naming Oriole
10:00:27 info: sojourn[0] voice: Voice Smith joined; Sojourn Fan waved
10:00:28 ~name {"id":"88888888-8888-8888-8888-888888888888","legacy":"greyling Resident","display":"Grey Lantern"}
10:00:28 ~name {"id":"99999999-9999-9999-9999-999999999999","legacy":"amberly Resident","display":"Amber"}
10:00:29 ~heard {"from":"assistant","how":"arrival","via":"NVDA","text":"They wear a fitted light grey top; Grey Lantern waves and Grey nods, and grey lantern bows."}
10:00:30 ~words {"kind":"nearby","from":"me","id":"{{Me}}","name":"Juniper Tester","style":"say","text":"hi Amber, the amber lamp is lit"}
10:00:31 ~words {"kind":"nearby","from":"people","id":"{{Kestrel}}","name":"Kestrel Tester","style":"say","text":"Wombat Operator HUD"}
10:00:32 ~words {"kind":"nearby","from":"objects","name":"Wombat Operator HUD v1.9","style":"object","text":"Your HUD is ready"}
10:00:33 ~heard {"from":"objects","how":"arrival","via":"NVDA","text":"Wombat Operator HUD v1.9: Your HUD is ready"}
10:00:34 ~heard {"from":"app","how":"answer","via":"NVDA","text":"Touched Wombat Operator HUD v1.9."}
10:00:35 ~name {"id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","legacy":"tamsinmoth Resident"}
10:00:36 ~words {"kind":"nearby","from":"objects","name":"Chat-Range","style":"object","text":"Ⅿoonbeam Tamarisk (tamsinmoth) is now in chat range."}
10:00:37 ~heard {"from":"objects","how":"arrival","via":"NVDA","text":"Chat-Range: Ⅿoonbeam Tamarisk (tamsinmoth) is now in chat range."}
10:00:38 ~heard {"from":"assistant","how":"arrival","via":"NVDA","text":"Ⅿoonbeam Tamarisk is by the bar."}
10:00:39 ~words {"kind":"nearby","from":"objects","name":"Kestrel Tester","style":"object","text":"the walrus trombone waits behind the mill"}
10:00:40 ~heard {"from":"objects","how":"arrival","via":"NVDA","text":"Kestrel Tester, IM: the walrus trombone waits behind the mill"}
10:00:41 ~name {"id":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","legacy":"dotsy Resident","display":"."}
10:00:42 ~heard {"from":"app","how":"answer","via":"NVDA","text":"Heading ENE.. or left, would like to:.."}
10:00:43 ~name {"id":"cccccccc-cccc-cccc-cccc-cccccccccccc","legacy":"starry Resident","display":"★★★"}
10:00:44 ~heard {"from":"app","how":"answer","via":"NVDA","text":"★★★ is here"}
10:00:45 ~name {"id":"dddddddd-dddd-dddd-dddd-dddddddddddd","legacy":"wilfleck Resident","display":"𝓜𝓪𝓻𝓲𝓰𝓸𝓵𝓭 ✨","plain":"Marigold"}
10:00:46 ~heard {"from":"app","how":"answer","via":"NVDA","text":"Marigold is 3 metres away, ten o'clock."}
10:00:47 10:00:47 ~words {"kind":"nearby","from":"people","id":"{{Kestrel}}","name":"Kestrel Tester","style":"say","text":"capybara"}
info: sojourn[0] a line whose time stamp went to the record before it
10:00:48 10:00:48 10:00:48 ~heard {"from":"people","how":"arrival","via":"NVDA","text":"Kestrel Tester: capybara"}
10:00:49 info: sojourn[0] 10:00:49 ~words {"kind":"nearby","from":"people","id":"{{Kestrel}}","name":"Kestrel Tester","style":"say","text":"pangolin"}
10:00:50 ~words {"kind":"nearby","from":"people","id":"{{Kestrel}}","name":"Kestrel Tester","style":"say","text":"see 10:00:51 ~dingo {here"}
""";

int failed = 0, passed = 0;
void Check(string what, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
    if (ok) passed++; else failed++;
}

var res = LogShare.Filter(log, new DateTime(2026, 1, 2, 10, 0, 0));
Check("a log with the records is filtered", res is not null);
var text = res?.Text ?? "";
var lower = text.ToLowerInvariant();

// nothing of anyone else's words, names, keys, nor the person's own name, account folder, keys or address
foreach (var w in new[] { "zebra", "marmalade", "walrus", "trombone", "quokka", "velvet", "platypus", "saffron", "narwhal", "gilded", "ferret", "pelican",
             "mongoose", "hippo", "axolotl", "iguana", "kestrel", "wren", "birdie", "oriole", "quill", "unlisted", "stranger",
             "faraway", "juniper", "someperson", "moonbeam", "tamarisk", "tamsinmoth", "marigold", "wilfleck", "capybara", "pangolin", "dingo", "203.0.113.7", "2001:db8", "sk-ant-abc", "sqr-abc", "22222222-2222", "80751d6f", "db1c5249" })
    Check($"\"{w}\" isn't in it", !lower.Contains(w));

// what stays
foreach (var w in new[]
         {
             "me, nearby: hello person 1, where is it?", "me, IM to person 2: ok person 2, see you", "me, IM to someone: hi there",
             "object Tidewater Harp, nearby: Touch me to pick a song, person 1.", "notice (ScriptDialog) from Tidewater Harp: Pick a song.",
             "notice (FriendOnlineOffline): person 3 is online", "notice (InventoryAccepted): someone took what you gave",
             "person 1, nearby: (words left out)", "person 2, IM: (words left out)", "person 1, in the group chat Builders: (words left out)",
             "person 2, nearby (emote) (an alert): (words left out)", "notice (TeleportOffered) from person 1: (words left out)",
             "voice: person 3 is calling", ", assistant: There's a lighthouse to the north, and person 2 is dancing.",
             ": person 1 is 4 metres away, two o'clock.", ": Draft to nearby: my own words stay.",
             "agent API on http://127.0.0.1:9725", "local 192.168.1.4", "version 0.1.0.583, v6 (an address) and (an address), at 10:00:19", "(a capability URL)", "(a key)", "C:\\Users\\(you)",
             "logged in as me (me)", "people: person 1 seen, and person 2 (person 2), and person 2",
             "<bridgeAuth>(left out)</bridgeAuth>", "screen reader: a bare line naming person 3",
             // a name made of everyday or the log's own words: replaced whole, its parts left alone
             "info: sojourn[0] voice: person 4 joined; person 4 waved",
             // a common word in a name (Grey Lantern, Amber): the word itself stays, the name capitalised doesn't, and
             // a name of more words in any case
             ", assistant: They wear a fitted light grey top; person 5 waves and person 5 nods, and person 5 bows.",
             "me, nearby: hi person 6, the amber lamp is lit",
         })
    Check($"keeps \"{w}\"", text.Contains(w));

Check("an object named as a person: their words, left out", text.Contains("object person 1, nearby: (words left out)"));
Check("an object relaying someone is left out", text.Contains("object Chat Relay, nearby: (words left out: they repeat someone's)"));
Check("the assistant's line repeating someone is left out", text.Contains("10:00:14 (a line repeating someone's words, left out)"));
Check("a read-back repeating someone's short line is left out", text.Contains("10:00:18 read out (again, NVDA): (words left out"));
// someone naming an object isn't someone's words in every line that names it
Check("an object's line read out is kept though someone said its name", text.Contains("read out (arrival, NVDA), objects: Wombat Operator HUD v1.9: Your HUD is ready"));
Check("and a touch of it", text.Contains("read out (answer, NVDA): Touched Wombat Operator HUD v1.9."));
Check("while what they said is still left out", text.Contains("10:00:31 person 1, nearby: (words left out)"));
Check("a record of an unknown kind is left out", text.Contains("10:00:21 (a record of a kind not known here, left out)"));
Check("a record that doesn't parse is left out", text.Contains("10:00:22 (a record that couldn't be read, left out)"));
Check("an entry with old-style chat in it is left out", text.Contains("10:00:23 (lines of an older kind, left out)"));
Check("no ~name records sent", !text.Contains("~name") && !text.Contains("~words") && !text.Contains("~heard"));
Check("a display name never looked up, written before a known name in brackets, is replaced too", text.Contains("Chat-Range: person 7 (person 7) is now in chat range.") && text.Contains("person 7 is by the bar."));
Check("a display name of punctuation is no stand-in: full stops stay", text.Contains("Heading ENE.. or left, would like to:.."));
Check("one of symbols is still a name", text.Contains("person 8 is here"));
Check("a display name in decorative letters, as it's said in plain ones, is replaced", text.Contains("person 9 is 3 metres away, ten o'clock."));
Check("nine people counted", res?.People == 9);
// the console logger writes a line in pieces, and a record from another thread landing between them took its time stamp
Check("a record behind another line's time stamp is still a record", text.Contains("10:00:47 person 1, nearby: (words left out)")
    && text.Contains("10:00:48 read out (arrival, NVDA): (someone else's words, left out)"));
Check("and the line that lost its stamp is kept", text.Contains("a line whose time stamp went to the record before it"));
Check("a record after another line's level is still a record", text.Contains("10:00:49 person 1, nearby: (words left out)"));

Check("a log from before the records isn't filtered", LogShare.Filter("10:00:00 info: sojourn[0] hello\n17:04 Someone: words\n") is null);
Check("nor one whose header names an older format", LogShare.Filter("10:00:00 ~log {\"v\":1}\n10:00:01 info: x\n") is null);

if (failed > 0 || Environment.GetEnvironmentVariable("LOGCHECK_SHOW") == "1") Console.WriteLine("\n" + text);
Console.WriteLine($"\n{passed} of {passed + failed} passed");
return failed;
