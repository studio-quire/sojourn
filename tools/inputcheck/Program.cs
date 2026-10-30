// Sojourn Access's judge of real input, checked without a person (tools/inputcheck/inputcheck.csproj says how to run
// it): simulated sequences of key and mouse messages, hook presses and focus changes fed to InputJudge, checking that
// the person's real use passes (a screen reader's passed-on Enter in an edit field, a number key on a notice, a held key
// walking, the focus moved by our own code) and that what a program could do is refused; then RealInput itself in a
// window of its own, with a UI Automation client reading it from another thread as a screen reader does. Claude Opus
// 5.5, 2026-10-29.
using System.Diagnostics;
using System.Runtime.InteropServices;
using Sojourn.Access;

// RealInput's checks below post keys to a window of our own and judge them as the hand's (read once, at its first use)
Environment.SetEnvironmentVariable("SOJOURN_TEST_INPUT", "keys");
int fails = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "ok   " : "FAIL ") + what); if (!ok) fails++; }
const int Enter = 0x0D, One = 0x31, Two = 0x32, Shift = 0x10, LShift = 0xA0, Delete = 0x2E, Space = 0x20, Down = 0x28;
const long Settle = InputJudge.Settle, Match = InputJudge.Match;
nint W = 0x1000, Other = 0x2000;   // the app's window, and another of its windows
if (args is ["-wpf", var wpfOut])
{
    Console.SetOut(new System.IO.StreamWriter(wpfOut) { AutoFlush = true });
    if (!OnHiddenDesktop()) { Console.WriteLine("FAIL -wpf brings a window to the front: only on a hidden desktop"); return 1; }
    LauncherInWpf();
    Console.WriteLine(fails == 0 ? "all passed" : $"{fails} failed");
    return fails;
}
InputJudge j;
Standing s, s2;

// ---- real use: a screen reader's passed-on keys in edit fields (the 2026-10-29 guided test's llTextBox reply) ----
j = new InputJudge();
j.Pressed(Enter, 10_000, W);
s = j.KeyDown(Enter, InputSource.Injected, 10_130, 10_130, false, W);
Check(s.Person && s.Text, "Enter in an edit field, taken by NVDA and sent on 130 ms later: the person's, and it sends a typed answer");
Check(!s.Confirms, "but it doesn't answer a confirmation (those take a hardware key)");
Check(s.PressedAt == 10_000, "its press time is the hand's press, not the re-send");
Check(j.KeyUp(Enter, InputSource.Injected, 10_131).Person, "and its release, sent with it, is the person's");
Check(!j.KeyDown(Enter, InputSource.Injected, 10_200, 10_200, false, W).Person, "a second injected Enter on the same press isn't");
j.Pressed(Delete, 11_000, W); j.Pressed(Delete, 11_030, W);
s = j.KeyDown(Delete, InputSource.Injected, 11_100, 11_100, false, W);
s2 = j.KeyDown(Delete, InputSource.Injected, 11_110, 11_110, true, W);
Check(s.Person && s2.Person, "a held Delete (two hook presses, two re-sends): both the person's, editing the box");
Check(s.Text && !s2.Text && s2.Why == Refusal.Held, "only the first press sends a typed answer; the held repeat doesn't");
j.Pressed(LShift, 11_500, W);
Check(j.KeyDown(Shift, InputSource.Injected, 11_510, 11_510, false, W).Person, "the hook's left Shift is the window's Shift");

// ---- real use: a number key on a notice, Enter on a confirmation, Space on a button, a click ----
j = new InputJudge();
s = j.KeyDown(One, InputSource.Hand, 20_000, 19_995, false, W);
Check(s.Confirms && s.PressedAt == 19_995, "a hardware 1 on a notice answers it, pressed when Windows says");
j.Changed(Cause.Person, 20_500);   // the person's Down arrow moved the selection
s = j.KeyDown(Enter, InputSource.Hand, 20_560, 20_550, false, W);
Check(s.Confirms, "Enter 50 ms after the person's own arrow moved the selection: answers (type-ahead is the person's)");
j.KeyDown(Space, InputSource.Hand, 21_000, 21_000, false, W);
s = j.KeyUp(Space, InputSource.Hand, 21_090);
Check(s.Confirms && s.PressedAt == 21_000, "Space's release (where a button acts) answers, as of its press");
j.Mouse(true, InputSource.Hand, 22_000);
s = j.Mouse(false, InputSource.Hand, 22_080);
Check(s.Confirms && s.PressedAt == 22_000, "a hardware click answers on its release, as of its press");

// ---- real use: a held key ----
j = new InputJudge();
s = j.KeyDown(Down, InputSource.Hand, 30_000, 30_000, false, W);
s2 = j.KeyDown(Down, InputSource.Hand, 30_530, 30_530, true, W);
Check(s.Person && s2.Person, "a held arrow: its repeats are still the person's (walking, scrolling)");
s = j.KeyDown(Enter, InputSource.Hand, 31_000, 31_000, true, W);
Check(!s.Confirms && s.Why == Refusal.Held, "Enter held from before (only its repeats arrive): doesn't answer");
j.KeyDown(Two, InputSource.Hand, 32_000, 32_000, false, W);
s = j.KeyDown(Two, InputSource.Hand, 32_600, 32_600, true, W);
Check(!s.Confirms && s.PressedAt == 32_000, "a held 2's repeat doesn't answer, and keeps its first press time");
Check(!j.KeyUp(Space, InputSource.Hand, 33_000).Confirms, "Space let go with no press seen here (held from before): doesn't press a button");
Check(!j.Mouse(false, InputSource.Hand, 33_100).Confirms, "a mouse release with no press seen here: doesn't answer");

// ---- real use: the focus moved by our own code for the person ----
j = new InputJudge();
j.Changed(Cause.Ours, 40_000);   // the focus put back on the notices after an answer, or onto a confirmation they asked for
Check(j.KeyDown(Enter, InputSource.Hand, 40_120, 40_100, false, W).Confirms, "Enter pressed 100 ms after our own move: answers");
j.Changed(Cause.Ours, 41_000);
s = j.KeyDown(One, InputSource.Hand, 41_010, 40_990, false, W);
Check(!s.Confirms && s.Why == Refusal.FocusMoved, "a key pressed before our own move but handled after: doesn't act on the new place");

// ---- what a program could do: move the focus, select another notice, bring the window to the front ----
j = new InputJudge();
j.Changed(Cause.Foreign, 50_000);
s = j.KeyDown(Enter, InputSource.Hand, 50_210, 50_200, false, W);
Check(!s.Confirms && s.Why == Refusal.FocusMoved, "the focus moved by something else 200 ms before a hardware Enter: refused");
Check(j.KeyDown(Enter, InputSource.Hand, 50_000 + Settle + 20, 50_000 + Settle + 10, false, W).Confirms, $"pressed again {Settle} ms after it: answers");
j.Changed(Cause.Foreign, 51_000);
Check(!j.KeyDown(Enter, InputSource.Hand, 51_005, 50_990, false, W).Confirms, "pressed just before the change, handled just after: refused");
j.Changed(Cause.Foreign, 52_000);
j.Mouse(true, InputSource.Hand, 52_010);   // a click that brought the window to the front
Check(!j.Mouse(false, InputSource.Hand, 52_090).Confirms, "a click right after the window came to the front: refused");
j = new InputJudge();
j.Changed(Cause.Foreign, 53_000);   // the app came to the front while the person held Enter
j.Pressed(Enter, 53_040, W); j.Pressed(Enter, 53_070, W); j.Pressed(Enter, 53_900, W);
Check(!j.KeyDown(Enter, InputSource.Injected, 53_050, 53_050, false, W).Text, "a held key's first press seen by the hook after activation: no typed answer");
Check(!j.KeyDown(Enter, InputSource.Injected, 53_080, 53_080, false, W).Text, "its next repeat: none");
Check(!j.KeyDown(Enter, InputSource.Injected, 53_910, 53_910, false, W).Text, "nor one well after it (the hook knows it's held)");
j.Released(Enter); j.Pressed(Enter, 54_500, W);
Check(j.KeyDown(Enter, InputSource.Injected, 54_600, 54_600, false, W).Text, "let go and pressed again: the typed answer goes");

// ---- what a program could do: post, inject, ride on a press ----
j = new InputJudge();
Check(!j.KeyDown(Enter, InputSource.Injected, 60_000, 60_000, false, W).Person, "an injected Enter, no press behind it: not the person's");
Check(!j.KeyUp(Enter, InputSource.Injected, 60_001).Person, "nor its release");
j.Pressed(Enter, 61_000, W);
Check(!j.KeyDown(Enter, InputSource.Posted, 61_010, 61_010, false, W).Person, "a posted Enter right after the hand's press: not the person's (posted keys go to any window)");
Check(!j.KeyDown(Enter, InputSource.Injected, 61_020, 61_020, false, Other).Person, "an injected Enter into another window than the one in front at the press: not the person's");
Check(j.KeyDown(Enter, InputSource.Hand, 61_030, 61_000, false, W).Confirms, "the hand's own Enter, not taken by anything: answers");
Check(!j.KeyDown(Enter, InputSource.Injected, 61_040, 61_040, false, W).Person, "an injected Enter riding on that press after it arrived itself: not the person's");
j.Pressed(One, 62_000, W);
Check(!j.KeyDown(Enter, InputSource.Injected, 62_010, 62_010, false, W).Person, "an injected Enter after the hand pressed 1: not the person's (another key)");
j.Pressed(Enter, 63_000, W);
Check(!j.KeyDown(Enter, InputSource.Injected, 63_000 + Match + 1, 63_000 + Match + 1, false, W).Person, $"the press more than {Match} ms ago: not the person's");
j.Pressed(Enter, 64_000, W); j.HookOff();
Check(!j.KeyDown(Enter, InputSource.Injected, 64_050, 64_050, false, W).Person, "the hook taken off (the app left the front): its presses go with it");
Check(!j.KeyUp(Enter, InputSource.Posted, 64_100).Person, "a posted release: not the person's");
Check(!j.Mouse(true, InputSource.Injected, 64_200).Person && !j.Mouse(false, InputSource.Posted, 64_210).Person, "injected and posted clicks: not the person's");

// ---- characters: what the person's key makes is theirs, nothing else; never an answer (mnemonics act on them) ----
j = new InputJudge();
j.KeyDown(0x41, InputSource.Hand, 70_000, 70_000, false, W);
s = j.Char('a', InputSource.Hand);
Check(s.Person && !s.Confirms && !s.Text, "the character of the person's key: theirs, but it never answers");
Check(!j.Char('b', InputSource.Posted).Person, "a second character for the same key (a program's): not the person's");
j.KeyDown(0x43, InputSource.Hand, 70_100, 70_100, false, W);
j.Other();   // a posted callback between the key and its character
Check(j.Char('c', InputSource.Hand).Person, "a callback between the key and its character doesn't cut them apart");
j.KeyUp(0x43, InputSource.Hand, 70_150);
Check(!j.Char('c', InputSource.Posted).Person, "a character after the key's release (a WM_SYSCHAR posted for Alt+C): not the person's");
Check(!j.Char('x', InputSource.Posted).Person, "a posted character with no key before it: not the person's");
j.KeyDown(0x44, InputSource.Injected, 70_200, 70_200, false, W);
Check(!j.Char('d', InputSource.Injected).Person, "the character of an injected key with no press behind it: not the person's");
j.KeyDown(0x45, InputSource.Hand, 70_300, 70_300, false, W);
Check(j.Char('\uD83D', InputSource.Hand).Person && j.Char('\uDE00', InputSource.Hand).Person, "a surrogate pair from one key: both halves the person's");

// ---- the 2026-10-29 NVDA run's two flows, as the judge sees them ----
j = new InputJudge();
j.KeyDown(0x11, InputSource.Hand, 80_000, 80_000, false, W);   // Ctrl
s = j.KeyDown(0x4E, InputSource.Hand, 80_100, 80_100, false, W);   // N: NewestNotice moves the focus and selects, during the press
j.Changed(Cause.Person, 80_110); j.Changed(Cause.Person, 80_112);
Check(s.Person, "Ctrl+N: the person's own press");
j.KeyUp(0x4E, InputSource.Hand, 80_200); j.KeyUp(0x11, InputSource.Hand, 80_220);
Check(j.KeyDown(0x33, InputSource.Hand, 81_500, 81_500, false, W).Confirms, "Ctrl+N, then 3 1.4 s later: answers the notice");
j = new InputJudge();
j.Changed(Cause.Foreign, 82_000);   // the notice arrived and was selected
j.KeyDown(0x4E, InputSource.Hand, 82_200, 82_200, false, W);
j.Changed(Cause.Person, 82_210);
s = j.KeyDown(0x33, InputSource.Hand, 82_400, 82_400, false, W);
Check(!s.Confirms && s.Why == Refusal.FocusMoved, "but 3 within 0.7 s of the notice's arrival is refused, Ctrl+N between or not");
j = new InputJudge();
j.KeyDown(Delete, InputSource.Hand, 90_000, 90_000, false, W);   // Delete in the inventory: the confirmation comes up
j.KeyUp(Delete, InputSource.Hand, 90_090);
j.Changed(Cause.Ours, 90_150); j.Changed(Cause.Ours, 90_152);   // ConfirmedAsync: the focus onto it, it selected
s = j.KeyDown(0x1B, InputSource.Hand, 96_800, 96_800, false, W);
Check(s.Person && s.Confirms, "a confirmation focused by ConfirmedAsync, Escape 6.8 s later: the person's, cancels");
Check(j.KeyDown(Enter, InputSource.Hand, 90_400, 90_400, false, W).Confirms, "and Enter 250 ms after it came up: confirms");

// ---- what Windows says of a posted key, in a window of our own (no focus taken, nothing global) ----
var sink = new Sink();
PostMessage(sink.Handle, 0x0100, Enter, 0);
var until = Stopwatch.StartNew();
while (sink.Seen == null && until.ElapsedMilliseconds < 2000)
{
    while (PeekMessage(out var msg, 0, 0, 0, 1)) DispatchMessage(ref msg);
    Thread.Sleep(5);
}
Check(sink.Seen == InputSource.Posted, $"Windows: a posted WM_KEYDOWN comes from no device (judged {sink.Seen?.ToString() ?? "nothing"})");
sink.DestroyHandle();

// ---- why the ToS's keys have no mnemonics: WinForms presses one on a character message a program posts ----
// (in a window of our own, off-screen, never activated, not in the taskbar or Alt+Tab)
foreach (var (msg, name) in new[] { (0x0106, "WM_SYSCHAR"), (0x0102, "WM_CHAR") })
{
    using var f = new Quiet();
    var b = new Button { Text = "&Continue" };
    var other = new CheckBox { Text = "x" };
    f.Controls.Add(other); f.Controls.Add(b);
    bool clicked = false;
    b.Click += (_, _) => clicked = true;
    f.Show();
    f.ActiveControl = other;
    PostMessage(other.Handle, msg, 'c', 0);
    var t = Stopwatch.StartNew();
    while (!clicked && t.ElapsedMilliseconds < 1000) { Application.DoEvents(); Thread.Sleep(5); }
    Check(clicked, $"WinForms: a posted {name} 'c' presses a &Continue button beside the focus (so mnemonics go, and characters never answer)");
    f.Close();
}

// ---- RealInput itself, as the app has it, in a window of our own (off-screen, never activated), posted keys judged as
// the hand's (SOJOURN_TEST_INPUT=keys), a UI Automation client on another thread reading it as a screen reader does
// after each focus or selection change (the 2026-10-29 NVDA run: UIA's connections to our windows, sent from another
// thread, never came back to the hook that tracked sends, so for 10 s the app took another thread's message to be in
// hand and refused the next answering key) ----
{
    Application.AddMessageFilter(RealInput.Filter);
    RealInput.Start();   // the hook thread stays off: these windows never come to the front
    using var f = new Quiet { Size = new Size(300, 200) };
    var say = new TextBox();
    var list = new ListView { View = View.Details, HideSelection = false, MultiSelect = false, Top = 30, Height = 100 };
    list.Columns.Add("notice");
    list.Items.Add("Access Menu: pick one. Keys: 1 Red, 2 Green, 3 Blue.");
    list.Items.Add("Confirm: Move to Trash. Enter confirms, Escape cancels.");
    f.Controls.Add(say); f.Controls.Add(list);
    f.Show();
    (bool Person, bool Confirms, string Why)? got = null;
    bool? textByPerson = null;
    list.SelectedIndexChanged += (_, _) => RealInput.Changed();
    say.TextChanged += (_, _) => textByPerson = RealInput.Person;
    say.KeyDown += (_, e) =>
    {
        if (e.KeyCode == Keys.N) Select(0);   // Ctrl+N: NewestNotice, during the person's press
        else if (e.KeyCode == Keys.Delete) _ = Confirming();
    };
    list.KeyDown += (_, e) => got = (RealInput.Person, RealInput.Confirms, RealInput.Why);
    f.OnApp = () => Select(1);   // something another thread sent moves the selection

    void Select(int i) { list.Focus(); list.Items[i].Selected = true; list.Items[i].Focused = true; }
    async Task Confirming() { await Task.Delay(80); RealInput.Ours(() => Select(1)); }   // ConfirmedAsync, once the gate raised it
    void Pump(int ms) { var t = Stopwatch.StartNew(); do { Application.DoEvents(); Thread.Sleep(2); } while (t.ElapsedMilliseconds < ms); }
    void While(Task t) { while (!t.IsCompleted) { Application.DoEvents(); Thread.Sleep(1); } t.GetAwaiter().GetResult(); }
    void Key(Control c, Keys k) { PostMessage(c.Handle, 0x0100, (int)k, 1); PostMessage(c.Handle, 0x0101, (int)k, unchecked((nint)0xC0000001)); }
    (bool Person, bool Confirms, string Why)? Press(Keys k) { got = null; Key(list, k); Pump(60); return got; }
    static string Said((bool Person, bool Confirms, string Why)? g) => g == null ? "no key seen" : g.Value.Confirms ? "answers" : g.Value.Person ? $"the person's; {g.Value.Why}" : g.Value.Why;
    int read = 0;
    void ScreenReader()
    {
        var h = list.Handle;
        While(Task.Run(() =>
        {
            var e = System.Windows.Automation.AutomationElement.FromHandle(h);
            if (e.Current.Name != null) read++;
            foreach (System.Windows.Automation.AutomationElement item in e.FindAll(System.Windows.Automation.TreeScope.Children, System.Windows.Automation.Condition.TrueCondition))
            {
                _ = item.Current.HasKeyboardFocus;
                if (item.Current.Name != null) read++;
            }
        }));
    }

    say.Focus();
    Pump(100);
    Key(say, Keys.N); Pump(100);
    ScreenReader(); Pump(300);
    var g = Press(Keys.D3);
    Check(g is { Confirms: true }, $"RealInput: Ctrl+N, a screen reader reading the notice, then 3: answers ({Said(g)})");
    Check(read > 1, $"(the screen reader read {read} things through UI Automation)");

    say.Focus(); Pump(100);
    Key(say, Keys.Delete); Pump(250);
    ScreenReader(); Pump(200);
    g = Press(Keys.Escape);
    Check(g is { Person: true }, $"RealInput: a confirmation focused by our own code, read by the screen reader, then Escape: the person's, cancels ({Said(g)})");
    g = Press(Keys.Enter);
    Check(g is { Confirms: true }, $"and Enter: confirms ({Said(g)})");

    // the protection stays: what moves under a key without the person or our code acting for them
    f.BeginInvoke(() => Select(0)); Pump(30);   // a callback (a notice arriving, a timer)
    g = Press(Keys.D3);
    Check(g is { Confirms: false, Why: "That changed just as you pressed. Press again if you meant it." }, $"RealInput: the selection moved by a callback just before 3: refused ({Said(g)})");
    Pump((int)Settle + 50);
    ScreenReader();
    g = Press(Keys.D3);
    Check(g is { Confirms: true }, $"and 3 pressed again {Settle} ms after it: answers ({Said(g)})");
    Key(say, Keys.Right); Pump(20);   // the person's key, then another thread moves the selection within the second after it
    While(Task.Run(() => SendMessage(f.Handle, 0x8001, 0, 0)));
    g = Press(Keys.D3);
    Check(g is { Confirms: false }, $"RealInput: the selection moved by a message another thread sent, just after the person's key: 3 refused ({Said(g)})");
    Key(say, Keys.Right); Pump(20);
    textByPerson = null;
    While(Task.Run(() => SendText(say.Handle, 0x000C, 0, "set by a program")));   // WM_SETTEXT
    Check(textByPerson == false, "RealInput: text another thread set in a box, just after the person's key: not the person's");
    f.Close();
}

// ---- the launcher's judge (src/Sojourn.Launcher/PressJudge.cs): the hooks' presses against the window's messages,
// where WPF's real keys say unavailable (the 2026-10-30 guided test) ----
{
    var LH = Sojourn.Launcher.InputSource.Hardware;
    var LI = Sojourn.Launcher.InputSource.Injected;
    var LU = Sojourn.Launcher.InputSource.Unavailable;
    const int V = 0x56, A = 0x41, LCtrl = 0xA2, Ctrl = 0x11;
    var k = new Sojourn.Launcher.PressJudge();
    k.Key(Enter, true, 1000, W);
    Check(k.KeyMessage(Enter, true, LU, 1010, W), "Launcher: Enter the hook saw, its message unavailable (as WPF's real keys): counts");
    Check(!k.KeyMessage(Enter, true, LU, 1020, W), "Launcher: a second message on the same press (a posted copy): refused");
    k.Key(Enter, true, 2000, W);
    Check(!k.KeyMessage(Enter, true, LU, 3100, W), "Launcher: a message more than a second after the press: refused");
    k.Key(Enter, true, 4000, W);
    Check(!k.KeyMessage(Enter, true, LU, 4010, Other), "Launcher: into another window than the one in front at the press: refused");
    Check(!k.KeyMessage(Two, true, LU, 4020, W), "Launcher: another key than the one pressed: refused");
    Check(k.KeyMessage(Enter, true, LI, 4030, W), "Launcher: injected on the hand's press (a screen reader's pass-on): counts");
    Check(!k.KeyMessage(Enter, true, LI, 5000, W), "Launcher: injected with no press (SendInput): refused");
    Check(!k.KeyMessage(Enter, true, LU, 5010, W), "Launcher: posted with no press: refused");
    Check(k.KeyMessage(Enter, true, LH, 6000, W), "Launcher: hardware with no hook press (the hook not on yet): counts");
    k.Key(Enter, true, 7000, W);
    Check(k.KeyMessage(Enter, true, LH, 7005, W) && !k.KeyMessage(Enter, true, LU, 7010, W), "Launcher: a hardware message takes its press, so a posted copy after it is refused");
    k.Key(Space, false, 8000, W);
    Check(!k.KeyMessage(Space, true, LU, 8001, W), "Launcher: a release the hook saw doesn't let a key-down count");
    Check(k.KeyMessage(Space, false, LU, 8002, W), "Launcher: but its key-up message counts (Space on a button)");
    k.Key(LCtrl, true, 9000, W); k.Key(V, true, 9100, W);
    Check(k.KeyMessage(Ctrl, true, LU, 9001, W) && k.KeyMessage(V, true, LU, 9101, W), "Launcher: Ctrl+V, the hook's left Ctrl as the window's Ctrl: both count");
    k.Key(A, true, 10000, W);
    k.KeyMessage(A, true, LU, 10001, W);
    Check(k.CharMessage('a', LU) && !k.CharMessage('b', LU), "Launcher: a counted key-down's character counts, a second character doesn't");
    k.Key(A, true, 10100, W);
    k.KeyMessage(A, true, LU, 10101, W);
    Check(k.CharMessage((char)0xD83D, LU) && k.CharMessage((char)0xDE00, LU) && !k.CharMessage('x', LU), "Launcher: a surrogate pair is one key's character");
    k.KeyMessage(A, true, LU, 10200, W);
    Check(!k.CharMessage('a', LU), "Launcher: the character of a refused key-down: refused");
    Check(k.CharMessage('a', LH), "Launcher: a character Windows calls hardware: counts");
    k.Button(true, 100, 200, 11000); k.Button(false, 100, 200, 11080);
    Check(k.ButtonMessage(true, 101, 199, LU, 11001) && k.ButtonMessage(false, 100, 200, LU, 11081), "Launcher: a click the hook saw: its down and up count at its point");
    k.Button(false, 100, 200, 12000);
    Check(!k.ButtonMessage(false, 300, 200, LU, 12001), "Launcher: a posted release elsewhere (on Continue) just after the hand's: refused");
    Check(!k.ButtonMessage(true, 100, 200, LU, 12002), "Launcher: a button-down on a release's press: refused");
    Check(k.ButtonMessage(false, 100, 200, LU, 12003), "Launcher: and the hand's own release still counts");
    Check(!k.ButtonMessage(false, 100, 200, LI, 13000), "Launcher: an injected click with no press: refused");
    k.Key(Enter, true, 14000, W); k.Button(false, 1, 1, 14000);
    k.Forget(keys: true, buttons: false);
    Check(!k.KeyMessage(Enter, true, LU, 14001, W) && k.ButtonMessage(false, 1, 1, LU, 14001), "Launcher: the keyboard hook off: its presses go, the mouse's stay");
}

// ---- -wpf OUT: the launcher's RealPress as WPF handles input, in a window of our own brought to the front, so only
// on a hidden desktop (tools/launcher.py check runs it there through QuireUia); no app, so no hooks: presses are given
// to its judge here as the hooks would ----
void LauncherInWpf()
{
    var seen = new System.Collections.Concurrent.BlockingCollection<(string What, bool Counts)>();
    nint hwnd = 0;
    using var shown = new ManualResetEventSlim();
    var ui = new Thread(() =>
    {
        var box = new System.Windows.Controls.TextBox();
        var win = new System.Windows.Window
        {
            Left = 100, Top = 100, Width = 200, Height = 100, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = System.Windows.WindowStyle.None, Background = System.Windows.Media.Brushes.White, Content = box,
        };
        win.PreviewKeyDown += (_, e) =>
        {
            seen.Add(("key", Sojourn.Launcher.RealPress.Now()));
            if (e.Key == System.Windows.Input.Key.F6) win.Dispatcher.BeginInvoke(() => seen.Add(("later", Sojourn.Launcher.RealPress.Now())));
        };
        box.TextChanged += (_, _) => seen.Add(("text", Sojourn.Launcher.RealPress.Now()));
        Sojourn.Launcher.RealPress.Start(win);
        win.Show();
        hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
        // WPF raises keys only in the focused window and clicks only where it's on screen: this desktop is hidden
        if (!win.Activate()) SwitchToThisWindow(hwnd, true);
        box.Focus();
        shown.Set();
        System.Windows.Threading.Dispatcher.Run();
    });
    ui.SetApartmentState(ApartmentState.STA);
    ui.IsBackground = true;
    ui.Start();
    shown.Wait(10_000);
    Thread.Sleep(300);
    var judge = Sojourn.Launcher.RealPress.Judge;
    string Next() => seen.TryTake(out var x, 1500) ? $"{x.What} {(x.Counts ? "counts" : "refused")}" : "nothing";
    string n, n2;
    PostMessage(hwnd, 0x0100, 0x74, 1);
    Check((n = Next()) == "key refused", $"Launcher in WPF: a posted F5 the hook didn't see: refused ({n})");
    judge.Key(0x74, true, Environment.TickCount64, hwnd);
    PostMessage(hwnd, 0x0100, 0x74, 1);
    Check((n = Next()) == "key counts", $"Launcher in WPF: F5 the hook saw: counts while WPF handles it ({n})");
    PostMessage(hwnd, 0x0100, 0x74, 1);
    Check((n = Next()) == "key refused", $"Launcher in WPF: F5 again on the same press: refused ({n})");
    judge.Key(0x75, true, Environment.TickCount64, hwnd);
    PostMessage(hwnd, 0x0100, 0x75, 1);
    n = Next(); n2 = Next();
    Check(n == "key counts" && n2 == "later refused", $"Launcher in WPF: F6 counts, work it leaves for later doesn't ({n}, {n2})");
    judge.Key(0x41, true, Environment.TickCount64, hwnd);
    PostMessage(hwnd, 0x0100, 0x41, 1);   // its WM_CHAR made by TranslateMessage, as for the hand's key
    var typed = $"{Next()}, {Next()}";
    PostMessage(hwnd, 0x0102, 'b', 1);
    typed += $", {Next()}";
    Check(typed == "key counts, text counts, text refused", $"Launcher in WPF: A's character counts, a character posted after it doesn't ({typed})");
    // (clicks aren't checked here: WPF takes a posted click only where the real cursor is, and this desktop's isn't
    // ours to move; PressJudge's checks above have the points, and clicks go through the same filter as keys)
    System.Windows.Threading.Dispatcher.FromThread(ui)?.InvokeShutdown();
}

if (args.Contains("-raw")) RawInput.Show();

Console.WriteLine(fails == 0 ? "all passed" : $"{fails} failed");
return fails;

[DllImport("user32.dll")] static extern bool PostMessage(nint hwnd, int msg, nint w, nint l);
[DllImport("user32.dll")] static extern nint SendMessage(nint hwnd, int msg, nint w, nint l);
[DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] static extern nint SendText(nint hwnd, int msg, nint w, string l);
[DllImport("user32.dll")] static extern bool PeekMessage(out WinMsg msg, nint hwnd, uint min, uint max, uint remove);
[DllImport("user32.dll")] static extern nint DispatchMessage(ref WinMsg msg);
[DllImport("user32.dll")] static extern void SwitchToThisWindow(nint hwnd, bool altTab);
[DllImport("user32.dll")] static extern nint GetThreadDesktop(uint thread);
[DllImport("user32.dll")] static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
[DllImport("user32.dll")] static extern bool CloseDesktop(nint desktop);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformation(nint obj, int index, char[] info, int length, out int needed);
[DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

// This thread's desktop isn't the one the person's input goes to (nor the usual one)
static bool OnHiddenDesktop()
{
    static string Name(nint d) { var b = new char[256]; return d != 0 && GetUserObjectInformation(d, 2, b, b.Length * 2, out _) ? new string(b).TrimEnd('\0') : ""; }
    var mine = Name(GetThreadDesktop(GetCurrentThreadId()));
    var input = OpenInputDesktop(0, false, 0x0001);   // DESKTOP_READOBJECTS
    var theirs = Name(input);
    if (input != 0) CloseDesktop(input);
    return mine.Length > 0 && !mine.Equals("Default", StringComparison.OrdinalIgnoreCase) && !mine.Equals(theirs, StringComparison.OrdinalIgnoreCase);
}

[StructLayout(LayoutKind.Sequential)] struct WinMsg { public nint H; public uint M; public nint W, L; public uint T; public int X, Y; }

sealed class Quiet : Form
{
    public Action? OnApp;   // WM_APP + 1
    protected override void WndProc(ref Message m) { if (m.Msg == 0x8001) OnApp?.Invoke(); base.WndProc(ref m); }
    public Quiet() { StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000); ShowInTaskbar = false; FormBorderStyle = FormBorderStyle.None; Size = new Size(10, 10); }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x80 | 0x08000000; return p; } }   // a tool window, no activation
}

sealed class Sink : NativeWindow
{
    public InputSource? Seen;
    public Sink() => CreateHandle(new CreateParams());
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0100)
            Seen = !GetCurrentInputMessageSource(out var s) || s.DeviceType == 0 ? InputSource.Posted : s.OriginId switch { 1 => InputSource.Hand, 2 => InputSource.Injected, _ => InputSource.Posted };
        base.WndProc(ref m);
    }
    [StructLayout(LayoutKind.Sequential)] struct Src { public int DeviceType, OriginId; }
    [DllImport("user32.dll")] static extern bool GetCurrentInputMessageSource(out Src s);
}

/// <summary>Why the judge needs a hook of its own: a key a low-level hook swallows (as NVDA swallows the keys it takes)
/// never reaches raw input, so raw input can't tell the hand's press from the screen reader's re-send.</summary>
static unsafe class RawInput
{
    const int F24 = 0x87;
    static readonly List<string> Log = new();
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static void L(string s) { lock (Log) Log.Add($"  {Clock.Elapsed.TotalMilliseconds,8:F1} ms  {s}"); }

    public static void Show()
    {
        Console.WriteLine("raw input and a swallowing hook (one injected F24, swallowed by our own hook):");
        using var hooked = new ManualResetEventSlim();
        uint thread = 0;
        HookProc proc = (code, w, l) =>
        {
            if (code == 0 && Marshal.ReadInt32(l, 0) == F24) { L($"hook: F24 {(w == 0x100 ? "down" : "up")}, swallowed"); return 1; }
            return CallNextHookEx(0, code, w, l);
        };
        var t = new Thread(() =>
        {
            thread = GetCurrentThreadId();
            var h = SetWindowsHookEx(13, proc, GetModuleHandle(null), 0);
            hooked.Set();
            while (GetMessage(out _, 0, 0, 0) > 0) { }
            UnhookWindowsHookEx(h);
        }) { IsBackground = true };
        t.Start();
        hooked.Wait();
        var sink = new RawSink();
        var rid = new Rid { UsagePage = 1, Usage = 6, Flags = 0x100, Target = sink.Handle };
        RegisterRawInputDevices(new[] { rid }, 1, (uint)sizeof(Rid));
        var inputs = new Input[] { new() { Type = 1, Vk = F24 }, new() { Type = 1, Vk = F24, Flags = 2 } };
        L("SendInput F24 down and up");
        SendInput(2, inputs, sizeof(Input));
        var until = Stopwatch.StartNew();
        while (until.ElapsedMilliseconds < 600) { Application.DoEvents(); Thread.Sleep(10); }
        PostThreadMessage(thread, 0x12, 0, 0);
        sink.DestroyHandle();
        GC.KeepAlive(proc);
        foreach (var line in Log) Console.WriteLine(line);
        Console.WriteLine(sink.Count == 0 ? "  raw input got nothing of it" : $"  raw input got {sink.Count} messages of it");
    }

    sealed class RawSink : NativeWindow
    {
        public int Count;
        public RawSink() => CreateHandle(new CreateParams());
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x00FF)
            {
                uint size = 0;
                GetRawInputData(m.LParam, 0x10000003, 0, ref size, 24);
                var buf = stackalloc byte[(int)size];
                GetRawInputData(m.LParam, 0x10000003, (nint)buf, ref size, 24);
                if (*(uint*)buf == 1 && *(ushort*)(buf + 24 + 6) == F24) { Count++; L("raw input: F24"); }
            }
            base.WndProc(ref m);
        }
    }

    delegate nint HookProc(int code, nint w, nint l);
    [StructLayout(LayoutKind.Sequential)] struct Rid { public ushort UsagePage, Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public ushort Vk; [FieldOffset(12)] public uint Flags; }
    [DllImport("user32.dll")] static extern nint SetWindowsHookEx(int id, HookProc p, nint mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(nint h);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint h, int c, nint w, nint l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandle(string? n);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern int GetMessage(out Msg m, nint h, uint a, uint b);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint t, uint m, nint w, nint l);
    [DllImport("user32.dll")] static extern bool RegisterRawInputDevices(Rid[] d, uint n, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(nint h, uint cmd, nint data, ref uint size, uint hdr);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, Input[] i, int size);
    [StructLayout(LayoutKind.Sequential)] struct Msg { public nint H; public uint M; public nint W, L; public uint T; public int X, Y; }
}
