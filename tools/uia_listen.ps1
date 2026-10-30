# Test helper: a UI Automation client that listens for notification events (UiaRaiseNotificationEvent), as Narrator
# and JAWS do, and writes each to a file as a JSON line {t, process, name, text, kind, processing, activity}. With
# -ProcessId only that process's. Runs until -Seconds pass. Used by tools/screenreader.py; never speaks anything.
#   powershell -NoProfile -File tools/uia_listen.ps1 -Out FILE [-Seconds 120] [-ProcessId PID]
param([string]$Out, [int]$Seconds = 120, [int]$ProcessId = 0)
Add-Type -ReferencedAssemblies UIAutomationClient, UIAutomationTypes, WindowsBase -TypeDefinition @"
using System;
using System.IO;
using System.Windows.Automation;
public static class Listen {
  static StreamWriter w; static int pid;
  static string J(string s) { return s == null ? "null" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\""; }
  public static void Start(string path, int processId) {
    pid = processId;
    w = new StreamWriter(path, false) { AutoFlush = true };
    Automation.AddAutomationEventHandler(AutomationElement.NotificationEvent, AutomationElement.RootElement, TreeScope.Subtree, On);
    w.WriteLine("{\"ready\": true}");
  }
  static void On(object src, AutomationEventArgs e) {
    var n = e as NotificationEventArgs;
    if (n == null) return;
    int p = 0; string name = null;
    try { var el = src as AutomationElement; if (el != null) { p = el.Current.ProcessId; name = el.Current.Name; } } catch (Exception) { }
    if (pid != 0 && p != pid) return;
    lock (w) w.WriteLine("{\"t\": " + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ", \"process\": " + p + ", \"name\": " + J(name)
      + ", \"text\": " + J(n.DisplayString) + ", \"kind\": " + J(n.NotificationKind.ToString()) + ", \"processing\": "
      + J(n.NotificationProcessing.ToString()) + ", \"activity\": " + J(n.ActivityId) + "}");
  }
}
"@
[Listen]::Start($Out, $ProcessId)
Start-Sleep -Seconds $Seconds
