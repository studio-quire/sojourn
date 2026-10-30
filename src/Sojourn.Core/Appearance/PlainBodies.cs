using System.Collections.Concurrent;
using System.Reflection;
using LibreMetaverse;
using LibreMetaverse.Imaging;

namespace Sojourn.Core.Appearance;

/// <summary>
/// Plain bodies (HostConfig.PlainBodies; the lab's zero profile): a skin or hair wearable without textures of its own
/// bakes to a flat tint of its colour sliders, with no face or shading, and textureless eyes are plain white (the
/// baker's eye bake starts white and tints only layers it has, so the eye colour sliders tint a painted iris).
/// Without this, the baker fills a textureless skin from its built-in layers (head_color.tga's eyes and lips, the skin grain's shading, the
/// body colour layers) and a new hair or eyes wearable points at the grid's default hair and iris textures, so a
/// fresh body arrives with a ready-made face. With it, a face, skin detail, hair grain or an iris is whatever the
/// resident paints and uploads; the system avatar mesh and its shape sliders stay.
///
/// The baker's layers are swapped in its own resource cache (a private static of LibreMetaverse 3.2.0's Baker), so the
/// switch is process-wide, as a host config is. Bakes made this way are cached under their own file names.
/// </summary>
public static class PlainBodies
{
    static int _installed;

    /// <summary>The slots' stock defaults, which a plain body treats as no texture (IMG_DEFAULT_AVATAR already is).</summary>
    public static bool IsStockDefault(UUID id) => id == Slots.DefaultHair || id == Slots.DefaultEyes || id == Slots.DefaultAvatar;

    /// <summary>Replace the baker's skin layers with neutral ones: transparent colour layers (the skin's tint shows
    /// through) and a skin grain that changes nothing. head_alpha.tga stays: it's the head's shape, not a look.</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;
        var field = typeof(Baker).GetField("ResourceCache", BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new InvalidOperationException("plain bodies: LibreMetaverse's Baker has no ResourceCache (a newer version?); can't swap its layers");
        var cache = (ConcurrentDictionary<string, ManagedImage>)field.GetValue(null)!;
        foreach (var name in new[] { "head_color.tga", "upperbody_color.tga", "lowerbody_color.tga" })
            cache[name] = new ManagedImage(512, 512, ManagedImage.ImageChannels.Color | ManagedImage.ImageChannels.Alpha);   // alpha 0
        var grain = new ManagedImage(512, 512, ManagedImage.ImageChannels.Gray);
        Array.Fill(grain.Red, byte.MaxValue);
        cache["head_skingrain.tga"] = grain;
        cache["body_skingrain.tga"] = grain.Clone();
        BakeCache.Variant = "plain";
    }
}
