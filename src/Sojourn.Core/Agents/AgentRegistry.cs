using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Config;
using Sojourn.Core.World;

namespace Sojourn.Core.Agents;

/// <summary>All agents in the process, by name and by port. Agents can be added and removed at runtime;
/// the set is saved back to the config file's "agents" list so a restart brings the same residents back.</summary>
public sealed class AgentRegistry : IAsyncDisposable
{
    readonly ConcurrentDictionary<string, AgentSession> _byName = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<int, AgentSession> _byPort = new();
    readonly object _lock = new();
    readonly ILoggerFactory _logs;
    readonly string? _configPath;

    public HostConfig Host { get; }
    public WorldHub World { get; } = new();

    public AgentRegistry(HostConfig host, ILoggerFactory logs, string? configPath)
    {
        Host = host;
        _logs = logs;
        _configPath = configPath;
    }

    public IReadOnlyCollection<AgentSession> All => _byName.Values.OrderBy(a => a.Config.Port).ToArray();

    public AgentSession? ByName(string name) => _byName.TryGetValue(name, out var a) ? a : null;
    public AgentSession? ByPort(int port) => _byPort.TryGetValue(port, out var a) ? a : null;

    public AgentSession Add(AgentConfig cfg, bool persist = true)
    {
        AgentSession session;
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(cfg.First)) throw new ArgumentException("first name required");
            if (string.IsNullOrWhiteSpace(cfg.Name)) cfg.Name = cfg.First.ToLowerInvariant();
            if (_byName.ContainsKey(cfg.Name)) throw new InvalidOperationException($"an agent named {cfg.Name} exists");
            if (cfg.Port == 0) cfg.Port = FreePort();
            else if (_byPort.ContainsKey(cfg.Port)) throw new InvalidOperationException($"port {cfg.Port} is taken");
            if (cfg.Port < Host.PortRangeStart || cfg.Port > Host.PortRangeEnd)
                throw new InvalidOperationException($"port {cfg.Port} is outside the host's range {Host.PortRangeStart}-{Host.PortRangeEnd}");
            if (Host.UsablePorts.Count > 0 && !Host.UsablePorts.Contains(cfg.Port))
                throw new InvalidOperationException($"port {cfg.Port} is held by another program");
            session = new AgentSession(cfg, Host, World, _logs);
            _byName[cfg.Name] = session;
            _byPort[cfg.Port] = session;
            if (persist && !Host.Agents.Any(a => a.Name.Equals(cfg.Name, StringComparison.OrdinalIgnoreCase)))
            {
                Host.Agents.Add(cfg);
                Save();
            }
        }
        session.Start(cfg.AutoLogin);
        return session;
    }

    public async Task<bool> RemoveAsync(string name)
    {
        AgentSession? s;
        lock (_lock)
        {
            if (!_byName.TryRemove(name, out s)) return false;
            _byPort.TryRemove(s.Config.Port, out _);
            Host.Agents.RemoveAll(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            Save();
        }
        await s.DisposeAsync();
        return true;
    }

    int FreePort()
    {
        for (var p = Host.PortRangeStart; p <= Host.PortRangeEnd; p++)
            if (!_byPort.ContainsKey(p) && (Host.UsablePorts.Count == 0 || Host.UsablePorts.Contains(p))
                && !Host.Agents.Any(a => a.Port == p && !_byName.ContainsKey(a.Name)))
                return p;
        throw new InvalidOperationException("no free port in the host's range");
    }

    void Save()
    {
        if (_configPath is null) return;
        try
        {
            var tmp = _configPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Host, HostConfig.JsonOptions));
            File.Move(tmp, _configPath, overwrite: true);
        }
        catch (Exception ex) { _logs.CreateLogger("registry").LogWarning(ex, "could not save config"); }
    }

    /// <summary>Logs every agent out, in parallel (shutdown).</summary>
    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(_byName.Values.Select(async s =>
        {
            try { await s.DisposeAsync(); } catch { }
        }));
        _byName.Clear();
        _byPort.Clear();
    }
}
