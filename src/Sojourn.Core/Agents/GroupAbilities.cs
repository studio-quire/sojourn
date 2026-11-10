namespace Sojourn.Core.Agents;

/// <summary>
/// A group role's abilities in plain words, in the official viewer's sets and order (role_actions.xml, its Abilities
/// tab): each the power bit it sets, so a bit LibreMetaverse has no name for (the environment, ignoring the landing
/// point, posting links) is still one. Shared by Access's role tabs and the confirmations that describe a role's change.
/// Claude Opus 5.5, 2026-11-09.
/// </summary>
public static class GroupAbilities
{
    public sealed record Ability(int Bit, string Set, string Words)
    {
        public ulong Mask => 1UL << Bit;
    }

    /// <summary>Whether a role's members are shown to people outside the group (the viewer's "Reveal members"): a power
    /// bit, but a role's own setting, not an ability.</summary>
    public const ulong MembersShown = 1UL << 47;

    /// <summary>Post links in group chat: listed only where the region says it has it (GroupChatPostLinksEnabled).</summary>
    public const int PostLinksBit = 52;

    public const int InviteBit = 1, EjectBit = 2, BanBit = 51, RemoveFromRolesBit = 9, AssignAnyBit = 8, ChangeAbilitiesBit = 10;

    public static readonly Ability[] All =
    {
        new(1, "Membership", "Invite people to the group"),
        new(2, "Membership", "Eject members with no role but Everyone"),
        new(51, "Membership", "Manage the ban list"),
        new(3, "Membership", "Change Anyone can join and the cost to join"),
        new(4, "Roles", "Create roles"),
        new(5, "Roles", "Delete roles"),
        new(6, "Roles", "Change roles' names, titles, descriptions and whether their members are shown"),
        new(7, "Roles", "Give members roles, only ones they have themselves"),
        new(8, "Roles", "Give members any role"),
        new(9, "Roles", "Take members off roles"),
        new(10, "Roles", "Give roles abilities and take them away"),
        new(11, "Group identity", "Change the charter, the insignia, Show in search and the maturity"),
        new(12, "Parcel management", "Deed land to the group and buy land for it"),
        new(13, "Parcel management", "Abandon the group's land"),
        new(14, "Parcel management", "Set the group's land for sale"),
        new(15, "Parcel management", "Divide and join parcels"),
        new(17, "Parcel identity", "Turn Show in search on and off for parcels, and set their category"),
        new(18, "Parcel identity", "Change parcels' names, descriptions and moderate content settings"),
        new(19, "Parcel identity", "Set the landing point and teleport routing"),
        new(20, "Parcel settings", "Change music and media settings"),
        new(21, "Parcel settings", "Turn Anyone can edit terrain on and off"),
        new(22, "Parcel settings", "Change About Land's options"),
        new(46, "Parcel settings", "Change the environment and day cycle"),
        new(23, "Parcel powers", "Always edit terrain on the group's land"),
        new(24, "Parcel powers", "Always fly on the group's land"),
        new(25, "Parcel powers", "Always create objects on the group's land"),
        new(26, "Parcel powers", "Ignore the landing point"),
        new(28, "Parcel powers", "Set home on the group's land"),
        new(41, "Parcel powers", "Host events on the group's land"),
        new(29, "Parcel access", "Manage parcels' allowed lists"),
        new(30, "Parcel access", "Manage parcels' ban lists"),
        new(31, "Parcel access", "Change parcels' passes for sale"),
        new(32, "Parcel access", "Eject and freeze people on parcels"),
        new(48, "Parcel content", "Return objects owned by the group"),
        new(33, "Parcel content", "Return objects set to the group"),
        new(34, "Parcel content", "Return objects not of the group"),
        new(35, "Parcel content", "Landscape with Linden plants"),
        new(36, "Object management", "Deed objects to the group"),
        new(38, "Object management", "Move, copy and modify the group's objects"),
        new(39, "Object management", "Set the group's objects for sale"),
        new(40, "Accounting", "Pay group liabilities and receive group dividends"),
        new(42, "Notices", "Send notices"),
        new(43, "Notices", "Receive notices and read past ones"),
        new(16, "Chat", "Join group chat"),
        new(27, "Chat", "Join group voice chat"),
        new(37, "Chat", "Moderate group chat"),
        new(PostLinksBit, "Chat", "Post links in group chat"),
        new(49, "Experiences", "Administer the group's experiences"),
        new(50, "Experiences", "Contribute to the group's experiences"),
    };

    public static Ability? Of(int bit) => All.FirstOrDefault(a => a.Bit == bit);

    /// <summary>The viewer's warning before an ability that lets a member raise themselves (AssignDangerousActionWarning,
    /// AssignDangerousAbilityWarning), or null.</summary>
    public static string? Warning(int bit) => bit switch
    {
        AssignAnyBit => "Anyone in this role can give themselves, and any other member, roles with more powers than they have, up to nearly an owner's.",
        ChangeAbilitiesBit => "Anyone in this role can give themselves, and any other member, every ability, up to nearly an owner's.",
        _ => null,
    };

    /// <summary>The abilities a change gives and takes away, in these words (bits with no words, and whether members are
    /// shown, left out).</summary>
    public static (List<Ability> Given, List<Ability> Taken) Diff(ulong before, ulong after)
    {
        var given = All.Where(a => (after & a.Mask) != 0 && (before & a.Mask) == 0).ToList();
        var taken = All.Where(a => (before & a.Mask) != 0 && (after & a.Mask) == 0).ToList();
        return (given, taken);
    }
}
