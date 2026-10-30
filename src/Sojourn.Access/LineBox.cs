namespace Sojourn.Access;

/// <summary>
/// A one-line input whose Enter (with Shift or Ctrl too) reaches its KeyDown handlers before Windows turns the key into
/// a character: handled there, the key is never translated, so the edit control never gets the carriage return it
/// answers with the default beep (heard in the guided test of 2026-10-29). Unhandled, Enter goes on as usual (a form's
/// default button).
/// </summary>
public class LineBox : TextBox
{
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!Multiline && (keyData & Keys.KeyCode) == Keys.Enter && (keyData & Keys.Alt) == 0
            && msg.Msg is WM_KEYDOWN)
        {
            var e = new KeyEventArgs(keyData);
            OnKeyDown(e);
            if (e.Handled || e.SuppressKeyPress) return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    const int WM_KEYDOWN = 0x0100;
}
