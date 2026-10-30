namespace Sojourn.Access.Walk;

/// <summary>
/// Where walk mode's keys go: a plain focusable pane at the foot of the window, out of the Tab order, reached by F3
/// (as Radegast's arrows walk only while its 3D window has the focus). Not an edit or a list, so a screen reader passes
/// the arrows and letters straight through rather than reading lines or items. Its text is the last cue's line, for
/// anyone looking. Leaving it (F3, Escape, Alt+number, another window) lets go of every key.
/// </summary>
public sealed class WalkPad : Control
{
    WalkMode? _walk;

    /// <summary>The session's walk mode, once logged in (until then the pad does nothing).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public WalkMode? Walk
    {
        get => _walk;
        set
        {
            _walk = value;
            if (value != null) value.Lines += line => { if (IsHandleCreated) BeginInvoke(() => { Text = $"Walk mode: {line}"; Invalidate(); }); };
        }
    }

    public WalkPad()
    {
        Name = "walkPad";
        AccessibleName = "Walk mode";
        AccessibleDescription = "Arrows walk and turn. F1 for walk mode's keys, Escape leaves.";
        AccessibleRole = AccessibleRole.Pane;
        TabStop = false;
        Dock = DockStyle.Bottom;
        Height = 22;
        SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Text = "Walk mode: F3";
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) switch
    {
        Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.Space => true,
        _ => base.IsInputKey(keyData),
    };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // bit 30 of the message's lParam: the key was already down (a repeat)
        bool repeat = _repeat;
        if (_walk?.KeyDown(e.KeyCode, e.Shift, repeat) == true) e.Handled = e.SuppressKeyPress = true;
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        _walk?.KeyUp(e.KeyCode);
        base.OnKeyUp(e);
    }

    bool _repeat;

    protected override bool ProcessKeyMessage(ref Message m)
    {
        if (m.Msg is 0x0100 or 0x0104) _repeat = ((long)m.LParam & (1L << 30)) != 0;
        return base.ProcessKeyMessage(ref m);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        _walk?.Enter();
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        _walk?.Leave();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Focused ? SystemColors.Highlight : SystemColors.Control);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Focused ? SystemColors.HighlightText : SystemColors.ControlText,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }
}
