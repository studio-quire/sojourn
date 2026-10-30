using LibreMetaverse;
using Sojourn.Session.Assistant;

namespace Sojourn.Session.Private;

/// <summary>Dictation as the session's parts see it (People/PeopleWiring.cs shows the person typing while they dictate):
/// the VR client's is Private/Dictation.cs, held by its keys and grips, its draft going to the conversation open on the
/// feed.</summary>
public interface IDictation
{
    /// <summary>The dictation button is held.</summary>
    bool Held { get; }
    /// <summary>A draft waits for the person.</summary>
    bool Pending { get; }
    /// <summary>Where a draft would go now: the conversation open (its session and name), else nearby chat.</summary>
    (DraftTo To, UUID? Session, string? Name) Destination();
    /// <summary>The other person of the one-to-one IM open now, if any.</summary>
    UUID? OpenImWith();
}
