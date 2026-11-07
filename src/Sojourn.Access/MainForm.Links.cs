using System.Diagnostics;
using System.Text.RegularExpressions;
using LibreMetaverse;
using Sojourn.Core.Access;

namespace Sojourn.Access;

/// <summary>
/// Links (docs/access-app.md > Links): Enter on a history's line opens the link on it, and Control+Shift+K or //links lists
/// the last ones seen in chat, IMs and notices, whoever said them. A web page opens in the person's browser, a place
/// teleports, a person's link is their profile; nothing else opens, whatever a line's link asks for. Written by Claude
/// Opus 5.5, 2026-11-04, after the blind tester asked for clickable links.
/// </summary>
public sealed partial class MainForm
{
    const int LinksKept = 50;
    static readonly Regex LinkIn = new(@"\b(?:https?://|secondlife://|hop://)[^\s<>""]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex PlaceLink = new(@"^(?:secondlife://|hop://|https?://(?:maps|slurl)\.secondlife\.com/secondlife/|https?://slurl\.com/secondlife/)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex PersonLink = new(@"^secondlife:///app/agent/(?<id>[0-9a-f-]{36})/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    sealed record SeenLink(string Url, string From);
    readonly List<SeenLink> _links = new();   // the newest last

    static IEnumerable<string> LinksIn(string text) =>
        LinkIn.Matches(text).Select(m => m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '\'', '"'));

    /// <summary>The first link in some text opened (a notecard's Control+K); false with none.</summary>
    bool OpenFirstLink(string text)
    {
        if (LinksIn(text).FirstOrDefault() is not { } url) return false;
        OpenLink(url);
        return true;
    }

    void NoteLinks(string text, string from)
    {
        foreach (var url in LinksIn(text))
        {
            _links.RemoveAll(l => l.Url == url);
            _links.Add(new SeenLink(url, from));
        }
        if (_links.Count > LinksKept) _links.RemoveRange(0, _links.Count - LinksKept);
    }

    static string LinkWords(string url)
    {
        if (PersonLink.IsMatch(url)) return "a person's profile";
        if (PlaceLink.IsMatch(url)) return Places.Parse(url) is { } place ? $"{place.Region}, a place" : "something only a viewer can open";
        return Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Host}{u.AbsolutePath.TrimEnd('/')}, a web page" : url;
    }

    /// <summary>Enter in a history: the first link on the line the caret is on. False with none, so Enter goes on as usual.</summary>
    bool OpenLinkOn(TextBox box)
    {
        var text = box.Text;
        int at = Math.Min(box.SelectionStart, text.Length);
        int from = at == 0 ? 0 : text.LastIndexOf('\n', at - 1) + 1, to = text.IndexOf('\n', at);
        var line = text[from..(to < 0 ? text.Length : to)];
        if (LinksIn(line).FirstOrDefault() is not { } url) return false;
        OpenLink(url);
        return true;
    }

    void OpenLink(string url)
    {
        if (PersonLink.Match(url) is { Success: true } person)
        {
            var id = new UUID(person.Groups["id"].Value);
            _ = ProfileAsync(id, id.ToString());
            return;
        }
        if (PlaceLink.IsMatch(url))
        {
            if (Places.Parse(url) is { } place) _ = _cmd.GoToAsync(place);
            else Answer("That link is for something Sojourn Access can't open.");
            return;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
        {
            Answer("That link is for something Sojourn Access can't open.");
            return;
        }
        Answer($"Opening {u.Host} in your browser.", LineFrom.People);
        if (Environment.GetEnvironmentVariable("SOJOURN_NO_BROWSER") == "1") return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Answer($"Couldn't open it: {e.Message}"); }
    }

    void ShowLinks()
    {
        if (_links.Count == 0) { Answer("No links yet. Links in chat, IMs and notices are listed here."); return; }
        var links = Enumerable.Reverse(_links).ToList();
        using var form = new Form
        {
            Name = "links", Text = "Links", AccessibleName = "Links", Size = new Size(560, 400),
            StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false,
        };
        var list = new ListBox { Name = "linksList", AccessibleName = "Links, the newest first. Enter opens one.", Dock = DockStyle.Fill };
        list.Items.AddRange(links.Select(l => (object)(l.From.Length > 0 ? $"{LinkWords(l.Url)}, from {l.From}" : LinkWords(l.Url))).ToArray());
        list.SelectedIndex = 0;
        var open = new Button { Name = "linksOpen", Text = "Open", AutoSize = true, DialogResult = DialogResult.OK };
        var close = new Button { Name = "linksClose", Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        form.AcceptButton = open;
        form.CancelButton = close;
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom };
        row.Controls.Add(open);
        row.Controls.Add(close);
        form.Controls.Add(list);
        form.Controls.Add(row);
        form.ActiveControl = list;
        if (form.ShowDialog(this) == DialogResult.OK && list.SelectedIndex >= 0) OpenLink(links[list.SelectedIndex].Url);
    }
}
