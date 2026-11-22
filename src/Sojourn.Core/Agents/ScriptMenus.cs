namespace Sojourn.Core.Agents;

/// <summary>
/// How viewers lay out a script menu's buttons (llDialog's list): three to a row, the list's first three along the
/// bottom, the next three above them and so on, so the last few, a short row when the count isn't a multiple of three,
/// are the top row, from the left. The official viewer's LLToastNotifyPanel::updateButtonsLayout ("we arrange buttons
/// from bottom to top for backward support of old script", its script toast widened to exactly three columns), and
/// Radegast's ntfScriptDialog draws them the same; the VR client's feed too (FeedPainter.NoticeKeys). So the list
/// [J,K,L, G,H,I, D,E,F, A,B,C] shows as A B C / D E F / G H I / J K L, top to bottom, and [A,B,C,D,E] as D E / A B C.
/// Block and Ignore sit apart, on a row of their own under them.
/// </summary>
public static class ScriptMenus
{
    public const int PerRow = 3;

    /// <summary>The list's indices row by row as shown, the top row first, each left to right.</summary>
    public static List<List<int>> Rows(int count)
    {
        var rows = new List<List<int>>();
        for (int start = (Math.Max(0, count) - 1) / PerRow * PerRow; start >= 0 && count > 0; start -= PerRow)
            rows.Add(Enumerable.Range(start, Math.Min(PerRow, count - start)).ToList());
        return rows;
    }

    /// <summary>The list's indices in the order a sighted person reads the menu: top row first, left to right.</summary>
    public static List<int> AsSeen(int count) => Rows(count).SelectMany(r => r).ToList();
}
