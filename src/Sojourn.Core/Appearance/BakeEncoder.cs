using CoreJ2K;
using CoreJ2K.j2k.image;
using CoreJ2K.j2k.image.input;
using CoreJ2K.Configuration;
using LibreMetaverse.Imaging;

namespace Sojourn.Core.Appearance;

/// <summary>
/// J2C for client-side bakes the way the viewer reads them: head, upper and lower bakes carry five components (R, G,
/// B, alpha, and the morph mask), and the viewer decodes component 4 as the bake's aux image (llvoavatar
/// onBakedTextureMasksLoaded; a bake without it fails to load and the avatar stays a cloud). LibreMetaverse's encoder
/// always writes four, dropping the mask its baker fills into the bump plane.
/// </summary>
public static class BakeEncoder
{
    public static byte[] Encode(ManagedImage img, bool withMask)
    {
        var cfg = CompleteConfigurationPresets.Streaming.WithFileFormat(false);
        return J2kImage.ToBytes(new Reader(img, withMask ? 5 : 4), cfg.EncoderConfiguration);
    }

    sealed class Reader : ImgReader
    {
        readonly ManagedImage _img;

        public Reader(ManagedImage img, int components)
        {
            _img = img;
            w = img.Width;
            h = img.Height;
            nc = components;
        }

        public override void Close() { }
        public override int GetNomRangeBits(int c) => 8;
        public override int GetFixedPoint(int c) => 0;
        public override bool IsOrigSigned(int c) => false;

        byte Sample(int c, int i) => c switch
        {
            0 => _img.Red?[i] ?? 255,
            1 => _img.Green?[i] ?? 255,
            2 => _img.Blue?[i] ?? 255,
            3 => _img.Alpha?[i] ?? 255,
            _ => _img.Bump?[i] ?? 255,   // the morph mask; unset means no masking
        };

        public override DataBlk GetInternCompData(DataBlk blk, int c)
        {
            if (blk.DataType != DataBlk.TYPE_INT) blk = new DataBlkInt(blk.ulx, blk.uly, blk.w, blk.h);
            var arr = new int[blk.w * blk.h];
            var k = 0;
            for (var y = blk.uly; y < blk.uly + blk.h; y++)
            {
                var row = y * w;
                for (var x = blk.ulx; x < blk.ulx + blk.w; x++) arr[k++] = Sample(c, row + x) - 128;
            }
            if (blk is DataBlkInt bi) bi.DataInt = arr; else blk.Data = arr;
            blk.offset = 0;
            blk.scanw = blk.w;
            blk.progressive = false;
            return blk;
        }

        public override DataBlk GetCompData(DataBlk blk, int c) => GetInternCompData(blk, c);
    }
}
