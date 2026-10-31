// Copied from Radegast Legacy's contrib tools (projects/radegast/tools/QuireUia, written by Claude Opus 5.5); see Program.cs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using Accessibility;

static class Server
{
    public const string Help = @"commands (a SEL is key=value pairs joined by ';': pid, name (exact), name~ (contains), id (AutomationId),
type (Edit, Button, List, Document, Tab, TabItem, ...), class, n (0-based index among matches); pid defaults to the watched one):
  watch PID                 record events from this process only (0: all)
  mark TEXT                 put a marker line in the event log
  windows [PID]             top-level windows
  tree [PID] [DEPTH] [raw]  the UI Automation tree (control view unless raw)
  focus                     the focused element and its ancestors, and the foreground window
  find SEL                  matching elements
  get SEL                   one element in detail, with its full value or text
  set SEL | TEXT            ValuePattern.SetValue
  invoke SEL                InvokePattern.Invoke (toolbar buttons, menu items)
  click SEL                 BM_CLICK to a Win32 button (Invoke fails on them off the input desktop)
  select SEL                SelectionItemPattern.Select
  toggle SEL / expand SEL / collapse SEL
  setfocus SEL              AutomationElement.SetFocus (as a screen reader's focus move would)
  key SEL | VK[,VK...]      post WM_KEYDOWN/WM_KEYUP to the element's window (SEL ""focus"": the watched app's focused
                            window, where the person's keys would go; 13 Enter, 9 Tab, 27 Esc, 46 Delete;
                            ctrl+shift+78 holds those modifiers in its thread's key state meanwhile; alt+49
                            posts WM_SYSKEYDOWN with Alt held, as Alt+1 arrives)
  keydown SEL | VK / keyup SEL | VK   post only WM_KEYDOWN (as a key first pressed) or WM_KEYUP: a key held between
                            the two (walking by arrows)
  chars SEL | TEXT          post WM_CHAR for each character (SEL ""focus"" as for key)
  mouse SEL [| right]       post a left (or right) click at the element's centre to its window
  sel SEL                   an edit's text and selection, by WM_GETTEXT and EM_GETSEL
  shot [PID] FILE           PrintWindow of the main window to a PNG
  activate [PID]            make its main window the hidden desktop's foreground (needed after a restart)
  close [PID] | close SEL    WM_CLOSE to its main window, or to the window holding SEL
  quit                      stop the server";

    static StreamWriter log;
    static readonly object logLock = new object();
    static readonly object uiaLock = new object();
    static volatile int watchPid;
    static TcpListener listener;
    static volatile bool quitting;

    public static int Run(List<string> args)
    {
        int port = int.Parse(Program.Opt(args, "--port", "9885"));
        var logPath = Program.Opt(args, "--log", Path.Combine(Path.GetTempPath(), "quireuia-events.jsonl"));
        watchPid = int.Parse(Program.Opt(args, "--pid", "0"));
        WinEvents.Pid = watchPid;
        log = new StreamWriter(logPath, true, new UTF8Encoding(false)) { AutoFlush = true };
        Log("server", "start", "desktop events on port " + port);

        // Events by the COM client: the managed client's event registration fails on a hidden desktop.
        ComUia.Pid = () => watchPid;
        ComUia.Start();
        if (!ComUia.Notification || !ComUia.LiveRegion)
            new Thread(() =>
            {
                while (!quitting && (!ComUia.Notification || !ComUia.LiveRegion)) { Thread.Sleep(1000); ComUia.Retry(false); }
            }) { IsBackground = true, Name = "uia-retry" }.Start();
        new Thread(WinEvents.Pump) { IsBackground = true, Name = "winevents" }.Start();

        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        while (!quitting)
        {
            TcpClient c;
            try { c = listener.AcceptTcpClient(); } catch (SocketException) { break; }
            ThreadPool.QueueUserWorkItem(_ => Serve(c));
        }
        Automation.RemoveAllEventHandlers();
        return 0;
    }

    static void Try(string what, Action a)
    {
        try { a(); Log("server", "listening", what); }
        catch (Exception e) { Log("server", "unavailable", what + ": " + e.Message); }
    }

    static void Serve(TcpClient c)
    {
        using (c)
        {
            var s = c.GetStream();
            var r = new StreamReader(s, Encoding.UTF8);
            var line = r.ReadLine() ?? "";
            string reply;
            try
            {
                lock (uiaLock) reply = Command(line.Replace("\\n", "\n"));
            }
            catch (Exception e) { reply = "ERR " + e.GetType().Name + ": " + e.Message; }
            var b = Encoding.UTF8.GetBytes(reply.EndsWith("\n") ? reply : reply + "\n");
            s.Write(b, 0, b.Length);
        }
        if (quitting) listener.Stop();
    }

    // ---- events ----

    public static void Log(string source, string kind, string text, string extra = null)
    {
        var sb = new StringBuilder();
        sb.Append("{\"t\":").Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
          .Append(",\"src\":").Append(J(source)).Append(",\"kind\":").Append(J(kind)).Append(",\"text\":").Append(J(text));
        if (extra != null) sb.Append(',').Append(extra);
        sb.Append('}');
        lock (logLock) log.WriteLine(sb.ToString());
    }

    public static string J(string s) => s == null ? "null" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "").Replace("\t", "\\t") + "\"";

    static bool Watched(AutomationElement e)
    {
        if (watchPid == 0) return true;
        try { return e.Current.ProcessId == watchPid; } catch { return false; }
    }

    static void OnFocus(object src, AutomationFocusChangedEventArgs e)
    {
        var el = src as AutomationElement;
        if (el == null || !Watched(el)) return;
        Log("uia", "focus", Describe(el, false));
    }

    static void OnNotification(object src, AutomationEventArgs e)
    {
        var n = e as NotificationEventArgs;
        var el = src as AutomationElement;
        if (n == null || el == null || !Watched(el)) return;
        Log("uia", "notification", n.DisplayString, "\"notificationKind\":" + J(n.NotificationKind.ToString()) +
            ",\"processing\":" + J(n.NotificationProcessing.ToString()) + ",\"activity\":" + J(n.ActivityId) +
            ",\"element\":" + J(Describe(el, false)));
    }

    static void OnLiveRegion(object src, AutomationEventArgs e)
    {
        var el = src as AutomationElement;
        if (el == null || !Watched(el)) return;
        Log("uia", "liveregion", Describe(el, false));
    }

    // ---- commands ----

    static string Command(string line)
    {
        line = line.Trim();
        int sp = line.IndexOf(' ');
        var verb = sp < 0 ? line : line.Substring(0, sp);
        var arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();
        string selPart = arg, tail = null;
        int bar = arg.IndexOf(" | ");
        if (bar >= 0) { selPart = arg.Substring(0, bar).Trim(); tail = arg.Substring(bar + 3); }
        else if (arg.EndsWith(" |")) { selPart = arg.Substring(0, arg.Length - 2).Trim(); tail = ""; }

        switch (verb)
        {
            case "help": return Help;
            case "watch": watchPid = int.Parse(arg); WinEvents.Pid = watchPid; return "watching " + watchPid;
            case "mark": Log("mark", "mark", arg); return "ok";
            case "quit": quitting = true; return "bye";
            case "windows": return string.Join("\n", TopWindows(PidArg(arg)).Select(DescribeWindow));
            case "tree": return Tree(arg);
            case "focus": return Focus();
            case "find":
                {
                    var found = Find(selPart, all: true);
                    return found.Count == 0 ? "ERR none" : string.Join("\n", found.Select((e, i) => i + ": " + Describe(e, true)));
                }
            case "get": return Detail(One(selPart));
            case "set":
                {
                    var e = One(selPart);
                    ((ValuePattern)e.GetCurrentPattern(ValuePattern.Pattern)).SetValue(tail ?? "");
                    return "ok";
                }
            case "invoke": ((InvokePattern)One(selPart).GetCurrentPattern(InvokePattern.Pattern)).Invoke(); return "ok";
            // Win32 buttons' Invoke wants to take focus first, which fails off the input desktop; BM_CLICK doesn't.
            case "click": Native.PostMessage(Hwnd(One(selPart)), 0x00F5, IntPtr.Zero, IntPtr.Zero); return "ok";
            case "select": ((SelectionItemPattern)One(selPart).GetCurrentPattern(SelectionItemPattern.Pattern)).Select(); return "ok";
            case "toggle": ((TogglePattern)One(selPart).GetCurrentPattern(TogglePattern.Pattern)).Toggle(); return "ok";
            case "expand": ((ExpandCollapsePattern)One(selPart).GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand(); return "ok";
            case "collapse": ((ExpandCollapsePattern)One(selPart).GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse(); return "ok";
            case "setfocus": One(selPart).SetFocus(); return "ok";
            case "key":
                {
                    var h = Target(selPart);
                    foreach (var spec in (tail ?? "").Split(',').Select(v => v.Trim()))
                    {
                        // ctrl+shift+78: the modifiers go into the target thread's key state (shared through
                        // AttachThreadInput) while the posted key is handled, so GetKeyState sees them held
                        var bits = spec.Split('+');
                        int vk = int.Parse(bits.Last());
                        var mods = bits.Take(bits.Length - 1).Select(m => m.ToLowerInvariant()).ToList();
                        uint target = 0, me = 0;
                        byte[] saved = null;
                        if (mods.Count > 0)
                        {
                            target = Native.GetWindowThreadProcessId(h, out _);
                            me = Native.GetCurrentThreadId();
                            Native.AttachThreadInput(me, target, true);
                            saved = new byte[256];
                            Native.GetKeyboardState(saved);
                            var state = (byte[])saved.Clone();
                            foreach (var m in mods)
                            {
                                if (m == "ctrl") { state[0x11] = state[0xA2] = 0x80; }
                                else if (m == "shift") { state[0x10] = state[0xA0] = 0x80; }
                                else if (m == "alt") { state[0x12] = state[0xA4] = 0x80; }
                                else throw new ArgumentException("unknown modifier " + m);
                            }
                            Native.SetKeyboardState(state);
                        }
                        uint scan = Native.MapVirtualKey((uint)vk, 0);
                        // Extended keys (arrows, Insert/Delete, Home/End/PgUp/PgDn, Windows and Applications keys)
                        // carry bit 24; with Alt held a key arrives as WM_SYSKEYDOWN with the context bit (29)
                        uint lp = 1u | (scan << 16);
                        if ((vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E || (vk >= 0x5B && vk <= 0x5D)) lp |= 1u << 24;
                        bool alt = mods.Contains("alt");
                        if (alt) lp |= 1u << 29;
                        Native.PostMessage(h, alt ? 0x0104u : 0x0100u, (IntPtr)vk, (IntPtr)(long)lp);
                        Native.PostMessage(h, alt ? 0x0105u : 0x0101u, (IntPtr)vk, (IntPtr)(long)(lp | (3u << 30)));
                        Thread.Sleep(mods.Count > 0 ? 400 : 30);
                        if (saved != null)
                        {
                            Native.SetKeyboardState(saved);
                            Native.AttachThreadInput(me, target, false);
                        }
                    }
                    return "ok";
                }
            case "keydown":
            case "keyup":
                {
                    var h = Target(selPart);
                    int vk = int.Parse((tail ?? "").Trim());
                    uint scan = Native.MapVirtualKey((uint)vk, 0);
                    uint lp = 1u | (scan << 16);
                    if ((vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E || (vk >= 0x5B && vk <= 0x5D)) lp |= 1u << 24;
                    bool down = verb == "keydown";
                    Native.PostMessage(h, down ? 0x0100u : 0x0101u, (IntPtr)vk, (IntPtr)(long)(down ? lp : lp | (3u << 30)));
                    return "ok";
                }
            case "mouse":
                {
                    // A click posted to the element's window at its centre; the real cursor isn't involved
                    var e = One(selPart);
                    var h = Hwnd(e);
                    var r = e.Current.BoundingRectangle;
                    var pt = new Native.POINT { X = (int)(r.Left + r.Width / 2), Y = (int)(r.Top + r.Height / 2) };
                    Native.ScreenToClient(h, ref pt);
                    var lp = (IntPtr)((pt.Y << 16) | (pt.X & 0xffff));
                    bool right = (tail ?? "left").Trim() == "right";
                    Native.PostMessage(h, right ? 0x0204u : 0x0201u, (IntPtr)(right ? 2 : 1), lp);
                    Thread.Sleep(30);
                    Native.PostMessage(h, right ? 0x0205u : 0x0202u, IntPtr.Zero, lp);
                    return "ok";
                }
            case "chars":
                {
                    var h = Target(selPart);
                    foreach (var ch in tail ?? "") { Native.PostMessage(h, 0x0102, (IntPtr)ch, (IntPtr)1); Thread.Sleep(5); }
                    return "ok";
                }
            case "sel":
                {
                    var h = Hwnd(One(selPart));
                    var sb = new StringBuilder(65536);
                    Native.SendMessage(h, 0x000D, (IntPtr)sb.Capacity, sb);
                    long r = Native.SendMessage(h, 0x00B0, IntPtr.Zero, IntPtr.Zero).ToInt64();
                    int start = (int)(r & 0xffff), end = (int)((r >> 16) & 0xffff);
                    return "text=" + J(sb.ToString()) + " selStart=" + start + " selEnd=" + end;
                }
            case "shot":
                {
                    var parts = arg.Split(new[] { ' ' }, 2);
                    int pid = parts.Length == 2 ? int.Parse(parts[0]) : watchPid;
                    return Shot(pid, parts.Last());
                }
            case "activate":
                {
                    // Make the app the hidden desktop's foreground window (a fresh process there may not be), so
                    // focus moves inside it raise focus events as they would for the person's own active window.
                    var w = MainWindow(PidArg(arg));
                    uint target = Native.GetWindowThreadProcessId(w, out _);
                    uint me = Native.GetCurrentThreadId();
                    // On the visible desktop (runs with nobody at the PC only), borrow the current foreground
                    // thread's right to hand the foreground over
                    var fgw = Native.GetForegroundWindow();
                    uint fg = fgw == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(fgw, out _);
                    Native.AttachThreadInput(me, target, true);
                    if (fg != 0) Native.AttachThreadInput(me, fg, true);
                    bool ok = Native.SetForegroundWindow(w);
                    if (!ok)
                    {
                        // Refused under a foreground lock (ForegroundLockTimeout set to never on this PC, seen
                        // 2026-10-29); SwitchToThisWindow activates it on its own desktop anyway
                        Native.SwitchToThisWindow(w, true);
                        Thread.Sleep(300);
                        var gi = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
                        ok = Native.GetGUIThreadInfo(target, ref gi) && gi.hwndActive == w;
                    }
                    if (fg != 0) Native.AttachThreadInput(me, fg, false);
                    Native.AttachThreadInput(me, target, false);
                    return ok ? "ok" : "ERR SetForegroundWindow refused";
                }
            case "close":
                {
                    var w = arg.Contains("=") ? Hwnd(One(selPart)) : MainWindow(PidArg(arg));
                    Native.PostMessage(w, 0x0010, IntPtr.Zero, IntPtr.Zero);
                    return "ok";
                }
            default: return "ERR unknown command; try help";
        }
    }

    // "focus" is the window with the keyboard focus in the watched app's GUI thread (which UIA can't tell on a desktop
    // that isn't the input desktop), so keys go where the person's would
    static IntPtr Target(string sel)
    {
        if (sel != "focus") return Hwnd(One(sel));
        foreach (var w in TopWindows(watchPid))
        {
            var gi = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
            if (Native.GetGUIThreadInfo(Native.GetWindowThreadProcessId(w, out _), ref gi) && gi.hwndFocus != IntPtr.Zero)
                return gi.hwndFocus;
        }
        throw new InvalidOperationException("nothing focused in pid " + watchPid);
    }

    static int PidArg(string arg)
    {
        var first = arg.Split(' ').FirstOrDefault();
        return int.TryParse(first, out var p) ? p : watchPid;
    }

    static string Tree(string arg)
    {
        var parts = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        bool raw = parts.Remove("raw");
        int pid = parts.Count > 0 ? int.Parse(parts[0]) : watchPid;
        int depth = parts.Count > 1 ? int.Parse(parts[1]) : 40;
        var walker = raw ? TreeWalker.RawViewWalker : TreeWalker.ControlViewWalker;
        var sb = new StringBuilder();
        foreach (var h in TopWindows(pid))
        {
            AutomationElement e;
            try { e = AutomationElement.FromHandle(h); } catch { continue; }
            Walk(walker, e, 0, depth, sb);
        }
        return sb.ToString();
    }

    static void Walk(TreeWalker w, AutomationElement e, int level, int depth, StringBuilder sb)
    {
        sb.Append(' ', level * 2).Append(Describe(e, true)).Append('\n');
        if (level >= depth) return;
        AutomationElement c;
        try { c = w.GetFirstChild(e); } catch { return; }
        int count = 0;
        while (c != null)
        {
            if (++count > 400) { sb.Append(' ', level * 2 + 2).Append("... more\n"); break; }
            Walk(w, c, level + 1, depth, sb);
            try { c = w.GetNextSibling(c); } catch { break; }
        }
    }

    static string Focus()
    {
        var sb = new StringBuilder();
        var fg = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(fg, out var fgPid);
        sb.Append("foreground: ").Append(fg == IntPtr.Zero ? "none" : DescribeWindow(fg)).Append(" pid=").Append(fgPid).Append('\n');
        // On a desktop that isn't the input desktop, GetForegroundWindow and UIA's FocusedElement say nothing; the
        // app's own GUI thread still knows its active window and focus.
        if (watchPid != 0)
        {
            foreach (var w0 in TopWindows(watchPid))
            {
                // the first window with a focus: a process can own windows on threads that have none (Windows' input
                // indicator overlay in WinForms apps)
                uint tid = Native.GetWindowThreadProcessId(w0, out _);
                var gi = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
                if (!Native.GetGUIThreadInfo(tid, ref gi) || gi.hwndFocus == IntPtr.Zero) continue;
                sb.Append("thread ").Append(tid).Append(" active: ").Append(gi.hwndActive == IntPtr.Zero ? "none" : DescribeWindow(gi.hwndActive)).AppendLine();
                if (gi.hwndFocus != IntPtr.Zero)
                {
                    sb.Append("thread focus: ");
                    try { sb.Append(Describe(AutomationElement.FromHandle(gi.hwndFocus), true)); } catch { sb.Append(DescribeWindow(gi.hwndFocus)); }
                    sb.AppendLine();
                    var fe = AutomationElement.FromHandle(gi.hwndFocus);
                    var pw = TreeWalker.ControlViewWalker.GetParent(fe);
                    int k = 0;
                    while (pw != null && k++ < 30 && pw != AutomationElement.RootElement)
                    {
                        sb.Append("  in: ").Append(Describe(pw, false)).AppendLine();
                        pw = TreeWalker.ControlViewWalker.GetParent(pw);
                    }
                }
                break;
            }
            return sb.ToString();
        }
        AutomationElement e;
        try { e = AutomationElement.FocusedElement; } catch (Exception ex) { return sb + "focused: ERR " + ex.Message; }
        sb.Append("focused: ").Append(Describe(e, true)).Append('\n');
        var w = TreeWalker.ControlViewWalker;
        var p = w.GetParent(e);
        int i = 0;
        while (p != null && i++ < 30)
        {
            if (p == AutomationElement.RootElement) break;
            sb.Append("  in: ").Append(Describe(p, false)).Append('\n');
            p = w.GetParent(p);
        }
        return sb.ToString();
    }

    static string Detail(AutomationElement e)
    {
        var sb = new StringBuilder(Describe(e, true)).Append('\n');
        var c = e.Current;
        sb.Append("help: ").Append(c.HelpText).Append('\n');
        sb.Append("localized type: ").Append(c.LocalizedControlType).Append('\n');
        sb.Append("keys: ").Append(c.AccessKey).Append(" | ").Append(c.AcceleratorKey).Append('\n');
        sb.Append("patterns: ").Append(string.Join(", ", e.GetSupportedPatterns().Select(p => p.ProgrammaticName.Replace("PatternIdentifiers.Pattern", "")))).Append('\n');
        if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
            sb.Append("value: ").Append(((ValuePattern)vp).Current.Value).Append('\n');
        else if (e.TryGetCurrentPattern(TextPattern.Pattern, out var tp))
            sb.Append("text: ").Append(((TextPattern)tp).DocumentRange.GetText(-1)).Append('\n');
        if (e.TryGetCurrentPattern(SelectionPattern.Pattern, out var sp))
            sb.Append("selection: ").Append(string.Join(" | ", ((SelectionPattern)sp).Current.GetSelection().Select(x => x.Current.Name))).Append('\n');
        return sb.ToString();
    }

    public static string Describe(AutomationElement e, bool withValue)
    {
        try
        {
            var c = e.Current;
            var sb = new StringBuilder();
            sb.Append(c.ControlType.ProgrammaticName.Replace("ControlType.", "")).Append(' ').Append(J(c.Name));
            if (!string.IsNullOrEmpty(c.AutomationId)) sb.Append(" id=").Append(c.AutomationId);
            if (!string.IsNullOrEmpty(c.ClassName)) sb.Append(" class=").Append(c.ClassName);
            if (c.NativeWindowHandle != 0) sb.Append(" hwnd=").Append(c.NativeWindowHandle.ToString("x"));
            if (c.HasKeyboardFocus) sb.Append(" FOCUSED");
            if (c.IsKeyboardFocusable) sb.Append(" focusable");
            if (c.IsOffscreen) sb.Append(" offscreen");
            if (!c.IsEnabled) sb.Append(" disabled");
            if (!string.IsNullOrEmpty(c.HelpText)) sb.Append(" help=").Append(J(Trunc(c.HelpText, 80)));
            if (withValue && e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
            {
                var v = ((ValuePattern)vp).Current.Value ?? "";
                if (v.Length > 0) sb.Append(" value=").Append(J(Trunc(v, 120)));
            }
            if (withValue && e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sip) && ((SelectionItemPattern)sip).Current.IsSelected)
                sb.Append(" selected");
            if (withValue && e.TryGetCurrentPattern(TogglePattern.Pattern, out var tg))
                sb.Append(" toggle=").Append(((TogglePattern)tg).Current.ToggleState);
            return sb.ToString();
        }
        catch (Exception ex) { return "(gone: " + ex.GetType().Name + ")"; }
    }

    static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "...(" + s.Length + " chars)";

    static AutomationElement One(string sel)
    {
        var found = Find(sel, all: false);
        if (found.Count == 0) throw new InvalidOperationException("no element matches " + sel);
        return found[0];
    }

    static List<AutomationElement> Find(string sel, bool all)
    {
        var kv = new Dictionary<string, string>();
        foreach (var part in sel.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            int i = part.IndexOf('=');
            if (i < 0) throw new ArgumentException("bad selector part " + part);
            kv[part.Substring(0, i).Trim()] = part.Substring(i + 1);
        }
        int pid = kv.TryGetValue("pid", out var ps) ? int.Parse(ps) : watchPid;
        int n = kv.TryGetValue("n", out var ns) ? int.Parse(ns) : 0;
        var result = new List<AutomationElement>();
        foreach (var h in TopWindows(pid))
        {
            AutomationElement top;
            try { top = AutomationElement.FromHandle(h); } catch { continue; }
            var candidates = new List<AutomationElement> { top };
            candidates.AddRange(top.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>());
            foreach (var e in candidates)
            {
                if (Matches(e, kv)) result.Add(e);
            }
        }
        if (all) return result;
        return result.Count > n ? new List<AutomationElement> { result[n] } : new List<AutomationElement>();
    }

    static bool Matches(AutomationElement e, Dictionary<string, string> kv)
    {
        try
        {
            var c = e.Current;
            foreach (var p in kv)
            {
                switch (p.Key)
                {
                    case "pid": case "n": break;
                    case "name": if (c.Name != p.Value) return false; break;
                    case "name~": if (c.Name == null || c.Name.IndexOf(p.Value, StringComparison.OrdinalIgnoreCase) < 0) return false; break;
                    case "id": if (c.AutomationId != p.Value) return false; break;
                    case "type": if (c.ControlType.ProgrammaticName != "ControlType." + p.Value) return false; break;
                    case "class": if (c.ClassName != p.Value) return false; break;
                    case "focused": if (c.HasKeyboardFocus != (p.Value == "1" || p.Value == "true")) return false; break;
                    default: throw new ArgumentException("unknown selector key " + p.Key);
                }
            }
            return true;
        }
        catch (ElementNotAvailableException) { return false; }
    }

    static IntPtr Hwnd(AutomationElement e)
    {
        var w = TreeWalker.RawViewWalker;
        var cur = e;
        while (cur != null)
        {
            int h = cur.Current.NativeWindowHandle;
            if (h != 0) return (IntPtr)h;
            cur = w.GetParent(cur);
        }
        throw new InvalidOperationException("no window handle");
    }

    // ---- windows ----

    static List<IntPtr> TopWindows(int pid)
    {
        var list = new List<IntPtr>();
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out var p);
            if ((pid == 0 || p == pid) && Native.IsWindowVisible(h)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static IntPtr MainWindow(int pid)
    {
        IntPtr best = IntPtr.Zero; long bestArea = -1;
        foreach (var h in TopWindows(pid))
        {
            Native.GetWindowRect(h, out var r);
            long a = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (a > bestArea) { bestArea = a; best = h; }
        }
        if (best == IntPtr.Zero) throw new InvalidOperationException("no visible window for pid " + pid);
        return best;
    }

    static string DescribeWindow(IntPtr h)
    {
        var sb = new StringBuilder(512);
        Native.GetWindowText(h, sb, sb.Capacity);
        var cls = new StringBuilder(256);
        Native.GetClassName(h, cls, cls.Capacity);
        Native.GetWindowRect(h, out var r);
        return $"hwnd={h.ToInt64():x} {J(sb.ToString())} class={cls} rect={r.Left},{r.Top},{r.Right - r.Left}x{r.Bottom - r.Top}";
    }

    static string Shot(int pid, string file)
    {
        var h = MainWindow(pid);
        Native.GetWindowRect(h, out var r);
        int w = Math.Max(1, r.Right - r.Left), ht = Math.Max(1, r.Bottom - r.Top);
        using (var bmp = new Bitmap(w, ht, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                var dc = g.GetHdc();
                Native.PrintWindow(h, dc, 2);
                g.ReleaseHdc(dc);
            }
            bmp.Save(file, ImageFormat.Png);
        }
        return "saved " + file + " " + w + "x" + ht;
    }
}

static class WinEvents
{
    // Out-of-context WinEvents: what MSAA clients (NVDA reads WinForms mostly this way) are told about.
    const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_ALERT = 0x0002, EVENT_OBJECT_FOCUS = 0x8005,
        EVENT_OBJECT_LIVEREGIONCHANGED = 0x8019, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_NAMECHANGE = 0x800C;
    public static volatile int Pid;
    static Native.WinEventProc proc = On;

    public static void Pump()
    {
        // A small visible window of our own, so the desktop isn't empty: UIA's notification registration fails while
        // there's no window on it (ComUia), and the app under test may keep its window hidden until it has logged in
        Native.CreateWindowEx(0, "STATIC", "QuireUia", 0x80000000 /* WS_POPUP */ | 0x10000000 /* WS_VISIBLE */, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Native.SetWinEventHook(EVENT_SYSTEM_ALERT, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, proc, 0, 0, 0);
        Native.SetWinEventHook(EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS, IntPtr.Zero, proc, 0, 0, 0);
        Native.SetWinEventHook(EVENT_OBJECT_LIVEREGIONCHANGED, EVENT_OBJECT_LIVEREGIONCHANGED, IntPtr.Zero, proc, 0, 0, 0);
        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
    }

    static void On(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            Native.GetWindowThreadProcessId(hwnd, out var p);
            if (Pid != 0 && p != Pid) return;
            string name = null, role = null;
            if (Native.AccessibleObjectFromEvent(hwnd, idObject, idChild, out var acc, out var child) == 0 && acc != null)
            {
                try { name = acc.get_accName(child); } catch { }
                try { var r = acc.get_accRole(child); role = r is int ri ? RoleName(ri) : r?.ToString(); } catch { }
            }
            string kind = ev == EVENT_OBJECT_FOCUS ? "focus" : ev == EVENT_SYSTEM_ALERT ? "alert" : ev == EVENT_SYSTEM_FOREGROUND ? "foreground"
                : ev == EVENT_OBJECT_LIVEREGIONCHANGED ? "liveregion" : ev.ToString("x");
            if (ev != EVENT_OBJECT_FOCUS && ev != EVENT_SYSTEM_ALERT && ev != EVENT_SYSTEM_FOREGROUND && ev != EVENT_OBJECT_LIVEREGIONCHANGED) return;
            Server.Log("msaa", kind, name, "\"role\":" + Server.J(role) + ",\"hwnd\":\"" + hwnd.ToInt64().ToString("x") + "\""
                + $",\"obj\":{idObject},\"child\":{idChild}");
        }
        catch { }
    }

    static string RoleName(int r)
    {
        var sb = new StringBuilder(128);
        Native.GetRoleText((uint)r, sb, (uint)sb.Capacity);
        return sb.ToString();
    }
}

static class Native
{
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool altTab);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] public static extern bool SetKeyboardState(byte[] state);
    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO { public int cbSize, flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public RECT rcCaret; }
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO gi);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, StringBuilder l);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("oleacc.dll")] public static extern int AccessibleObjectFromEvent(IntPtr h, int idObject, int idChild, [MarshalAs(UnmanagedType.Interface)] out IAccessible acc, out object child);
    [DllImport("oleacc.dll", CharSet = CharSet.Unicode)] public static extern uint GetRoleText(uint role, StringBuilder sb, uint n);
}
