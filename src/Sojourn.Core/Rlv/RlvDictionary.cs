using LibreMetaverse;

namespace Sojourn.Core.Rlv;

/// <summary>What a restriction takes as its option (RLVa's ERlvBehaviourOptionType); Custom: its own handler.</summary>
public enum RlvOpt { None, Exception, NoneOrException, Modifier, NoneOrModifier, Custom }

/// <summary>Which way a global modifier's values combine: the smallest wins, the largest, or the first set.</summary>
public enum RlvComp { First, Min, Max }

/// <summary>A global modifier (RLVa's RlvBehaviourModifier): the value a restriction carries, combined across objects.</summary>
public sealed record RlvModifierSpec(string Name, object Default, bool AddDefaultOnEmpty, RlvComp Comp);

/// <summary>Force-wear flags (RLVa's FORCEWEAR_*).</summary>
[Flags]
public enum RlvWear { None = 0, Replace = 1, Add = 2, Remove = 4, Node = 16, Subtree = 32, ContextNone = 256, ContextObject = 512 }

/// <summary>One behaviour of the RLVa command set, for one kind of use (restriction, force or reply).</summary>
public sealed class RlvBehaviour
{
    public required string Name { get; init; }
    /// <summary>The canonical behaviour (synonyms map to it: touchfar to fartouch, camdistmax to setcam_avdistmax...).</summary>
    public required string Key { get; init; }
    public RlvOpt Opt { get; init; }
    public bool Strictable { get; init; }
    public bool Synonym { get; init; }
    public bool Deprecated { get; init; }
    /// <summary>Only one object may hold it at a time (setcam, setdebug, setenv: RLVa refuses a second holder).</summary>
    public bool Exclusive { get; init; }
    public RlvModifierSpec? Modifier { get; init; }
    /// <summary>Per-object values set by <c>behaviour_name:value=force</c> (setsphere's, setoverlay's): name and type.</summary>
    public Dictionary<string, Type> LocalModifiers { get; init; } = new();
    public RlvWear Wear { get; init; }
    /// <summary>Which kind: Add (restriction: n/y), Force, Reply.</summary>
    public RlvType Kind { get; init; }
}

/// <summary>
/// The command set, after RLVa's RlvBehaviourDictionary (Firestorm's rlvhelper.cpp, read 2026-11-01): every restriction,
/// force and reply command it knows, with the same option types, strictness, synonyms and modifiers. What Sojourn does
/// with each is in Rlv.cs and docs/rlv.md.
/// </summary>
public static class RlvDictionary
{
    static readonly Dictionary<(string, RlvType), RlvBehaviour> Map = new();
    static readonly List<RlvBehaviour> All = new();

    public static IReadOnlyList<RlvBehaviour> Entries => All;

    public static RlvBehaviour? Find(string name, RlvType type)
    {
        var kind = type is RlvType.Add or RlvType.Remove ? RlvType.Add : type;
        return Map.GetValueOrDefault((name, kind));
    }

    public static readonly RlvModifierSpec FarTouchDist = new("fartouchdist", 1.5f, true, RlvComp.Min);
    public static readonly RlvModifierSpec SitTpDist = new("sittpdist", 1.5f, true, RlvComp.Min);
    public static readonly RlvModifierSpec TpLocalDist = new("tplocaldist", 1.5f, true, RlvComp.Min);
    public static readonly RlvModifierSpec RecvImDistMin = new("recvimdistmin", float.MaxValue, true, RlvComp.Max);
    public static readonly RlvModifierSpec RecvImDistMax = new("recvimdistmax", float.MaxValue, true, RlvComp.Min);
    public static readonly RlvModifierSpec SendImDistMin = new("sendimdistmin", float.MaxValue, true, RlvComp.Max);
    public static readonly RlvModifierSpec SendImDistMax = new("sendimdistmax", float.MaxValue, true, RlvComp.Min);
    public static readonly RlvModifierSpec StartImDistMin = new("startimdistmin", float.MaxValue, true, RlvComp.Max);
    public static readonly RlvModifierSpec StartImDistMax = new("startimdistmax", float.MaxValue, true, RlvComp.Min);
    public static readonly RlvModifierSpec CamAvDist = new("setcam_avdist", 0f, false, RlvComp.Max);
    public static readonly RlvModifierSpec CamAvDistMin = new("setcam_avdistmin", 0f, false, RlvComp.Max);
    public static readonly RlvModifierSpec CamAvDistMax = new("setcam_avdistmax", float.MaxValue, false, RlvComp.Min);
    public static readonly RlvModifierSpec CamOriginDistMin = new("setcam_origindistmin", 0f, true, RlvComp.Max);
    public static readonly RlvModifierSpec CamOriginDistMax = new("setcam_origindistmax", float.MaxValue, true, RlvComp.Min);
    public static readonly RlvModifierSpec CamEyeOffset = new("setcam_eyeoffset", Vector3.Zero, true, RlvComp.First);
    public static readonly RlvModifierSpec CamEyeOffsetScale = new("setcam_eyeoffsetscale", 0f, true, RlvComp.First);
    public static readonly RlvModifierSpec CamFocusOffset = new("setcam_focusoffset", Vector3.Zero, true, RlvComp.First);
    /// <summary>The viewer's default field of view (DEFAULT_FIELD_OF_VIEW, 60 degrees vertical), in radians.</summary>
    public const float DefaultFov = 1.0471976f;
    public static readonly RlvModifierSpec CamFovMin = new("setcam_fovmin", DefaultFov, true, RlvComp.Max);
    public static readonly RlvModifierSpec CamFovMax = new("setcam_fovmax", DefaultFov, true, RlvComp.Min);
    /// <summary>The viewer's default texture (IMG_DEFAULT), what @setcam_textures paints the world with when given no texture.</summary>
    public static readonly UUID DefaultTexture = new("d2114404-dd59-4a4d-8e6c-49359e91bbf0");
    public static readonly RlvModifierSpec CamTexture = new("setcam_textures", DefaultTexture, true, RlvComp.First);
    public static readonly RlvModifierSpec NameTagsDist = new("shownametagsdist", 0f, true, RlvComp.Min);

    static RlvBehaviour Add(string name, RlvOpt opt = RlvOpt.None, bool strict = false, RlvModifierSpec? mod = null,
        string? key = null, bool deprecated = false, bool exclusive = false, Dictionary<string, Type>? local = null, RlvWear wear = RlvWear.None)
    {
        var b = new RlvBehaviour
        {
            Name = name, Key = key ?? name, Opt = opt, Strictable = strict, Modifier = mod, Synonym = key != null,
            Deprecated = deprecated, Exclusive = exclusive, LocalModifiers = local ?? new(), Wear = wear, Kind = RlvType.Add,
        };
        Map[(name, RlvType.Add)] = b;
        All.Add(b);
        return b;
    }

    static void Force(string name, RlvWear wear = RlvWear.None, string? key = null)
    {
        var b = new RlvBehaviour { Name = name, Key = key ?? name, Kind = RlvType.Force, Wear = wear };
        Map[(name, RlvType.Force)] = b;
        All.Add(b);
    }

    static void Reply(string name)
    {
        var b = new RlvBehaviour { Name = name, Key = name, Kind = RlvType.Reply };
        Map[(name, RlvType.Reply)] = b;
        All.Add(b);
    }

    static RlvDictionary()
    {
        const RlvOpt E = RlvOpt.Exception, NE = RlvOpt.NoneOrException, M = RlvOpt.Modifier, NM = RlvOpt.NoneOrModifier, C = RlvOpt.Custom;
        Add("acceptpermission");
        Add("accepttp", NE, strict: true);
        Add("accepttprequest", NE, strict: true);
        Add("addattach", C);
        Add("addoutfit", C);
        Add("allowidle");
        Add("alwaysrun");
        Add("attachthis", C, wear: RlvWear.Node);
        Add("attachallthis", C, key: "attachthis", wear: RlvWear.Subtree);
        Add("attachthis_except", C, wear: RlvWear.Node);
        Add("attachallthis_except", C, key: "attachthis_except", wear: RlvWear.Subtree);
        Add("buy");
        Add("chatwhisper");
        Add("chatnormal");
        Add("chatshout");
        Add("detach", C);
        Add("detachthis", C, wear: RlvWear.Node);
        Add("detachallthis", C, key: "detachthis", wear: RlvWear.Subtree);
        Add("detachthis_except", C, wear: RlvWear.Node);
        Add("detachallthis_except", C, key: "detachthis_except", wear: RlvWear.Subtree);
        Add("edit", NE);
        Add("editattach");
        Add("editobj", E);
        Add("editworld");
        Add("viewtransparent");
        Add("viewwireframe");
        Add("emote");
        Add("fartouch", NM, mod: FarTouchDist);
        Add("touchfar", NM, mod: FarTouchDist, key: "fartouch");
        Add("fly");
        Add("interact");
        Add("jump");
        Add("notify", C);
        Add("pay");
        Add("permissive");
        Add("recvchat", NE, strict: true);
        Add("recvchatfrom", E, strict: true);
        Add("recvemote", NE, strict: true);
        Add("recvemotefrom", E, strict: true);
        Add("recvim", C, strict: true);
        Add("recvimfrom", E, strict: true);
        Add("redirchat", C);
        Add("rediremote", C);
        Add("remattach", C);
        Add("remoutfit", C);
        Add("rez");
        Add("sendchannel", C, strict: true);
        Add("sendchannel_except", C, strict: true);
        Add("sendchat");
        Add("sendim", C, strict: true);
        Add("sendimto", E, strict: true);
        Add("sendgesture");
        Add("setdebug", exclusive: true);
        Add("setenv", exclusive: true);
        Add("setgroup");
        Add("share", NE, strict: true);
        Add("sharedunwear", C);
        Add("sharedwear", C);
        Add("showhovertext", E);
        Add("showhovertextall");
        Add("showhovertexthud");
        Add("showhovertextworld");
        Add("showinv");
        Add("showloc");
        Add("showminimap");
        Add("shownames", NE, strict: true);
        Add("shownametags", C, strict: true);
        Add("shownearby");
        Add("showself");
        Add("showselfhead");
        Add("showworldmap");
        Add("sit");
        Add("sittp", NM, mod: SitTpDist);
        Add("standtp");
        Add("startim", C, strict: true);
        Add("startimto", E, strict: true);
        Add("temprun");
        Add("touchall");
        Add("touchattach", NE);
        Add("touchattachother", NE);
        Add("touchattachself");
        Add("touchhud", NE);
        Add("touchme");
        Add("touchthis", E);
        Add("touchworld", NE);
        Add("tplm");
        Add("tploc");
        Add("tplocal", NM, mod: TpLocalDist);
        Add("tplure", NE, strict: true);
        Add("tprequest", NE, strict: true);
        Add("unsharedunwear", C);
        Add("unsharedwear", C);
        Add("unsit");
        Add("viewnote");
        Add("viewscript");
        Add("viewtexture");
        Add("setcam", exclusive: true);
        Add("setcam_avdist", M, mod: CamAvDist);
        Add("setcam_avdistmin", M, mod: CamAvDistMin);
        Add("setcam_avdistmax", M, mod: CamAvDistMax);
        Add("setcam_origindistmin", M, mod: CamOriginDistMin);
        Add("setcam_origindistmax", M, mod: CamOriginDistMax);
        Add("setcam_eyeoffset", M, mod: CamEyeOffset);
        Add("setcam_eyeoffsetscale", M, mod: CamEyeOffsetScale);
        Add("setcam_focusoffset", M, mod: CamFocusOffset);
        Add("setcam_fovmin", C, mod: CamFovMin);
        Add("setcam_fovmax", C, mod: CamFovMax);
        Add("setcam_mouselook");
        Add("setcam_textures", NM, mod: CamTexture);
        Add("setcam_unlock");
        Add("camavdist", M, mod: CamAvDist, key: "setcam_avdist", deprecated: true);
        Add("camdistmin", M, mod: CamAvDistMin, key: "setcam_avdistmin", deprecated: true);
        Add("camdistmax", M, mod: CamAvDistMax, key: "setcam_avdistmax", deprecated: true);
        Add("camtextures", NM, mod: CamTexture, key: "setcam_textures", deprecated: true);
        Add("camzoommin", C, deprecated: true);
        Add("camzoommax", C, deprecated: true);
        Add("camunlock", key: "setcam_unlock", deprecated: true);
        Add("setoverlay", NM, local: new() { ["alpha"] = typeof(float), ["texture"] = typeof(UUID), ["tint"] = typeof(Vector3) });
        Add("setoverlay_touch");
        Add("setsphere", NM, local: new()
        {
            ["mode"] = typeof(int), ["origin"] = typeof(int), ["color"] = typeof(Vector3), ["distmin"] = typeof(float),
            ["distmax"] = typeof(float), ["distextend"] = typeof(int), ["param"] = typeof(Vector4), ["tween"] = typeof(float),
            ["valuemin"] = typeof(float), ["valuemax"] = typeof(float),
        });

        const RlvWear R = RlvWear.Replace, A = RlvWear.Add, X = RlvWear.Remove, Nd = RlvWear.Node, St = RlvWear.Subtree,
            Cn = RlvWear.ContextNone, Co = RlvWear.ContextObject;
        Force("attach", R | Nd | Cn);
        Force("attachall", R | St | Cn);
        Force("attachover", A | Nd | Cn);
        Force("attachallover", A | St | Cn);
        Force("attachthis", R | Nd | Co);
        Force("attachallthis", R | St | Co);
        Force("attachthisover", A | Nd | Co);
        Force("attachallthisover", A | St | Co);
        Force("detach", X | Nd | Cn);
        Force("detachall", X | St | Cn);
        Force("detachthis", X | Nd | Co);
        Force("detachallthis", X | St | Co);
        Force("remattach", X | Nd | Cn);
        Force("remoutfit", X | Nd | Cn);
        Force("addoutfit", R | Nd | Cn);
        Force("addoutfitall", R | St | Cn);
        Force("addoutfitover", A | Nd | Cn);
        Force("addoutfitallover", A | St | Cn);
        Force("addoutfitthis", R | Nd | Co);
        Force("addoutfitallthis", R | St | Co);
        Force("addoutfitthisover", A | Nd | Co);
        Force("addoutfitallthisover", A | St | Co);
        Force("attachoverorreplace", R | Nd | Cn);
        Force("attachalloverorreplace", R | St | Cn);
        Force("attachthisoverorreplace", R | Nd | Co);
        Force("attachallthisoverorreplace", R | St | Co);
        Force("adjustheight");
        Force("detachme");
        Force("fly");
        Force("setcam_focus");
        Force("setcam_eyeoffset");
        Force("setcam_eyeoffsetscale");
        Force("setcam_focusoffset");
        Force("setcam_fov");
        Force("setcam_mode");
        Force("setgroup");
        Force("setrot");
        Force("sit");
        Force("sitground");
        Force("tpto");
        Force("unsit");
        Force("setoverlay_tween");
        Force("setdebug_avatarsex");
        Force("setdebug_renderresolutiondivisor");

        foreach (var r in new[]
        {
            "findfolder", "findfolders", "getaddattachnames", "getaddoutfitnames", "getattach", "getattachnames",
            "getcam_avdist", "getcam_avdistmin", "getcam_avdistmax", "getcam_fov", "getcam_fovmin", "getcam_fovmax",
            "getcam_textures", "getcommand", "getgroup", "getheightoffset", "getinv", "getinvworn", "getoutfit",
            "getoutfitnames", "getpath", "getpathnew", "getremattachnames", "getremoutfitnames", "getsitid", "getstatus",
            "getstatusall", "version", "versionnew", "versionnum",
            "getdebug_avatarsex", "getdebug_renderresolutiondivisor", "getdebug_restrainedloveforbidgivetorlv",
            "getdebug_restrainedlovenosetenv", "getdebug_windlightuseatmosshaders",
        }) Reply(r);
    }
}
