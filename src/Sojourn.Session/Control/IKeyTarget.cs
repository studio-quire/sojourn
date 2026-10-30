namespace Sojourn.Session.Control;

/// <summary>What the headset's keyboard types into: a page's focused field, or a request to the assistant.</summary>
public interface IKeyTarget
{
    /// <summary>What it types into, in a few words, for the keyboard's strip.</summary>
    string Label { get; }
    /// <summary>False once it's gone (the page put away): the keyboard goes with it.</summary>
    bool Alive { get; }
    /// <summary>The thing typed into; the same one asked for again puts the keyboard away.</summary>
    object Owner { get; }
    void Char(char ch);
    void Backspace();
    /// <summary>Enter; true when that finishes the typing (the keyboard goes).</summary>
    bool Enter();
    /// <summary>The keyboard came up for it.</summary>
    void Opened();
    /// <summary>The keyboard went (× or another target).</summary>
    void Closed();
}
