using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace Sojourn.Launcher;

/// <summary>The password as the viewers send it ("$1$" + MD5, as the client's login sheet makes it), straight from the
/// password box's protected buffer: never a string, every copy cleared.</summary>
static class PasswordHash
{
    public static string? Of(SecureString secure)
    {
        if (secure.Length == 0) return null;
        IntPtr ptr = Marshal.SecureStringToGlobalAllocUnicode(secure);
        var chars = new char[secure.Length];
        byte[]? bytes = null;
        try
        {
            Marshal.Copy(ptr, chars, 0, chars.Length);
            bytes = new byte[Encoding.UTF8.GetMaxByteCount(chars.Length)];
            int n = Encoding.UTF8.GetBytes(chars, 0, chars.Length, bytes, 0);
            return "$1$" + Convert.ToHexString(MD5.HashData(bytes.AsSpan(0, n))).ToLowerInvariant();
        }
        finally
        {
            Array.Clear(chars);
            if (bytes != null) Array.Clear(bytes);
            Marshal.ZeroFreeGlobalAllocUnicode(ptr);
        }
    }
}
