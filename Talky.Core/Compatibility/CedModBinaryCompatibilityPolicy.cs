using System;

namespace Talky.Compatibility;

public readonly struct CedModBinaryIdentity
{
    internal CedModBinaryIdentity(
        string release,
        string framework,
        string moduleVersionId,
        string prefixIlSha256,
        string helperIlSha256,
        string artifactSha256)
    {
        Release = release;
        Framework = framework;
        ModuleVersionId = moduleVersionId;
        PrefixIlSha256 = prefixIlSha256;
        HelperIlSha256 = helperIlSha256;
        ArtifactSha256 = artifactSha256;
    }

    public string Release { get; }
    public string Framework { get; }
    public string ModuleVersionId { get; }
    public string PrefixIlSha256 { get; }
    public string HelperIlSha256 { get; }
    public string ArtifactSha256 { get; }
}

/// <summary>
/// Exact CedMod binaries whose Sentinal cache behavior has been reviewed for Talky's rewrite.
/// Artifact hashes are provenance metadata; runtime compatibility is checked without filesystem
/// access by matching the module MVID and IL hashes of both relevant methods.
/// </summary>
public static class CedModBinaryCompatibilityPolicy
{
    private static readonly CedModBinaryIdentity[] Known =
    {
        new CedModBinaryIdentity(
            "3.4.32",
            "LabAPI",
            "6e84e866-4a89-4aa4-9dea-d44e17ab7b86",
            "30086CB8CEC8BF5309F89B6F58F6175F2BAB8D63B75A772EC82DC9B143B4B975",
            "941A7ABA51B95D2FDECBD64D68B6EAD2FFA42134C3FB669B7E93EF56EA34CAEF",
            "67ABB243B626100668675AAD20F3091D482F7AD80A4784D63E1EC0A54F764341"),
        new CedModBinaryIdentity(
            "3.4.32",
            "EXILED",
            "6dbe00fe-70a3-4a36-81fc-aabc3f4a7907",
            "5BF15997EFB812D04EEE15A669363CC7191EB787A1972D80EBA033F555AF190E",
            "FEBA6304521C1F12C46DFEB9789A996A77AB367450882E37A6BD700DF7D60768",
            "80838585D6AE62A8E5704C19CC6C77612AC444F52248F349D2A5CA08E0EC991A"),
    };

    public static bool TryMatch(
        string moduleVersionId,
        string prefixIlSha256,
        string helperIlSha256,
        out CedModBinaryIdentity identity)
    {
        for (int i = 0; i < Known.Length; i++)
        {
            CedModBinaryIdentity candidate = Known[i];
            if (EqualsOrdinalIgnoreCase(candidate.ModuleVersionId, moduleVersionId) &&
                EqualsOrdinalIgnoreCase(candidate.PrefixIlSha256, prefixIlSha256) &&
                EqualsOrdinalIgnoreCase(candidate.HelperIlSha256, helperIlSha256))
            {
                identity = candidate;
                return true;
            }
        }

        identity = default;
        return false;
    }

    private static bool EqualsOrdinalIgnoreCase(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
