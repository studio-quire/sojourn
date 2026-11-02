using LibreMetaverse;

namespace Sojourn.Core.Rlv;

/// <summary>What a command's param says it is (RLVa's ERlvParamType).</summary>
public enum RlvType { Unknown, Add, Remove, Force, Reply, Clear }

/// <summary>How a command went (RLVa's ERlvCmdRet), for @notify, the log and the debug echo.</summary>
public enum RlvRet
{
    Success, Unset, Duplicate, Deprecated, Delayed, Retained,
    Failed, Syntax, Option, Param, Lock, Disabled, Unknown, NoSharedRoot, Unheld, Blocked, Throttled,
}

/// <summary>
/// One command: <c>behaviour[:option]=param</c>, as RLVa's RlvCommand parses it (rlvhelper.cpp parseCommand): an empty
/// behaviour is invalid, a missing param is invalid except for <c>clear</c>; n/add and y/rem are restrictions, force a
/// one-off action, a number a reply channel. A behaviour ending in <c>_sec</c> is the strict form of one that has it.
/// </summary>
public sealed class RlvCommand
{
    public UUID Object { get; }
    public string Behaviour { get; }
    public string Option { get; }
    public string Param { get; }
    public RlvType Type { get; }
    public bool Valid { get; }
    /// <summary>The <c>_sec</c> form.</summary>
    public bool Strict { get; private set; }
    /// <summary>The dictionary entry (null: unknown, or a <c>_sec</c> the behaviour doesn't have).</summary>
    public RlvBehaviour? Info { get; private set; }
    /// <summary>Held as a counted restriction (RLVa's isRefCounted): it restricts on its own, option or not.</summary>
    public bool Counted { get; internal set; }
    /// <summary>For <c>setsphere_distmin</c>-style force commands: the local modifier's name.</summary>
    public string? LocalModifier { get; private set; }

    public RlvCommand(UUID obj, string text)
    {
        Object = obj;
        Behaviour = Option = Param = "";
        if (!Parse(text, out var b, out var o, out var p)) return;
        Behaviour = b;
        Option = o;
        Param = p;
        Type = p is "n" or "add" ? RlvType.Add
            : p is "y" or "rem" ? RlvType.Remove
            : b == "clear" ? RlvType.Clear
            : p == "force" ? RlvType.Force
            : int.TryParse(p, out _) ? RlvType.Reply
            : RlvType.Unknown;
        Valid = Type != RlvType.Unknown;
        if (!Valid) { Option = Param = ""; return; }
        Resolve();
    }

    RlvCommand(RlvCommand c, RlvType t)
    {
        Object = c.Object; Behaviour = c.Behaviour; Option = c.Option; Param = c.Param; Valid = c.Valid;
        Strict = c.Strict; Info = c.Info; LocalModifier = c.LocalModifier; Type = t;
    }

    /// <summary>The same command as a removal (what @clear and the garbage collector issue).</summary>
    public RlvCommand AsRemove() => new(this, RlvType.Remove);

    void Resolve()
    {
        int us = Behaviour.LastIndexOf('_');
        string last = us >= 0 ? Behaviour[(us + 1)..] : "";
        Strict = last == "sec";
        var name = Strict ? Behaviour[..^4] : Behaviour;
        Info = RlvDictionary.Find(name, Type);
        if (Info == null && !Strict && last.Length > 0 && Type == RlvType.Force
            && RlvDictionary.Find(Behaviour[..us], RlvType.Add) is { } owner && owner.LocalModifiers.ContainsKey(last))
        {
            Info = owner;
            LocalModifier = last;
        }
        if (Strict && Info is { Strictable: false }) Info = null;
    }

    /// <summary>RLVa's truth table: &lt;behaviour&gt;[:&lt;option&gt;]=&lt;param&gt;.</summary>
    static bool Parse(string s, out string behaviour, out string option, out string param)
    {
        behaviour = option = param = "";
        int idxParam = s.IndexOf('=');
        int idxOption = idxParam > 0 ? s.IndexOf(':') : -1;
        if (idxOption > idxParam - 1) idxOption = -1;
        if (idxOption == 0 || idxParam == 0) return false;
        behaviour = idxOption != -1 ? s[..idxOption] : idxParam != -1 ? s[..idxParam] : s;
        if (idxParam == -1 || s.Length - 1 == idxParam)
            return behaviour == "clear" && (idxOption == 0 || idxParam != 0);
        if (idxOption != -1 && idxOption + 1 != idxParam) option = s[(idxOption + 1)..idxParam];
        param = s[(idxParam + 1)..];
        return true;
    }

    public bool HasOption => Option.Length > 0;

    /// <summary>behaviour[:option], as @getstatus and @notify show it.</summary>
    public string Text => HasOption ? $"{Behaviour}:{Option}" : Behaviour;

    /// <summary>The key a restriction is stored under (a synonym's canonical behaviour), or the behaviour itself.</summary>
    public string Key => Info?.Key ?? Behaviour;

    public override string ToString() => Param.Length > 0 ? $"{Text}={Param}" : Text;

    /// <summary>Same restriction (behaviour, option, strictness), whatever the param.</summary>
    public bool SameAs(RlvCommand o) => Behaviour == o.Behaviour && Option == o.Option;
}
