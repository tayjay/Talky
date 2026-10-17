using System;
using Talky.Settings;

namespace Talky.Core;

/// <summary>
/// Shares plugin identity and lifecycle callbacks with the EXILED shim.
/// </summary>
public static class TalkyBridge
{
    private static readonly object LifecycleLock = new object();
    private static object _boundInstance;
    private static object _lifecycleOwner;

    public static string Name = "Talky";
    public static string Prefix = "talky";
    public static string Author = "TayTay";
    public static Version Version;

    public static Action<Config> Enable;

    public static Action Disable;

    /// <summary>
    /// Reports active or incompletely released runtime ownership across assembly reloads.
    /// Merely loading or constructing a plugin assembly does not acquire ownership.
    /// </summary>
    public static bool HasLifecycleOwner
    {
        get
        {
            lock (LifecycleLock)
                return _lifecycleOwner != null;
        }
    }

    /// <summary>Binds an inactive plugin instance without replacing an active lifecycle.</summary>
    public static bool TryBindLifecycle(
        object instance,
        Action<Config> enable,
        Action disable)
    {
        if (instance == null)
            throw new ArgumentNullException(nameof(instance));
        if (enable == null)
            throw new ArgumentNullException(nameof(enable));
        if (disable == null)
            throw new ArgumentNullException(nameof(disable));

        lock (LifecycleLock)
        {
            if (_lifecycleOwner != null)
                return false;

            _boundInstance = instance;
            Enable = enable;
            Disable = disable;
            return true;
        }
    }

    /// <summary>Acquires ownership only for the instance whose callbacks are currently bound.</summary>
    public static bool TryAcquireLifecycle(object instance)
    {
        if (instance == null)
            throw new ArgumentNullException(nameof(instance));

        lock (LifecycleLock)
        {
            if (!ReferenceEquals(_boundInstance, instance))
                return false;
            if (_lifecycleOwner != null)
                return ReferenceEquals(_lifecycleOwner, instance);

            _lifecycleOwner = instance;
            return true;
        }
    }

    public static bool IsLifecycleOwner(object instance)
    {
        lock (LifecycleLock)
            return instance != null && ReferenceEquals(_lifecycleOwner, instance);
    }

    /// <summary>Releases a fully cleaned lifecycle; stale instances cannot release a successor.</summary>
    public static bool ReleaseLifecycle(object instance)
    {
        lock (LifecycleLock)
        {
            if (instance == null || !ReferenceEquals(_lifecycleOwner, instance))
                return false;

            _lifecycleOwner = null;
            return true;
        }
    }
}
