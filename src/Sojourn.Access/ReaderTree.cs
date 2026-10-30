namespace Sojourn.Access;

/// <summary>
/// A TreeView that MSAA screen readers (NVDA reads WinForms tree views this way) are told about truly: Windows' own
/// accessible object for the tree view answers them, as for any native one, and UI Automation keeps WinForms' provider.
///
/// Why: the tree view control itself announces each focus move as a WinEvent whose child id is the item's
/// accessibility id, numbered in the order items were inserted (children and placeholders included, and on after a
/// refill). WinForms' own MSAA object (.NET 7 on) reads a child id as a position among the top-level nodes, so wherever
/// the two differ the reader was told about another item: in the inventory, arrowing through the top-level folders,
/// each with a placeholder child, NVDA spoke the folder at twice the highlight's position, "not selected" (the guided
/// test of 2026-10-29). Ids past the top level's count fell through to Windows' object, so items inside folders were
/// right, and so was the top level once refilled (the ids go on counting); a flat list longer than everything that
/// went in before it would be off by that many. Windows' object maps the ids itself, and gives NVDA's tree support the
/// item's handle (its "3 of 17, level 1").
/// </summary>
public class ReaderTree : TreeView
{
    protected override void WndProc(ref Message m)
    {
        // OBJID_CLIENT to the control's own window procedure, which leaves it to oleacc's tree view proxy; the other
        // object ids (UI Automation's among them) to WinForms
        if (m.Msg == WM_GETOBJECT && unchecked((int)(long)m.LParam) == OBJID_CLIENT) { DefWndProc(ref m); return; }
        base.WndProc(ref m);
    }

    // Windows' object names the tree by its window text
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (string.IsNullOrEmpty(Text) && !string.IsNullOrEmpty(AccessibleName)) Text = AccessibleName;
    }

    const int WM_GETOBJECT = 0x003D, OBJID_CLIENT = -4;
}
