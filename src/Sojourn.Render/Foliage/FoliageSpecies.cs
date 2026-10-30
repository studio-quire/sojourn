// Data from the Second Life viewer: indra/newview/app_settings/trees.xml and grass.xml, read the way
// LLVOTree::initClass / LLVOGrass::initClass read them (llvotree.cpp, llvograss.cpp).
// Copyright (C) 2010, Linden Research, Inc. LGPL 2.1 only; see the viewer's licence header.
using UUID = LibreMetaverse.UUID;

namespace Sojourn.Render.Foliage;

/// <summary>LLVOTree::TreeSpeciesData, one row of trees.xml.</summary>
public sealed record TreeSpecies(
    int Id,
    string Name,
    UUID Texture,
    float Droop,          // degrees from vertical at each branch recursion
    float Twist,
    float Branches,       // branches per recursion; the loop runs (int)Branches times
    int Depth,            // U8 mDepth
    float ScaleStep,
    int TrunkDepth,       // U8 mTrunkDepth; read with istream >> int, so "3.0" -> 3 and "0.1" -> 0
    float BranchLength,
    float TrunkLength,
    float LeafScale,
    float BillboardScale,
    float BillboardRatio,
    float TrunkAspect,
    float BranchAspect,
    float LeafRotate,     // mRandomLeafRotate: parsed, unused by the geometry
    float NoiseMag,
    float NoiseScale,
    float Taper,
    float RepeatZ);

/// <summary>LLVOGrass::GrassSpeciesData, one row of grass.xml.</summary>
public sealed record GrassSpecies(int Id, string Name, UUID Texture, float BladeSizeX, float BladeSizeY);

public static class FoliageSpecies
{
    private static TreeSpecies T(int id, string name, string tex, float droop, float twist, float branches, int depth,
        float scaleStep, int trunkDepth, float branchLength, float trunkLength, float leafScale, float billboardScale,
        float billboardRatio, float trunkAspect, float branchAspect, float leafRotate, float noiseMag, float noiseScale,
        float taper, float repeatZ)
        => new(id, name, new UUID(tex), droop, twist, branches, depth, scaleStep, trunkDepth, branchLength, trunkLength,
            leafScale, billboardScale, billboardRatio, trunkAspect, branchAspect, leafRotate, noiseMag, noiseScale, taper, repeatZ);

    /// <summary>trees.xml, indexed by species id (0..20, no gaps).</summary>
    public static readonly IReadOnlyList<TreeSpecies> Trees =
    [
        //  id name               texture                                 droop  twist branches depth step trunkD branchL trunkL leaf  bbScale bbRatio trunkAsp branchAsp leafRot noiseMag noiseScale taper repeatZ
        T(0, "Pine 1", "0187babf-6c0d-5891-ebed-4ecab1426683",            60f, 5f, 5f, 1, 0.7f, 6, 8f, 11.5f, 22f, 39.5f, 1.1f, 0.1f, 0.05f, 20f, 0.5f, 2.5f, 0.8f, 3f),
        T(1, "Oak", "8a515889-eac9-fb55-8eba-d2dc09eb32c8",               35f, 3f, 4f, 3, 0.7f, 0, 3f, 3.8f, 7f, 10.25f, 1f, 0.15f, 0.07f, 0f, 1.2f, 4f, 0.3f, 4f),
        T(2, "Tropical Bush 1", "5bc11cd6-2f40-071e-a8da-0903394204f9",   10f, 0f, 6f, 1, 0.5f, 1, 0.5f, 0.15f, 7.5f, 5f, 1.25f, 1f, 0.08f, 0f, 1f, 1f, 0.2f, 1f),
        T(3, "Palm 1", "ca4e8c27-473c-eb1c-2f5d-50ee3f07d85c",            0f, 0f, 3f, 1, 0.5f, 0, 0.7f, 9f, 10f, 13.25f, 1f, 0.035f, 0.03f, 0f, 0.2f, 6f, 0.7f, 10f),
        T(4, "Dogwood", "64367bd1-697e-b3e6-0b65-3f862a577366",           30f, 0f, 3f, 2, 0.7f, 1, 2.75f, 4f, 5.5f, 10f, 1f, 0.06f, 0.05f, 0f, 1.5f, 2f, 0.8f, 3f),
        T(5, "Tropical Bush 2", "cdd9a9fc-6d0b-f90d-8416-c72b6019bca8",   10f, 0f, 3f, 1, 0.5f, 1, 0.5f, 0.15f, 6f, 4.5f, 0.9f, 1f, 0.08f, 0f, 1f, 1f, 0.2f, 1f),
        T(6, "Palm 2", "2d784476-d0db-9979-0cff-9408745a7cf3",            0f, 0f, 3f, 1, 0.5f, 0, 0.7f, 10f, 7.5f, 13.5f, 1f, 0.035f, 0.03f, 0f, 0.2f, 6f, 0.6f, 12f),
        T(7, "Cypress 1", "fb2ae204-3fd1-df33-594f-c9f882830e66",         30f, 0f, 3f, 4, 0.5f, 0, 10f, 10f, 70f, 22.5f, 1f, 0.05f, 0.03f, 0f, 1.2f, 1f, 0.5f, 6f),
        T(8, "Cypress 2", "30047cec-269d-408e-0c30-b2603b887268",         30f, 0f, 3f, 4, 0.6f, 3, 7.5f, 10f, 35f, 25f, 0.8f, 0.05f, 0.04f, 0f, 1.2f, 1f, 0.5f, 5f),
        T(9, "Pine 2", "d691a01c-13b7-578d-57c0-5caef0b4e7e1",            50f, 7.5f, 4f, 2, 0.7f, 6, 6f, 10f, 15.5f, 33f, 1.35f, 0.1f, 0.08f, 5f, 0.5f, 2.5f, 0.7f, 3f),
        T(10, "Plumeria", "6de37e4e-7029-61f5-54b8-f5e63f983f58",         8f, 7f, 3f, 2, 0.6f, 0, 3f, 0.1f, 20f, 10f, 1.35f, 0.10f, 0.075f, 0f, 0f, 0f, 0.85f, 2f),
        T(11, "Winter Pine 1", "10d2a01a-0818-84b9-4b96-c2eb63256519",    90f, 2.5f, 6f, 1, 0.66f, 8, 0f, 4f, 6.75f, 12.5f, 0.6f, 0.1f, 0.05f, 0f, 0f, 2.5f, 0.85f, 2f),
        T(12, "Winter Aspen", "7c0cf89b-44b1-1ce2-dd74-07102a98ac2a",     85f, 3f, 5f, 1, 0.6f, 8, 3f, 4.5f, 8f, 12f, 0.675f, 0.06f, 0.05f, 0f, 0.75f, 2.5f, 0.8f, 2f),
        T(13, "Winter Pine 2", "67931331-0c02-4876-1255-28770896c6a2",    140f, 5f, 6f, 1, 0.6f, 7, 0f, 3f, 5f, 7.5f, 0.5f, 0.1f, 0.05f, 0f, 0.75f, 2.5f, 0.5f, 2f),
        T(14, "Eucalyptus", "a6162133-724b-54df-a12f-51cd070ad6f3",       20f, 5f, 3.6f, 4, 0.6f, 0, 12f, 8f, 33f, 24f, 1.3f, 0.15f, 0.08f, 0f, 0f, 0f, 0.675f, 3f),
        T(15, "Fern", "8872f2b8-31db-42d8-580a-b3e4a91262de",             12f, 0f, 7f, 1, 0.5f, 0, 0.01f, 0f, 4f, 3.5f, 0.85f, 1f, 0.08f, 0f, 1f, 1f, 0.2f, 1f),
        T(16, "Eelgrass", "96b4de31-f4fa-337d-ec78-451e3609769e",         0f, 0f, 5f, 1, 0.5f, 1, 0.5f, 0.15f, 5f, 3f, 1f, 1f, 0.08f, 0f, 1f, 1f, 0.2f, 1f),
        T(17, "Sea Sword", "5894e2e7-ab8d-edfa-e61c-18cf16854ba3",        0f, 0f, 6f, 1, 0.7f, 1, 0f, 0f, 2f, 2f, 1f, 1f, 1f, 0f, 0.5f, 0f, 0f, 1f),
        T(18, "Kelp 1", "2caf1179-7861-6ff3-4b7d-46e17780bdfa",           -15f, 0f, 1f, 1, 1f, 3, 2.5f, 0.75f, 1.85f, 4.9f, 1f, 0.04f, 0.05f, 0f, 1f, 2f, 0.8f, 2f),
        T(19, "Beach Grass 1", "18fb888b-e8f1-dce7-7da7-321d651ea6b0",    0f, 0f, 4f, 1, 0.7f, 1, 0f, 0f, 4f, 2.5f, 1.2f, 1f, 1f, 0f, 0.5f, 0f, 0f, 1f),
        T(20, "Kelp 2", "2a4880b6-b7a3-690a-2049-bfbe38eafb9f",           -15f, 0f, 1f, 1, 1f, 3, 2.5f, 1.35f, 2f, 4.9f, 1f, 0.025f, 0.05f, 0f, 1f, 2f, 0.8f, 2f),
    ];

    /// <summary>grass.xml, indexed by species id (0..5). Species 0 and 2 share a texture.</summary>
    public static readonly IReadOnlyList<GrassSpecies> Grasses =
    [
        new(0, "Grass 0", new UUID("6c4727b8-ac79-ba44-3b81-f9aa887b47eb"), 1.35f, 1.35f),
        new(1, "Grass 1", new UUID("79504bf5-c3ec-0763-6563-d843de66d0a1"), 1.0f, 0.66f),
        new(2, "Grass 2", new UUID("6c4727b8-ac79-ba44-3b81-f9aa887b47eb"), 1.8f, 1.8f),
        new(3, "Grass 3", new UUID("99bd60a2-3250-efc9-2e39-2fbcadefbecc"), 1.0f, 1.0f),
        new(4, "Grass 4", new UUID("7a2b3a4a-53c2-53ac-5716-aac7d743c020"), 2.25f, 2.25f),
        new(5, "undergrowth_1", new UUID("8f458549-173b-23ff-d4ff-bfaa5ea2371b"), 2.0f, 2.0f),
    ];

    /// <summary>
    /// The species the viewer actually draws: LLVOTree::processUpdateMessage falls back to the first entry of its
    /// species map (id 0) for an unknown id.
    /// </summary>
    public static TreeSpecies Tree(int id) => id >= 0 && id < Trees.Count ? Trees[id] : Trees[0];

    /// <summary>LLVOGrass::updateSpecies: same fallback to the first species.</summary>
    public static GrassSpecies Grass(int id) => id >= 0 && id < Grasses.Count ? Grasses[id] : Grasses[0];
}
