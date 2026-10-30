namespace Sojourn.Render.Realtime;

/// <summary>
/// Holds the render thread's scene work (the draw list: every object, face, pass and shadow map) near a budget, the
/// way dynamic resolution holds the GPU's: twice a second the scene phase's mean decides. Over budget, what's tiny on
/// screen stops casting shadows, then stops being drawn, the threshold climbing a step at a time (radii in pixels);
/// two seconds under three quarters of it, it comes back a step. A busy place then keeps its frame rate and loses its far small
/// things first, where the viewer would just slow down. At 0, everything is drawn, as the viewer does.
/// </summary>
public sealed class SceneBudget
{
    static readonly float[] Steps = { 0, 0.6f, 0.9f, 1.3f, 1.9f, 2.8f, 4f, 6f };

    /// <summary>The scene phase's budget a frame, milliseconds (0 turns it off).</summary>
    public double BudgetMs { get; set; } = 5;
    public int Step { get; private set; }
    /// <summary>Below this on-screen radius objects aren't drawn; below <see cref="ShadowPixels"/> they cast no shadow.</summary>
    public float Pixels => Steps[Step];
    public float ShadowPixels => Step == 0 ? 0 : Steps[Step] * 2.5f;
    public Action<string>? Log { get; set; }

    double _sum;
    int _n, _calm;
    long _windowAt = System.Environment.TickCount64;

    public void Frame(double sceneMs)
    {
        _sum += sceneMs;
        _n++;
        long now = System.Environment.TickCount64;
        if (now - _windowAt < 500) return;
        double mean = _sum / Math.Max(1, _n);
        _windowAt = now;
        _sum = 0;
        _n = 0;
        if (BudgetMs <= 0) { Step = 0; return; }
        if (mean > BudgetMs && Step < Steps.Length - 1)
        {
            Step++;
            _calm = 0;
        }
        else if (mean < BudgetMs * 0.75 && Step > 0)
        {
            if (++_calm >= 4) { Step--; _calm = 0; }
        }
        else _calm = 0;
    }

    public string Describe() => Step == 0 ? "" : $", budget step {Step} (under {Pixels:0.#} px not drawn, {ShadowPixels:0.#} px no shadow)";
}
