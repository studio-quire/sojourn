using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Sojourn.Session.Assistant;

namespace Sojourn.Launcher;

/// <summary>
/// The language: the globe at the foot's right, findable whatever language the words are in, its list above it ("as
/// Windows", then each language by its own name). The choice is the client's own setting too (vr-settings.json's
/// uiLanguage, SettingsFile.UiLanguage); picking one puts the launcher in it at once (Lang.Use, then
/// <see cref="LanguageChanged"/> says everything again). Words.cs has how the XAML's English is translated.
/// </summary>
public partial class LauncherView
{
    long _languageListClosedAt = -10_000;

    static string ProviderWords(string label) => label == "OpenRouter" ? label : Lang.T(label);

    /// <summary>What's kept for a provider ("Anthropic key", "Studio Quire token"), in the person's language.</summary>
    static string Thing(string kind) => Lang.T(ProviderKeys.Thing(kind));

    void ShowLanguages()
    {
        FillLanguages();
        LanguageList.IsOpen = true;
        if (!Places.Background && FindChosen(LanguageChoices) is { } row)
            Dispatcher.BeginInvoke(() => row.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void FillLanguages()
    {
        LanguageChoices.Children.Clear();
        KeyboardNavigation.SetDirectionalNavigation(LanguageChoices, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetTabNavigation(LanguageChoices, KeyboardNavigationMode.Cycle);
        var setting = _settings.UiLanguage;
        var system = Lang.Find(Lang.System()) ?? Lang.All[0];
        LanguageChoices.Children.Add(LanguageRow("auto", Lang.T("as Windows"), null, system, setting == "auto"));
        var two = new UniformGrid { Columns = 2 };
        foreach (var l in Lang.All) two.Children.Add(LanguageRow(l.Code, l.Name, l, null, setting == l.Code));
        LanguageChoices.Children.Add(two);
    }

    /// <summary>A row of the list: its name (a language's in its own script and fonts), what "as Windows" is under it,
    /// and a tick on the one chosen.</summary>
    Button LanguageRow(string setting, string name, Lang.Language? own, Lang.Language? under, bool chosen)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var top = new TextBlock { Text = name, FontSize = 14, Foreground = (Brush)FindResource("Ink"), TextTrimming = TextTrimming.CharacterEllipsis };
        if (own != null) top.Language = XmlLanguage.GetLanguage(own.Tag);
        text.Children.Add(top);
        if (under != null)
            text.Children.Add(new TextBlock
            {
                Text = under.Name, FontSize = 12, Foreground = (Brush)FindResource("Faint"), Language = XmlLanguage.GetLanguage(under.Tag),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(text);
        if (chosen)
        {
            var tick = new TextBlock
            {
                Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 12, Foreground = (Brush)FindResource("Accent"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(tick, 1);
            content.Children.Add(tick);
        }
        var row = new Button { Style = (Style)FindResource("Row"), Margin = new Thickness(0), Content = content, Tag = chosen };
        AutomationProperties.SetName(row, under != null ? $"{name}, {under.Name}" : name);
        row.Click += (_, _) => PickLanguage(setting);
        return row;
    }

    static Button? FindChosen(Panel panel)
    {
        foreach (var child in panel.Children)
        {
            if (child is Button { Tag: true } b) return b;
            if (child is Panel p && FindChosen(p) is { } inner) return inner;
        }
        return null;
    }

    void PickLanguage(string setting)
    {
        LanguageList.IsOpen = false;
        if (_run.Running) return;   // the client saves the settings file while it runs
        _settings.FirstStart();   // (a language picked before the first launch mustn't make the file look already set up)
        if (setting != _settings.UiLanguage) _settings.UiLanguage = setting;
        Lang.Use(setting);
    }

    /// <summary>The language changed (here, or by whoever calls Lang.Use): everything shown is said again in it.</summary>
    internal void LanguageChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(LanguageChanged); return; }
        Words.Translate(this);
        if (Window.GetWindow(this) is { } w) w.Language = Words.Language;
        foreach (RadioButton rb in ProviderTrack.Children)
            rb.Content = ProviderWords(SettingsFile.Providers.First(p => p.Value == (string)rb.Tag).Label);
        if (_account == null) AccountName.Text = Lang.T("new account");
        UpdatePassHint();
        BuildList();
        UpdatePages();
        ShowAssistant();
        StatusText.Text = StatusWords(_run.Status);
        ShowTermsTitle();
        ShowNote();
        ShowUpdate();
        if (LanguageList.IsOpen) FillLanguages();
    }

    /// <summary>For the pictures: the language list's card as it opens from the globe, drawn on its own.</summary>
    internal FrameworkElement LanguageListShot()
    {
        FillLanguages();
        LanguageList.Child = null;
        return LanguageListCard;
    }
}
