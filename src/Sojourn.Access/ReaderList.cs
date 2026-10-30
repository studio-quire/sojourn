using System.Reflection;
using System.Runtime.InteropServices;

namespace Sojourn.Access;

/// <summary>
/// A list view that tells screen readers an item took the focus only while the list has the keyboard focus itself.
///
/// Why: every list keeps a focused item, the one the person comes back to (Fill puts it back after a refresh), and
/// WinForms raises UI Automation's focus-changed event for an item whenever it's made the focused one
/// (ListViewItem.Focused's setter) or is selected while focused (OnSelectedIndexChanged), in a list without the focus
/// too. So a list filling beside the one the person was in announced its own item as though the focus had gone there:
/// arriving in Objects nearby (Alt+7), the parts and faces lists under it filled just after it and sent the last word
/// ("Finding its parts.", "Faces need the view ..."), and Friends and My groups did the same every 3 s while the
/// person was in People nearby. On Second Life itself (2026-10-29, NVDA) Alt+7 said "Objects nearby, table" and no item
/// until Down. So: MakeCurrent sets the focused item without the event while the list hasn't the focus, and selection
/// changes there leave out WinForms' event (its internal `_selectedItem` says that item was announced already); the
/// SelectedIndexChanged handlers run as before, and OnGotFocus announces the focused item when the person arrives.
/// </summary>
public class ReaderList : ListView
{
    static readonly FieldInfo? Announced = typeof(ListView).GetField("_selectedItem", BindingFlags.Instance | BindingFlags.NonPublic);

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        if (!Focused && SelectedIndices.Count > 0) Announced?.SetValue(this, Items[SelectedIndices[0]]);
        base.OnSelectedIndexChanged(e);
    }

    /// <summary>The item made the list's focused one (where the person lands coming back), announced only if the list
    /// has the keyboard focus.</summary>
    public static void MakeCurrent(ListViewItem item)
    {
        if (item.ListView is not { } list) return;
        if (list.Focused || !list.IsHandleCreated) { item.Focused = true; return; }
        var state = new LVITEM { state = LVIS_FOCUSED, stateMask = LVIS_FOCUSED };
        SendMessage(list.Handle, LVM_SETITEMSTATE, item.Index, ref state);
    }

    const int LVM_SETITEMSTATE = 0x1000 + 43;
    const uint LVIS_FOCUSED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    struct LVITEM
    {
        public uint mask;
        public int iItem, iSubItem;
        public uint state, stateMask;
        public IntPtr pszText;
        public int cchTextMax, iImage;
        public IntPtr lParam;
        public int iIndent, iGroupId;
        public uint cColumns;
        public IntPtr puColumns, piColFmt;
        public int iGroup;
    }

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hwnd, int msg, int wParam, ref LVITEM lParam);
}
