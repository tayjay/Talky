using System;
using Talky.Settings;

namespace Talky.Core;

/// <summary>
/// Shares plugin identity and lifecycle callbacks with the EXILED shim.
/// </summary>
public static class TalkyBridge
{
    public static string Name = "Talky";
    public static string Prefix = "talky";
    public static string Author = "TayTay";
    public static Version Version;

    public static Action<Config> Enable;

    public static Action Disable;
}
