using System;
using System.Reflection;
using System.Security.Cryptography;
using Talky.Compatibility;

namespace Talky.Bridges;

internal static class CedModBinaryVerifier
{
    public static bool TryVerify(
        MethodInfo sentinalPrefix,
        MethodInfo helper,
        out CedModBinaryIdentity identity,
        out string detail)
    {
        identity = default;
        detail = "CedMod semantic identity is unavailable";
        if (sentinalPrefix == null || helper == null ||
            !ReferenceEquals(sentinalPrefix.Module, helper.Module))
        {
            return false;
        }

        MethodBody prefixBody = sentinalPrefix.GetMethodBody();
        MethodBody helperBody = helper.GetMethodBody();
        byte[] prefixIl = prefixBody?.GetILAsByteArray();
        byte[] helperIl = helperBody?.GetILAsByteArray();
        if (prefixIl == null || prefixIl.Length == 0 ||
            helperIl == null || helperIl.Length == 0)
        {
            detail = "CedMod semantic identity has no readable method body";
            return false;
        }

        string mvid = helper.Module.ModuleVersionId.ToString("D");
        string prefixHash = Sha256(prefixIl);
        string helperHash = Sha256(helperIl);
        if (!CedModBinaryCompatibilityPolicy.TryMatch(
                mvid,
                prefixHash,
                helperHash,
                out identity))
        {
            detail =
                "CedMod semantic identity is not allowlisted: mvid=" + mvid +
                ", prefix_il_sha256=" + prefixHash +
                ", helper_il_sha256=" + helperHash;
            return false;
        }

        detail =
            "CedMod " + identity.Release + " " + identity.Framework +
            " semantic identity verified; mvid=" + identity.ModuleVersionId;
        return true;
    }

    private static string Sha256(byte[] value)
    {
        using (SHA256 algorithm = SHA256.Create())
        {
            return BitConverter.ToString(algorithm.ComputeHash(value)).Replace("-", string.Empty);
        }
    }
}
