using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;

namespace Sojourn.Launcher;

/// <summary>
/// The launcher's words in the person's language (Lang, linked; catalog Lang/launcher.json). The XAML keeps its
/// English: <see cref="Remember"/>, called right after InitializeComponent, walks the view's logical tree and remembers
/// each English string it finds (a TextBlock's text, a control's content, a field's hint in its Tag, an automation
/// name), then shows it in the language in use. <see cref="Translate"/>, after a language change, re-translates what
/// was remembered and lets go of what code has set since (the view re-sets those itself); elements code made later are
/// never taken for XAML's English. Names (Sojourn, Second Life, Beta, OpenRouter), glyphs and digits are left alone.
/// </summary>
static class Words
{
    sealed class Said(string english) { public readonly string English = english; public string Shown = english; }

    static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, Said>> _said = new();
    static readonly HashSet<string> Names = new(StringComparer.Ordinal) { "Sojourn", "Second Life", "Beta", "OpenRouter" };

    /// <summary>The language for WPF itself (xml:lang): its CJK fonts and line breaking follow it.</summary>
    public static XmlLanguage Language => XmlLanguage.GetLanguage(Lang.Current.Tag);

    public static void Remember(FrameworkElement root) { root.Language = Language; Walk(root, first: true); }

    public static void Translate(FrameworkElement root) { root.Language = Language; Walk(root, first: false); }

    static void Walk(DependencyObject o, bool first)
    {
        Word(o, AutomationProperties.NameProperty, first);
        switch (o)
        {
            case TextBlock: Word(o, TextBlock.TextProperty, first); break;
            case ContentControl: Word(o, ContentControl.ContentProperty, first); break;
        }
        if (o is TextBox) Word(o, FrameworkElement.TagProperty, first);   // a field's hint (Theme.xaml shows its Tag)
        foreach (var child in LogicalTreeHelper.GetChildren(o))
            if (child is DependencyObject d) Walk(d, first);
    }

    static void Word(DependencyObject o, DependencyProperty p, bool first)
    {
        var now = o.ReadLocalValue(p) as string;
        Said? s = null;
        if (_said.TryGetValue(o, out var said)) said.TryGetValue(p, out s);
        if (s == null)
        {
            if (!first || now is null || !Worded(now)) return;
            _said.GetOrCreateValue(o)[p] = s = new Said(now);
        }
        else if (now != s.Shown)
        {
            said!.Remove(p);   // code has set it since: it's the code's to say
            return;
        }
        s.Shown = Lang.T(s.English);
        if (s.Shown != now) o.SetValue(p, s.Shown);
    }

    static bool Worded(string s) => !Names.Contains(s) && s.Any(char.IsLetter);
}
