namespace BamCam.Tests;

/// <summary>
/// Tests that change the global style and tool libraries (CamLibraries). They run on their own, not in parallel
/// with other tests, which would otherwise see libraries appear or vanish while generating toolpaths.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalLibrariesCollection
{
    public const string Name = "Global libraries";
}