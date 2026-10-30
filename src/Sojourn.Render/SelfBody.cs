using LibreMetaverse;

namespace Sojourn.Render;

/// <summary>A hand to reach for: where the wrist joint should be and how it should be turned, in the view region's frame
/// (the wrist's world rotation: the skeleton's rest frame for that hand, X the thumb's side, Y along the arm outward
/// from the body, i.e. +Y for the left hand and -Y for the right, Z the back of the hand).</summary>
public readonly record struct HandTarget(Vector3 Wrist, Quaternion Rotation);

/// <summary>
/// The person's own avatar as they see it in the headset: drawn locally rather than where the region last put it, so
/// it stays under the head; its head and what's worn on it hidden (no hair or hat in your eyes); scaled so its eyes are
/// at <see cref="EyeHeight"/> above its feet; its arms reaching for the hands. Positions are in the view region's frame.
/// Only this viewer sees it: the region knows nothing of it.
/// </summary>
public sealed record SelfBody
{
    public required UUID Id { get; init; }
    /// <summary>The camera, between the eyes. Standing (not on a seat), the body is placed so its eyes are here
    /// horizontally; its feet stay on the ground the region gives it.</summary>
    public required Vector3 Head { get; init; }
    /// <summary>Where the body faces (radians counter-clockwise from east).</summary>
    public required float Yaw { get; init; }
    /// <summary>Eye height above the feet the body is scaled to when standing upright; 0 keeps its own size.</summary>
    public float EyeHeight { get; init; }
    public HandTarget? Left { get; init; }
    public HandTarget? Right { get; init; }
}
