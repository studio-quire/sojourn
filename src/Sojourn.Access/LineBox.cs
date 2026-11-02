namespace Sojourn.Access;

/// <summary>
/// A one-line input whose Enter (with Shift or Ctrl too) reaches its KeyDown handlers before Windows turns the key into
/// a character: handled there, the key is never translated, so the edit control never gets the carriage return it
/// answers with the default beep (heard in the guided test of 2026-10-29). Unhandled, Enter goes on as usual (a form's
/// default button). With AppsMenu set, the Applications key, Shift+F10 and a right click show that menu instead of the
/// edit control's own (a conversation's, MainForm.ImSounds.cs).
/// </summary>
public class LineBox : TextBox
{
    /// <summary>Shows the box's own menu in place of Windows' edit menu; null: Windows' menu.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action? AppsMenu { get; set; }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // on the key's press (Windows' own menu comes on its release, and never for posted keys)
        if (AppsMenu != null && msg.Msg is WM_KEYDOWN or WM_SYSKEYDOWN && keyData is Keys.Apps or (Keys.Shift | Keys.F10))
        {
            AppsMenu();
            return true;
        }
        if (!Multiline && (keyData & Keys.KeyCode) == Keys.Enter && (keyData & Keys.Alt) == 0
            && msg.Msg is WM_KEYDOWN)
        {
            var e = new KeyEventArgs(keyData);
            OnKeyDown(e);
            if (e.Handled || e.SuppressKeyPress) return true;
        }
        if (keyData == (Keys.Control | Keys.Back) && msg.Msg is WM_KEYDOWN && TextKeys.DeleteWordBack(this)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CONTEXTMENU && AppsMenu != null)
        {
            // a key's (lParam -1) was shown on its press; a right click's now
            if (m.LParam != -1) AppsMenu();
            return;
        }
        base.WndProc(ref m);
    }

    const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_CONTEXTMENU = 0x007B;
}

/// <summary>
/// Control+Backspace deletes the word before the caret, as in Word, browsers and Radegast: a plain Windows edit control
/// types U+007F for it instead (a box that screen readers read as "delete", or not at all). Handled before the key is
/// translated, so no character arrives.
/// </summary>
public static class TextKeys
{
    /// <summary>The selection if there is one, else back to the start of the word before the caret (the spaces before
    /// it, then the word, or a run of punctuation; a line break on its own); a password box clears back to its start.
    /// False if the box takes no typing (the key then goes on as usual).</summary>
    public static bool DeleteWordBack(TextBoxBase box)
    {
        if (box.ReadOnly || !box.Enabled) return false;
        if (box.SelectionLength == 0)
        {
            int end = box.SelectionStart;
            bool secret = box is TextBox { UseSystemPasswordChar: true } or TextBox { PasswordChar: not '\0' };
            int start = secret ? 0 : WordStart(box.Text, end);
            if (start == end) return true;
            box.Select(start, end - start);
        }
        box.SelectedText = "";
        return true;
    }

    static int WordStart(string text, int at)
    {
        int i = at;
        while (i > 0 && text[i - 1] is ' ' or '\t') i--;
        if (i > 0 && text[i - 1] == '\n') return i < at ? i : i - (i > 1 && text[i - 2] == '\r' ? 2 : 1);
        bool word = i > 0 && IsWord(text[i - 1]);
        while (i > 0 && !char.IsWhiteSpace(text[i - 1]) && IsWord(text[i - 1]) == word) i--;
        return i;
    }

    static bool IsWord(char c) => char.IsLetterOrDigit(c) || c is '_' or '\'';
}
