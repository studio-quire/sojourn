using System.Diagnostics;
using System.Reflection;

namespace Sojourn.Access;

/// <summary>
/// Lists and trees emptied without making the window wait on UI Automation. As WinForms removes an item it disconnects
/// the item's provider (UiaDisconnectProvider), a call that waits on the programs listening to UI Automation: with a
/// listener attached as a screen reader attaches (global focus, structure and name events), half a millisecond an item a
/// screen reader had touched here (1000 tree nodes cleared in 478 ms, against 29 ms untouched or with nothing listening;
/// 2026-11-13), several times that on a slow PC, and all of it on the window's thread ahead of any key she presses. On the
/// blind tester's PC, showing a few of her inventory's items after browsing it took 0.3-1.3 s (her logs of 2026-11-11).
/// So the providers are taken off the items first, the items cleared at once, and WinForms' own release run for each
/// afterward, ten milliseconds at a time on a timer, whose ticks wait behind her keys (posted messages would go ahead of
/// them). Until it's run, a reader still holding one gets what WinForms gives for an item that's gone.
/// This reaches into WinForms' private members (TreeNode._accessibleObject and ListViewItem._accessibilityObject, with
/// each one's ReleaseUiaProvider, as of .NET 10); without them, clearing is as WinForms does it.
/// Written by Claude Opus 5.5, 2026-11-13.
/// </summary>
static class UiaRelease
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly FieldInfo? NodeObject = typeof(TreeNode).GetField("_accessibleObject", Private);
    static readonly MethodInfo? NodeRelease = typeof(TreeNode).GetMethod("ReleaseUiaProvider", Private, Type.EmptyTypes);
    static readonly FieldInfo? ItemObject = typeof(ListViewItem).GetField("_accessibilityObject", Private);
    static readonly MethodInfo? ItemRelease = typeof(ListViewItem).GetMethod("ReleaseUiaProvider", Private, Type.EmptyTypes);
    const int SliceMs = 10;

    static readonly Queue<(object Owner, object Provider)> _waiting = new();
    static System.Windows.Forms.Timer? _timer;

    public static bool Ready => NodeObject != null && NodeRelease != null && ItemObject != null && ItemRelease != null;

    /// <summary>A tree's (or a node's) nodes removed, their children's too. The window's thread.</summary>
    public static void Clear(TreeNodeCollection nodes)
    {
        if (NodeObject == null || NodeRelease == null) { nodes.Clear(); return; }
        int before = _waiting.Count;
        Take(nodes);
        nodes.Clear();
        Wait(_waiting.Count - before);
    }

    static void Take(TreeNodeCollection nodes)
    {
        foreach (TreeNode n in nodes)
        {
            if (NodeObject!.GetValue(n) is { } provider)
            {
                NodeObject.SetValue(n, null);
                _waiting.Enqueue((n, provider));
            }
            if (n.Nodes.Count > 0) Take(n.Nodes);
        }
    }

    /// <summary>A list view's items removed. The window's thread.</summary>
    public static void Clear(ListView list)
    {
        if (ItemObject == null || ItemRelease == null) { list.Items.Clear(); return; }
        int before = _waiting.Count;
        foreach (ListViewItem item in list.Items)
            if (ItemObject.GetValue(item) is { } provider)
            {
                ItemObject.SetValue(item, null);
                _waiting.Enqueue((item, provider));
            }
        list.Items.Clear();
        Wait(_waiting.Count - before);
    }

    static void Wait(int added)
    {
        if (added == 0) return;
        if (_timer == null)
        {
            _timer = new System.Windows.Forms.Timer { Interval = 1 };
            _timer.Tick += (_, _) => ReleaseSome();
        }
        _timer.Start();
    }

    static void ReleaseSome()
    {
        var slice = Stopwatch.StartNew();
        while (_waiting.Count > 0 && slice.ElapsedMilliseconds < SliceMs)
        {
            var (owner, provider) = _waiting.Dequeue();
            try
            {
                // (put back, then WinForms' own release: a list item's sub-items too; not into one that's been added
                // again meanwhile and may have a provider of its own: that one's left, holding a little memory)
                if (owner is TreeNode n && n.TreeView == null && NodeObject!.GetValue(n) == null) { NodeObject.SetValue(n, provider); NodeRelease!.Invoke(n, null); }
                else if (owner is ListViewItem i && i.ListView == null && ItemObject!.GetValue(i) == null) { ItemObject.SetValue(i, provider); ItemRelease!.Invoke(i, null); }
            }
            catch (Exception) { /* a provider not released holds a little memory, nothing more */ }
        }
        if (_waiting.Count == 0) _timer!.Stop();
    }
}
