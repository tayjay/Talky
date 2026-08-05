namespace Talky.Compatibility;

public readonly struct CedModRewriteState<TSync, TOffset>
{
    private CedModRewriteState(TSync previous, TOffset offset)
    {
        IsArmed = true;
        Previous = previous;
        Offset = offset;
    }

    public bool IsArmed { get; }
    public TSync Previous { get; }
    public TOffset Offset { get; }

    public static CedModRewriteState<TSync, TOffset> Capture(
        bool targetActive,
        bool isInvisible,
        bool nearPositioning,
        bool targetEligible,
        bool offsetFinite,
        bool hasPrevious,
        TSync previous,
        TOffset offset)
    {
        if (!targetActive || isInvisible || nearPositioning ||
            !targetEligible || !offsetFinite)
        {
            return default;
        }

        return new CedModRewriteState<TSync, TOffset>(
            hasPrevious ? previous : default,
            offset);
    }
}

public readonly struct CedModRewriteCommit<TSync>
{
    private readonly TSync _value;

    private CedModRewriteCommit(bool updateCache, TSync value)
    {
        UpdateCache = updateCache;
        _value = value;
    }

    public bool UpdateCache { get; }
    public TSync Outgoing => _value;
    public TSync CacheValue => _value;

    public static CedModRewriteCommit<TSync> PassThrough(TSync original) =>
        new CedModRewriteCommit<TSync>(false, original);

    public static CedModRewriteCommit<TSync> Rewrite(TSync rewritten) =>
        new CedModRewriteCommit<TSync>(true, rewritten);
}
