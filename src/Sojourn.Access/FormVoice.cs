using System.Windows.Forms.Automation;
using Sojourn.Core.Access;

namespace Sojourn.Access;

/// <summary>
/// The announcer's UI Automation voice in this app (Narrator, JAWS and other readers; NVDA is spoken to by its own
/// API when it runs): a notification raised from the control that has the focus, or the form, through WinForms' own
/// accessibility objects. So the app's UIA tree stays WinForms' (no subclassed window procedure, as the VR client's
/// window needs), and the reader hears it from where the person is.
/// </summary>
public sealed class FormVoice(Form form) : IReaderVoice
{
    public string Name => "UI Automation";

    public bool Speak(string text, bool interrupt, bool urgent = false)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return false;
        var processing = interrupt || urgent ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.All;
        try
        {
            form.BeginInvoke(() =>
            {
                if (form.IsDisposed) return;
                Control target = form;
                if (Form.ActiveForm == form)
                    for (Control? c = form.ActiveControl; c != null; c = (c as ContainerControl)?.ActiveControl)
                        target = c;
                target.AccessibilityObject.RaiseAutomationNotification(AutomationNotificationKind.Other, processing, text);
            });
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    public void Cancel() { }   // a UIA client can't be told; the reader's own key (Ctrl) stops it
    public void Braille(string text) { }   // readers braille notifications themselves
    public void Dispose() { }
}
