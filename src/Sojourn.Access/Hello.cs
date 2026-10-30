using Windows.Security.Credentials.UI;

namespace Sojourn.Access;

/// <summary>
/// Windows Hello's consent prompt, for people whose keys never come from a physical keyboard (a braille keyboard, Dragon,
/// Voice Access, an on-screen keyboard, switch access): with `access_answers_without_key` set to hello, a confirmation answered
/// by input that isn't a hardware press is asked again by Windows, in its own window, with the action in its words,
/// answered by the person's face, fingerprint or PIN. A program can raise the prompt but not answer it (it doesn't
/// have the face or the finger; a PIN it would have to have seen typed). One at a time.
/// </summary>
static class Hello
{
    static int _asking;

    public enum Result { Verified, Refused, Unavailable }

    public static async Task<Result> AskAsync(nint window, string what)
    {
        if (Interlocked.Exchange(ref _asking, 1) == 1) return Result.Refused;
        try
        {
            if (await UserConsentVerifier.CheckAvailabilityAsync() != UserConsentVerifierAvailability.Available) return Result.Unavailable;
            var r = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(window, $"Sojourn Access: {what}");
            return r == UserConsentVerificationResult.Verified ? Result.Verified : Result.Refused;
        }
        catch (Exception) { return Result.Unavailable; }
        finally { Interlocked.Exchange(ref _asking, 0); }
    }

    /// <summary>An answer the message being handled can't give by itself (not a hardware press, or text not typed by the
    /// person's keys): with hello on, Windows Hello is asked, and then it's given; otherwise, or for a key that was held
    /// or pressed just as things changed (pressing again does), why is said.</summary>
    public static void Instead(bool hello, nint window, string what, string why, Action<string> say, Action then)
    {
        if (!hello || RealInput.Refused is Refusal.Held or Refusal.FocusMoved) { say(why); return; }
        _ = AskThen();

        async Task AskThen()
        {
            switch (await AskAsync(window, what))
            {
                case Result.Verified: then(); break;
                case Result.Unavailable: say(Missing); break;
                default: say("Not answered."); break;
            }
        }
    }

    public const string Missing = "Windows Hello isn't set up on this computer (a PIN, face or fingerprint, in Windows' sign-in options), so this takes a key on the keyboard.";
}
