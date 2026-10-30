using System.Text.Json.Nodes;

namespace Sojourn.Session.Assistant;

/// <summary>What the assistant shows the person (cards and labels, SojournShow's show and remove): the VR client's is
/// Showing/Shown.cs. Both refuse with a LeapError (the screen full, no such card).</summary>
public interface IShowing
{
    JsonObject Show(JsonObject a);
    JsonObject Remove(JsonObject a);
}
