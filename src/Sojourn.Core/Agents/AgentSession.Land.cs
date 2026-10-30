namespace Sojourn.Core.Agents;

/// <summary>Land and region (batch B5): the estate's tools and the land seen from the map. Parcels are
/// <see cref="Parcels"/> (AgentSession.Work.cs).</summary>
public sealed partial class AgentSession
{
    EstateOps? _estate;
    public EstateOps Estate => _estate ??= new EstateOps(this);
    LandMap? _landMap;
    public LandMap LandMap => _landMap ??= new LandMap(this);
}
