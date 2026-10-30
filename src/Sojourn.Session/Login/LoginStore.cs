using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Sojourn.Session.Login;

/// <summary>The grids the login screen offers by name; any other is a login URI (OpenSim grids, the local grid).</summary>
public static class Grids
{
    public const string Main = "agni", Beta = "aditi";
    public const string MainUri = "https://login.agni.lindenlab.com/cgi-bin/login.cgi";
    public const string BetaUri = "https://login.aditi.lindenlab.com/cgi-bin/login.cgi";

    public static string UriOf(string grid) => grid switch { Main => MainUri, Beta => BetaUri, _ => grid };

    /// <summary>A grid's key for folder names (the state and cache dirs): agni, aditi, or host_port.</summary>
    public static string KeyOf(string grid)
    {
        if (grid is Main or Beta) return grid;
        return Uri.TryCreate(grid, UriKind.Absolute, out var u) ? $"{u.Host}_{u.Port}" : "grid";
    }

    public static bool IsSecondLife(string grid) => grid is Main or Beta;

    /// <summary>What a person typed as a login URI, made whole (a scheme, a path): null if it can't be one.</summary>
    public static string? Normalise(string typed)
    {
        var t = typed.Trim();
        if (t.Length == 0) return null;
        if (!t.Contains("://")) t = "http://" + t;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.Host.Length == 0) return null;
        if (u.Host.EndsWith("lindenlab.com", StringComparison.OrdinalIgnoreCase))
            return u.Host.Contains("aditi", StringComparison.OrdinalIgnoreCase) ? Beta : Main;
        return u.AbsolutePath == "/" ? u.GetLeftPart(UriPartial.Authority) + "/" : u.ToString();
    }

    /// <summary>A grid's name for a person: Second Life, Beta, or the host.</summary>
    public static string Label(string grid, string? name = null) => grid switch
    {
        Main => "Second Life",
        Beta => "Beta",
        _ => name is { Length: > 0 } ? name : Uri.TryCreate(grid, UriKind.Absolute, out var u) ? (u.IsDefaultPort ? u.Host : $"{u.Host}:{u.Port}") : grid,
    };

    /// <summary>The grid's own name from its get_grid_info (OpenSim's, as Firestorm's grid manager asks), or null.</summary>
    public static async Task<string?> AskNameAsync(string uri)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            var xml = await http.GetStringAsync(new Uri(new Uri(uri), "get_grid_info"));
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            var name = doc.Root?.Element("gridname")?.Value?.Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (Exception) { return null; }
    }
}

/// <summary>Where a login starts, as the login screen and the launcher take it: last, home, or a place typed as a
/// region name or SLurl (secondlife://, maps.secondlife.com, hop://, Region/x/y/z) made "uri:Region&amp;x&amp;y&amp;z".</summary>
public static class Starts
{
    /// <summary>The start for a choice (0 last, 1 home, 2 a place) and the place's text; null: no place given.</summary>
    public static string? Of(int start, string place)
    {
        if (start == 0) return "last";
        if (start == 1) return "home";
        var t = Uri.UnescapeDataString(place.Trim());
        if (t.Length == 0) return null;
        bool hop = t.StartsWith("hop://", StringComparison.OrdinalIgnoreCase);
        foreach (var prefix in new[] { "secondlife:///app/teleport/", "secondlife://", "https://maps.secondlife.com/secondlife/", "http://maps.secondlife.com/secondlife/",
                     "https://slurl.com/secondlife/", "http://slurl.com/secondlife/", "hop://" })
            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { t = t[prefix.Length..]; break; }
        var p = t.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (hop && p.Length > 1) p = p[1..];   // hop://grid:port/Region/x/y/z
        if (p.Length == 0) return null;
        return $"uri:{p[0]}&{N(p, 1, "128")}&{N(p, 2, "128")}&{N(p, 3, "0")}";

        static string N(string[] p, int i, string d) => i < p.Length && float.TryParse(p[i], System.Globalization.CultureInfo.InvariantCulture, out var f)
            ? ((int)MathF.Round(f)).ToString(System.Globalization.CultureInfo.InvariantCulture) : d;
    }

    /// <summary>A start as a person reads it back into the place field: the region and position, or "".</summary>
    public static string PlaceText(string start)
    {
        if (!start.StartsWith("uri:", StringComparison.Ordinal)) return "";
        var p = start[4..].Split('&');
        return p.Length >= 4 && p[1] == "128" && p[2] == "128" && p[3] == "0" ? p[0] : string.Join('/', p);
    }
}

/// <summary>A saved account: who, on which grid, where to start, and (only if the person asked) the password as the
/// viewers keep it: "$1$" and the MD5 of it, never plain, protected by Windows for this user (DPAPI).</summary>
public sealed record SavedAccount
{
    public string First { get; init; } = "";
    public string Last { get; init; } = "Resident";
    /// <summary>agni, aditi, or a login URI.</summary>
    public string Grid { get; init; } = Grids.Main;
    /// <summary>The grid's own name, for others (from get_grid_info).</summary>
    public string? GridName { get; init; }
    /// <summary>last, home, or uri:Region&amp;x&amp;y&amp;z.</summary>
    public string Start { get; init; } = "last";
    public DateTime Used { get; init; }
    /// <summary>DPAPI-protected "$1$..." (base64), or null: not remembered.</summary>
    public string? Secret { get; init; }

    [JsonIgnore] public string Id => Ids.Of(Grid, First, Last);
    [JsonIgnore] public bool Remembered => Secret != null;
    /// <summary>The name as a person writes it: the username alone for Resident.</summary>
    [JsonIgnore] public string Name => Last.Equals("Resident", StringComparison.OrdinalIgnoreCase) ? First : $"{First} {Last}";
}

public static class Ids
{
    public static string Of(string grid, string first, string last) =>
        $"{Grids.KeyOf(grid)}/{first.Trim().ToLowerInvariant()}.{last.Trim().ToLowerInvariant()}";
}

/// <summary>
/// Saved accounts, in a folder of their own the assistant can't reach: the repo's local/secrets/logins when run from
/// the repo, else %LOCALAPPDATA%\Sojourn\secrets\logins (the assistant's deny rules name both, and its shell hook
/// refuses commands that do: assistant/harness.py). The usernames are there too (TPV policy 2.e keeps them to LL's
/// servers as it does passwords). Passwords go in only when the person ticks "remember", and only as the viewers keep
/// them ($1$ + MD5), under DPAPI for this Windows user. SOJOURN_LOGINS points it elsewhere (tests).
/// Written by Claude Opus 5.5, 2026-10-28.
/// </summary>
public sealed class LoginStore
{
    public string Dir { get; }
    string FilePath => Path.Combine(Dir, "accounts.json");
    string ResumePath => Path.Combine(Dir, "resume.dat");
    readonly object _lock = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public LoginStore(string baseDir) => Dir = Locate(baseDir);

    static string Locate(string baseDir)
    {
        if (Environment.GetEnvironmentVariable("SOJOURN_LOGINS") is { Length: > 0 } env) return Path.GetFullPath(env);
        // the repo's local/: the folder above the base dir that sits beside src/Sojourn.Session
        for (var d = new DirectoryInfo(Path.GetFullPath(baseDir)); d != null; d = d.Parent)
            if (d.Name == "local" && d.Parent is { } root && Directory.Exists(Path.Combine(root.FullName, "src", "Sojourn.Client")))
                return Path.Combine(d.FullName, "secrets", "logins");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sojourn", "secrets", "logins");
    }

    sealed class FileShape
    {
        public List<SavedAccount> Accounts { get; set; } = new();
        public string? Last { get; set; }
    }

    FileShape Read()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<FileShape>(File.ReadAllText(FilePath), Json) ?? new() : new(); }
        catch (Exception) { return new(); }
    }

    void Write(FileShape f)
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(f, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>The saved accounts, the last used first.</summary>
    public IReadOnlyList<SavedAccount> Accounts()
    {
        lock (_lock) return Read().Accounts.OrderByDescending(a => a.Used).ToList();
    }

    public string? LastId { get { lock (_lock) return Read().Last; } }

    /// <summary>After a login that worked: the account kept (or updated), last used now; its password kept only as
    /// given (hash: remember it; null with remember off: forget any kept one).</summary>
    public void Logged(SavedAccount account, string? hash, bool remember)
    {
        lock (_lock)
        {
            var f = Read();
            var old = f.Accounts.FirstOrDefault(a => a.Id == account.Id);
            f.Accounts.RemoveAll(a => a.Id == account.Id);
            string? secret = !remember ? null : hash != null ? Protect(hash) : old?.Secret;
            f.Accounts.Add(account with { Used = DateTime.UtcNow, Secret = secret });
            f.Last = account.Id;
            Write(f);
        }
    }

    /// <summary>The person turned "remember" off for a saved account: its password goes at once.</summary>
    public void ForgetPassword(string id)
    {
        lock (_lock)
        {
            var f = Read();
            int i = f.Accounts.FindIndex(a => a.Id == id);
            if (i < 0 || f.Accounts[i].Secret == null) return;
            f.Accounts[i] = f.Accounts[i] with { Secret = null };
            Write(f);
        }
    }

    /// <summary>The account forgotten: off the list, its password with it.</summary>
    public void Forget(string id)
    {
        lock (_lock)
        {
            var f = Read();
            f.Accounts.RemoveAll(a => a.Id == id);
            if (f.Last == id) f.Last = null;
            Write(f);
        }
    }

    /// <summary>The kept "$1$..." for an account, or null.</summary>
    public static string? HashOf(SavedAccount a) => a.Secret is { } s ? Unprotect(s) : null;

    // ---- across a restart after a lost GPU, or from the launcher: the account and its hash for the next run, for seconds ----

    /// <summary>The next run (started at once) logs this account in without asking: its hash goes to a file protected
    /// as a remembered password is, which the next run reads and deletes; older than two minutes it's ignored and
    /// deleted. <paramref name="remember"/>: whether that run keeps the password after it works (null: as the account
    /// already is). The launcher (Sojourn.Launcher) hands its login to the client this way.</summary>
    public void HandOver(SavedAccount account, string hash, bool? remember = null)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var o = new JsonObject { ["account"] = JsonSerializer.SerializeToNode(account with { Secret = null }, Json), ["hash"] = hash, ["at"] = DateTime.UtcNow.ToString("o") };
            if (remember is { } r) o["remember"] = r;
            File.WriteAllText(ResumePath, Protect(o.ToJsonString()));
        }
        catch (Exception) { }
    }

    /// <summary>A handed-over login nobody took (the client never started): gone.</summary>
    public void DropHandOver()
    {
        try { File.Delete(ResumePath); } catch (Exception) { }
    }

    public (SavedAccount Account, string Hash, bool? Remember)? TakeOver()
    {
        try
        {
            if (!File.Exists(ResumePath)) return null;
            var text = File.ReadAllText(ResumePath);
            File.Delete(ResumePath);
            if (Unprotect(text) is not { } json || JsonNode.Parse(json) is not JsonObject o) return null;
            if (!DateTime.TryParse(o["at"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
                || DateTime.UtcNow - at > TimeSpan.FromMinutes(2)) return null;
            var acct = o["account"].Deserialize<SavedAccount>(Json);
            var hash = o["hash"]?.GetValue<string>();
            bool? remember = o["remember"]?.GetValue<bool>();
            return acct != null && hash != null ? (acct, hash, remember) : null;
        }
        catch (Exception) { return null; }
    }

    // ---- DPAPI (CryptProtectData for the current user) ----

    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Sojourn saved login");

    static string Protect(string plain)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        try { return Convert.ToBase64String(Dpapi(data, protect: true)); }
        finally { Array.Clear(data); }
    }

    static string? Unprotect(string protectedB64)
    {
        try
        {
            var bytes = Dpapi(Convert.FromBase64String(protectedB64), protect: false);
            try { return Encoding.UTF8.GetString(bytes); } finally { Array.Clear(bytes); }
        }
        catch (Exception) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DataBlob { public int Size; public IntPtr Data; }

    const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);

    static byte[] Dpapi(byte[] input, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("saved logins need Windows' data protection");
        var hIn = GCHandle.Alloc(input, GCHandleType.Pinned);
        var hEnt = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var inBlob = new DataBlob { Size = input.Length, Data = hIn.AddrOfPinnedObject() };
            var entBlob = new DataBlob { Size = Entropy.Length, Data = hEnt.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref inBlob, "Sojourn", ref entBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob);
            if (!ok) throw new InvalidOperationException($"data protection failed ({Marshal.GetLastWin32Error()})");
            try
            {
                var result = new byte[outBlob.Size];
                Marshal.Copy(outBlob.Data, result, 0, outBlob.Size);
                return result;
            }
            finally { LocalFree(outBlob.Data); }
        }
        finally { hIn.Free(); hEnt.Free(); }
    }
}
