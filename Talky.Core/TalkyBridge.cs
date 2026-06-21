using System;
using Talky.Settings;

namespace Talky.Core;

/// <summary>
/// Decouples Talky's runtime from the EXILED shim.
/// </summary>
public static class TalkyBridge
{
    /// <summary>Plugin identity, populated by Talky.dll at startup.</summary>
    public static string Name = "Talky";
    public static string Prefix = "talky";
    public static string Author = "TayTay";
    public static Version Version;

    /// <summary>Invoked by the EXILED shim's OnEnabled, passing the EXILED-loaded config.</summary>
    public static Action<Config> Enable;

    /// <summary>Invoked by the EXILED shim's OnDisabled.</summary>
    public static Action Disable;
}
