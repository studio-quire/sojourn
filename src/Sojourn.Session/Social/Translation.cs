using System.Collections.Concurrent;
using System.Globalization;

namespace Sojourn.Session.Social;

/// <summary>A way to translate a line: its translation into a language (ISO 639-1) and the language it was in, or null
/// when it can't (or the line is already in that language).</summary>
public interface ITranslator
{
    string Name { get; }
    /// <summary>Whether lines go off this computer (a cloud service): such a translator never runs while incognito.</summary>
    bool Remote { get; }
    Task<(string Text, string From)?> TranslateAsync(string text, string to, CancellationToken ct);
}

/// <summary>
/// The viewer's TranslateChat for the feed: other people's nearby lines, IMs and group chat translated into the
/// person's language, the translation drawn under the line, dim (FeedEntry.Translation). Off by default, as in the
/// viewer; which service translates is the person's call (cloud translation sends what people say to a third party),
/// so none is wired: the "test" translator (SOJOURN_TEST_TRANSLATOR=1) is for checking the plumbing, and a local one
/// (e.g. NLLB through CTranslate2, already in the assistant's venv for Whisper) or a cloud one plugs in through
/// <see cref="Register"/>. One line at a time, newest first when lines pile up, the last 500 kept; never the person's
/// own lines or objects', never a remote service while incognito. Claude Opus 5.5, 2026-10-28 (batch B4).
/// </summary>
public sealed class Translation
{
    readonly SocialFeed _feed;
    readonly SocialSettings _opt;
    readonly Func<bool> _incognito;
    readonly ConcurrentDictionary<string, ITranslator> _translators = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<(string, string), string?> _cache = new();
    readonly ConcurrentQueue<string> _cacheOrder = new();
    readonly ConcurrentStack<FeedEntry> _waiting = new();
    readonly SemaphoreSlim _wake = new(0);
    int _translated, _failed;

    public Translation(SocialFeed feed, SocialSettings opt, Func<bool> incognito)
    {
        _feed = feed;
        _opt = opt;
        _incognito = incognito;
        if (Environment.GetEnvironmentVariable("SOJOURN_TEST_TRANSLATOR") == "1") Register(new TestTranslator());
        feed.Joined += Joined;
        new Thread(Run) { IsBackground = true, Name = "translation", Priority = ThreadPriority.BelowNormal }.Start();
    }

    public void Register(ITranslator t) => _translators[t.Name] = t;

    /// <summary>The translators there are ("none" first).</summary>
    public IReadOnlyList<string> Names => new[] { "none" }.Concat(_translators.Keys.OrderBy(k => k)).ToList();

    ITranslator? Current => _translators.TryGetValue(_opt.Translator, out var t) ? t : null;

    /// <summary>The language lines go into: the setting, else the system's.</summary>
    public string To => _opt.TranslateTo is { Length: >= 2 } l ? l.ToLowerInvariant() : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    /// <summary>Why nothing is translated now, or null when it is.</summary>
    public string? Idle()
    {
        if (!_opt.Translate) return "translate_chat is off";
        if (Current is not { } t) return "no translator is set up (which service may see people's words is the person's call)";
        if (t.Remote && _incognito()) return "incognito: nothing goes to a translation service";
        return null;
    }

    public (int Translated, int Failed, int Waiting) Counts => (_translated, _failed, _waiting.Count);

    void Joined(FeedEntry e)
    {
        if (e.Mine || e.Kind == FeedKind.Notice || e.Style == FeedStyle.Object || e.Text.Length < 2 || Idle() != null) return;
        _waiting.Push(e);
        // a flood drops the oldest
        while (_waiting.Count > 30 && _waiting.TryPop(out _)) { }
        _wake.Release();
    }

    /// <summary>A text translated now, for the agent API (SojournSocial translate).</summary>
    public async Task<(string Text, string From)?> TranslateAsync(string text, string? to, CancellationToken ct)
    {
        if (Current is not { } t) throw new InvalidOperationException("no translator is set up");
        if (t.Remote && _incognito()) throw new InvalidOperationException("incognito: nothing goes to a translation service");
        return await t.TranslateAsync(text, to ?? To, ct);
    }

    void Run()
    {
        while (true)
        {
            _wake.Wait();
            if (!_waiting.TryPop(out var e) || Current is not { } t || Idle() != null) continue;
            var to = To;
            // places as their names (a service would mangle a SLurl; the line above keeps the link)
            var text = Links.Labelled(e.Text);
            try
            {
                if (!_cache.TryGetValue((text, to), out var got))
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var r = t.TranslateAsync(text, to, cts.Token).GetAwaiter().GetResult();
                    got = r is { } ok && !string.Equals(ok.From, to, StringComparison.OrdinalIgnoreCase) && ok.Text.Trim() != text.Trim() ? ok.Text.Trim() : null;
                    _cache[(text, to)] = got;
                    _cacheOrder.Enqueue(text);
                    while (_cacheOrder.Count > 500 && _cacheOrder.TryDequeue(out var old)) _cache.TryRemove((old, to), out _);
                }
                if (got is { Length: > 0 }) { _feed.SetTranslation(e.Seq, got); Interlocked.Increment(ref _translated); }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failed);
                Console.Error.WriteLine($"translation: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>For checking the plumbing without a service: "translates" by turning each word around, from a pretend
    /// language "xx", unless the line starts with "en:" (already the person's).</summary>
    sealed class TestTranslator : ITranslator
    {
        public string Name => "test";
        public bool Remote => false;

        public Task<(string Text, string From)?> TranslateAsync(string text, string to, CancellationToken ct)
        {
            if (text.StartsWith(to + ":", StringComparison.OrdinalIgnoreCase)) return Task.FromResult<(string, string)?>((text, to));
            var words = text.Split(' ').Select(w => new string(w.Reverse().ToArray()));
            return Task.FromResult<(string, string)?>(("[" + to + "] " + string.Join(' ', words), "xx"));
        }
    }
}
