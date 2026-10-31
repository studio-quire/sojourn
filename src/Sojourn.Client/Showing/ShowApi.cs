using System.Text.Json.Nodes;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

namespace Sojourn.Client.Showing;

/// <summary>
/// SojournShow: the assistant's way to show the person things on screen (<see cref="Shown"/>). Held like any op while
/// a confirmation is under way (not RunsWhileHeld), and everything shown hides while one waits.
/// </summary>
public sealed class ShowApi : LeapApi
{
    readonly Shown _shown;

    public override string Name => "SojournShow";
    public override string Desc => "things the assistant shows the person on screen: cards and labels, with them or out in the world";
    public override bool NeedsSession => false;

    public ShowApi(AgentSession s, Shown shown) : base(s)
    {
        _shown = shown;
        Op("show", "{id, at?, title?, blocks?: [...], text?, for?} -> the element: shows it, or changes the one with that id (fields given replace "
            + "its own). at: view | wrist | room | an object's or avatar's id | [x,y,z] in this region. Blocks: {text, style?: body|title|big|dim}, "
            + "{list: [text | {text, detail?, done?}], numbered?}, {table: [[cells]], header?}, {timer: seconds}, {stopwatch: true}, "
            + "{progress, of?, label?}, {chart: [values | [label, value]], kind?: bars|line, unit?}, {image: view | texture id | last | a picture's file name in Pictures\\Sojourn (big; at room puts it in front of them; "
            + "incognito: blind only last, pictures taken in a spell only during it)}, "
            + "{toward: id | [x,y,z], label?}, {map: [id | [x,y,z] | {at, label}]}. for: seconds until it goes by itself", a => _shown.Show(a));
        Op("remove", "{id | all: true}: takes it away", a => _shown.Remove(a));
        Op("list", "-> {shown: [{id, at, title, blocks, age_s, ...}]}: what's on screen", _ => _shown.List());
    }
}
