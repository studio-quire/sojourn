using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Sojourn.Launcher;

/// <summary>
/// An installed copy's updates (docs/launcher.md > The package): checked once when the launcher opens, offered in its
/// footer, and downloaded and installed only at the person's own press (the TPV policy's consent for updates). Velopack
/// installs them; the releases come from GitHub (studio-quire/sojourn), and a release counts only if its feed
/// (releases.win.json, which holds each package's SHA-256) carries our signature: ECDSA P-256 by the key tools/package.py
/// keeps on the studio's PC, checked here against <see cref="PublicKey"/>. Velopack then checks each download against
/// the feed. SOJOURN_UPDATE_FEED (a folder or a GitHub repository's address) stands in for the real one in the
/// package's check. Velopack's version is 0.1.N for the viewer's 0.1.0.N (it takes three parts; N is the build).
/// </summary>
sealed class Updates
{
    const string Repo = "https://github.com/studio-quire/sojourn";
    /// <summary>The update key's public half (SubjectPublicKeyInfo, base64): `python tools/package.py key` made it.</summary>
    const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE2D50nwbtjTgPVZeHswojKgL3KyPc0bIjO8S58NDj3Sp7iH7UBsaWBCe5vlzy+wTU7Jg/yFu/4W3BXpHt4rPETA==";

    readonly UpdateManager _manager;
    UpdateInfo? _found;

    Updates(UpdateManager manager) => _manager = manager;

    /// <summary>The updater, for an installed copy; null when run from the repo or a folder Velopack didn't install.</summary>
    public static Updates? Make()
    {
        if (!InstalledHere) return null;
        try
        {
            var where = Environment.GetEnvironmentVariable("SOJOURN_UPDATE_FEED") is { Length: > 0 } f ? f : Repo;
            var m = new UpdateManager(new SignedFeed(where, PublicKey), new UpdateOptions { MaximumDeltasBeforeFallback = 10 });
            return m.IsInstalled ? new Updates(m) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Velopack installed this copy: its Update.exe is above the program's folder (current\).</summary>
    public static bool InstalledHere { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "..", "Update.exe"));

    /// <summary>The version a check found newer than this one ("0.1.0.N"), or null. Throws if the check failed.</summary>
    public async Task<string?> CheckAsync()
    {
        _found = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        return _found is { } u ? ViewerVersion(u.TargetFullRelease.Version.ToString()) : null;
    }

    /// <summary>Downloads the update found (progress 0-100), then hands over to Velopack's Update.exe, which closes this
    /// launcher, installs it and starts the new one. Only from the person's press.</summary>
    public async Task InstallAsync(Action<int> progress)
    {
        if (_found is not { } u) return;
        await _manager.DownloadUpdatesAsync(u, progress).ConfigureAwait(false);
        _manager.ApplyUpdatesAndRestart(u.TargetFullRelease);
    }

    /// <summary>Velopack's 0.1.N as the viewer's own 0.1.0.N.</summary>
    static string ViewerVersion(string v)
    {
        var p = v.Split('-', '+')[0].Split('.');
        return p.Length == 3 ? $"{p[0]}.{p[1]}.0.{p[2]}" : v;
    }

    /// <summary>
    /// Uninstalling (Velopack's hook, before it removes the program, 30 s at most, no windows): what can be made again
    /// goes, the caches and the logs; what's the person's stays, their saved accounts, keys, settings, chat log and the
    /// assistant's memory, so a reinstall (or Sojourn Access, which shares the keys) finds them. The folders are renamed
    /// at once and deleted by a process of their own, as a viewer's cache can hold many thousands of files.
    /// </summary>
    public static void TidyForUninstall()
    {
        var root = Sojourn.Session.DataFolder.Root;
        foreach (var name in new[] { "cache", "logs" })
        {
            var dir = Path.Combine(root, name);
            if (!Directory.Exists(dir)) continue;
            var gone = Path.Combine(root, $"{name}.removing-{Guid.NewGuid():N}");
            try { Directory.Move(dir, gone); }
            catch (Exception) { continue; }
            try
            {
                var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add("rmdir");
                psi.ArgumentList.Add("/s");
                psi.ArgumentList.Add("/q");
                psi.ArgumentList.Add(gone);
                Process.Start(psi);
            }
            catch (Exception) { }
        }
    }
}

/// <summary>
/// Velopack's update source over GitHub releases (or a folder), taking a release only when its feed's signature holds.
/// Each release carries releases.win.json and releases.win.json.sig (base64 of the IEEE P1363 r||s signature over the
/// feed's bytes); the feeds of the newest releases are merged, each package fetched from the release that has it, so a
/// person a few versions behind still finds the deltas from where they are.
/// </summary>
sealed class SignedFeed : IUpdateSource
{
    readonly string _where;
    readonly ECDsa _key;
    readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);   // package file name -> URL or path
    static readonly HttpClient Http = MakeHttp();

    public SignedFeed(string where, string publicKey)
    {
        _where = where;
        _key = ECDsa.Create();
        _key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
    }

    static HttpClient MakeHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Sojourn", Places.Version));
        return h;
    }

    bool IsFolder => !_where.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null,
        VelopackAsset? latestLocalRelease = null)
    {
        var feedName = $"releases.{channel}.json";
        var merged = new List<VelopackAsset>();
        _files.Clear();
        foreach (var drop in await Drops().ConfigureAwait(false))
        {
            if (!drop.TryGetValue(feedName, out var feedAt) || !drop.TryGetValue(feedName + ".sig", out var sigAt)) continue;
            byte[] feed, sig;
            try
            {
                feed = await Bytes(feedAt, 4 << 20).ConfigureAwait(false);
                sig = Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(await Bytes(sigAt, 4096).ConfigureAwait(false)).Trim());
            }
            catch (Exception e) { logger.Warn($"update feed {feedAt}: {e.Message}"); continue; }
            if (!_key.VerifyData(feed, sig, HashAlgorithmName.SHA256))
            {
                logger.Warn($"update feed {feedAt}: its signature doesn't hold; ignored");
                continue;
            }
            var assets = VelopackAssetFeed.FromJson(System.Text.Encoding.UTF8.GetString(feed)).Assets ?? Array.Empty<VelopackAsset>();
            foreach (var a in assets)
            {
                if (_files.ContainsKey(a.FileName) || !drop.TryGetValue(a.FileName, out var at)) continue;
                _files[a.FileName] = at;
                merged.Add(a);
            }
        }
        return new VelopackAssetFeed { Assets = merged.ToArray() };
    }

    public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress,
        CancellationToken cancelToken = default)
    {
        if (!_files.TryGetValue(releaseEntry.FileName, out var at)) throw new InvalidOperationException($"{releaseEntry.FileName} isn't in a signed feed");
        Stream source;
        long length;
        if (IsFolder)
        {
            source = File.OpenRead(at);
            length = source.Length;
        }
        else
        {
            var r = await Http.GetAsync(at, HttpCompletionOption.ResponseHeadersRead, cancelToken).ConfigureAwait(false);
            r.EnsureSuccessStatusCode();
            length = r.Content.Headers.ContentLength ?? releaseEntry.Size;
            source = await r.Content.ReadAsStreamAsync(cancelToken).ConfigureAwait(false);
        }
        await using (source)
        await using (var dest = File.Create(localFile))
        {
            var buf = new byte[1 << 20];
            long done = 0;
            int n, last = -1;
            while ((n = await source.ReadAsync(buf, cancelToken).ConfigureAwait(false)) > 0)
            {
                await dest.WriteAsync(buf.AsMemory(0, n), cancelToken).ConfigureAwait(false);
                done += n;
                int pct = length > 0 ? (int)(done * 100 / length) : 0;
                if (pct != last) progress(last = pct);
            }
        }
    }

    /// <summary>The places releases are in, newest first, each its files by name: the folder's, or each GitHub release's
    /// assets (drafts aside; the newest twenty).</summary>
    async Task<List<Dictionary<string, string>>> Drops()
    {
        var drops = new List<Dictionary<string, string>>();
        if (IsFolder)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(_where)) d[Path.GetFileName(f)] = f;
            drops.Add(d);
            return drops;
        }
        var repo = new Uri(_where).AbsolutePath.Trim('/');
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases?per_page=20");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var r = await Http.SendAsync(req).ConfigureAwait(false);
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync().ConfigureAwait(false));
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in rel.GetProperty("assets").EnumerateArray())
                d[a.GetProperty("name").GetString()!] = a.GetProperty("browser_download_url").GetString()!;
            drops.Add(d);
        }
        return drops;
    }

    async Task<byte[]> Bytes(string at, int max)
    {
        if (IsFolder)
        {
            if (new FileInfo(at).Length > max) throw new InvalidDataException("too large");
            return await File.ReadAllBytesAsync(at).ConfigureAwait(false);
        }
        using var r = await Http.GetAsync(at, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        r.EnsureSuccessStatusCode();
        if (r.Content.Headers.ContentLength > max) throw new InvalidDataException("too large");
        var b = await r.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        return b.Length <= max ? b : throw new InvalidDataException("too large");
    }
}
