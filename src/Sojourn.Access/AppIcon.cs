using System.Reflection;

namespace Sojourn.Access;

/// <summary>
/// Sojourn Access's icon (art/icon/access.ico, rendered by tools/icon.py) on its windows. The exe carries it as its
/// ApplicationIcon, which Explorer shows; a WinForms window doesn't take the exe's, but shows WinForms' own generic icon
/// unless the form sets one. So the same .ico, embedded as a resource, becomes the default every Form starts with: one
/// place for all of them, the forms built as plain Forms included. Each window then picks the sizes it wants from it
/// (the hand-drawn 16 and 24 for the title bar, 32 or 48 for Alt+Tab and the taskbar). A fixed dialog shows no icon
/// unless it's given its own (Windows' way for dialogs), so the login form, the first window in the taskbar, is given
/// one by Load. Should a later WinForms rename its default, the other windows show the generic icon and nothing else
/// changes.
/// </summary>
static class AppIcon
{
    /// <summary>Before any form is made.</summary>
    public static void Apply()
    {
        if (Load() is { } icon)
            typeof(Form).GetField("s_defaultIcon", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, icon);
    }

    /// <summary>The icon, a new instance each time (a form given the default's own instance treats it as unset).</summary>
    public static Icon? Load()
    {
        using var s = typeof(AppIcon).Assembly.GetManifestResourceStream("access.ico");
        return s == null ? null : new Icon(s);
    }
}
