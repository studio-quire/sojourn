using System.Collections.Concurrent;
using LibreMetaverse;
using LibreMetaverse.Packets;

namespace Sojourn.Render.World;

/// <summary>
/// The animations each object signals (ObjectAnimation: llStartObjectAnimation on an animated object), by object id,
/// as the viewer's LLObjectSignaledAnimationMap keeps them: the message can come before the object does, so they're
/// kept by id rather than on the prim. A new list each message (the animator notices a new list). Attach to a client
/// before it logs in.
/// </summary>
public sealed class ObjectAnimations
{
    readonly ConcurrentDictionary<UUID, List<Animation>> _byObject = new();

    /// <summary>One for every session in the process (the host attaches it to each new client).</summary>
    public static ObjectAnimations Shared { get; } = new();

    public void Attach(GridClient client) => client.Network.RegisterCallback(PacketType.ObjectAnimation, OnAnimation, false);

    public List<Animation>? Get(UUID objectId) => _byObject.TryGetValue(objectId, out var l) ? l : null;

    void OnAnimation(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is not ObjectAnimationPacket p) return;
        var list = p.AnimationList.Select(a => new Animation { AnimationID = a.AnimID, AnimationSequence = a.AnimSequenceID, AnimationSourceObjectID = p.Sender.ID }).ToList();
        _byObject[p.Sender.ID] = list;   // an empty list too: the animator stops what played
    }
}
