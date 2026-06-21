using System;
using Exiled.API.Enums;
using Exiled.API.Features;
using Exiled.API.Interfaces;
using Talky.Core;
using Talky.Settings;

namespace Talky.Exiled;

/// <summary>
/// EXILED-managed config: inherits every Talky setting and satisfies <see cref="IConfig"/> via
/// Config's virtual IsEnabled/Debug. It lives in this separate (embedded) assembly so Talky.dll
/// never names an EXILED type in its own metadata.
/// </summary>
public sealed class TalkyExiledConfig : Config, IConfig
{
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// The native EXILED plugin. Talky.dll loads this type from its embedded resource and adds the
/// instance to EXILED's loader. Identity and lifecycle are forwarded through <see cref="TalkyBridge"/>,
/// so this assembly needs no compile-time reference back to Talky.dll.
/// </summary>
public sealed class TalkyExiledPlugin : Plugin<TalkyExiledConfig>
{
    public override string Name => TalkyBridge.Name;
    public override string Prefix => TalkyBridge.Prefix;
    public override string Author => TalkyBridge.Author;
    public override Version Version => TalkyBridge.Version ?? base.Version;
    public override PluginPriority Priority => PluginPriority.Default;
    public override Version RequiredExiledVersion { get; } = new Version(9, 14, 2);

    public override void OnEnabled()
    {
        TalkyBridge.Enable?.Invoke(Config);
        base.OnEnabled();
    }

    public override void OnDisabled()
    {
        TalkyBridge.Disable?.Invoke();
        base.OnDisabled();
    }

    public override void OnReloaded()
    {
        OnDisabled();
        OnEnabled();
        base.OnReloaded();
    }

    public override void OnRegisteringCommands()
    {
        // No-op: Talky registers no EXILED commands, and we don't want the base scanning this assembly.
    }
}
