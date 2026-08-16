using System;
using Exiled.API.Enums;
using Exiled.API.Features;
using Exiled.API.Interfaces;
using Talky.Core;
using Talky.Settings;

namespace Talky.Exiled;

/// <summary>
/// Provides Talky's EXILED-facing configuration.
/// </summary>
public sealed class TalkyExiledConfig : Config, IConfig
{
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// Forwards EXILED plugin identity and lifecycle through <see cref="TalkyBridge"/>.
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
        // Skip command scanning because Talky registers no EXILED commands.
    }
}
