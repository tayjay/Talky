using System;

namespace Talky.Security;

public enum GrabAdmission
{
    Released = 0,
    DuplicatePress = 1,
    Throttled = 2,
    Accepted = 3,
    InvalidTime = 4,
}

public enum EmotionAdmission
{
    InvalidSelection = 0,
    Duplicate = 1,
    Applied = 2,
    Coalesced = 3,
    InvalidTime = 4,
}

/// <summary>
/// Per-player admission state for client-controlled server-specific settings. The state is
/// intentionally independent from Unity and networking so all edge/cooldown behavior can be
/// tested deterministically.
/// </summary>
public sealed class PlayerSettingAdmission
{
    public const double GrabAnimationSeconds = 1.111d;
    public const double EmotionApplyIntervalSeconds = 0.5d;
    public const int MinimumEmotionIndex = 0;
    public const int MaximumEmotionIndex = 6;

    private bool _grabPressed;
    private double _nextGrabAt;
    private bool _hasAppliedEmotion;
    private int _lastAppliedEmotion;
    private bool _hasPendingEmotion;
    private int _pendingEmotion;
    private double _nextEmotionAt;

    public bool HasPendingEmotion => _hasPendingEmotion;
    public double NextEmotionAt => _nextEmotionAt;

    public GrabAdmission ObserveGrab(bool pressed, double nowSeconds)
    {
        if (!IsFinite(nowSeconds) || nowSeconds < 0d)
            return GrabAdmission.InvalidTime;

        if (!pressed)
        {
            _grabPressed = false;
            return GrabAdmission.Released;
        }

        if (_grabPressed)
            return GrabAdmission.DuplicatePress;

        _grabPressed = true;
        if (nowSeconds < _nextGrabAt)
            return GrabAdmission.Throttled;

        _nextGrabAt = nowSeconds + GrabAnimationSeconds;
        return GrabAdmission.Accepted;
    }

    public EmotionAdmission ObserveEmotion(int selection, double nowSeconds)
    {
        if (selection < MinimumEmotionIndex || selection > MaximumEmotionIndex)
            return EmotionAdmission.InvalidSelection;
        if (!IsFinite(nowSeconds) || nowSeconds < 0d)
            return EmotionAdmission.InvalidTime;

        if (_hasAppliedEmotion && selection == _lastAppliedEmotion)
        {
            // The newest client choice is already active, so it also cancels a previously queued
            // intermediate choice.
            _hasPendingEmotion = false;
            return EmotionAdmission.Duplicate;
        }

        if (_hasPendingEmotion && selection == _pendingEmotion)
            return EmotionAdmission.Duplicate;

        if (nowSeconds >= _nextEmotionAt)
        {
            CommitEmotion(selection, nowSeconds);
            return EmotionAdmission.Applied;
        }

        _pendingEmotion = selection;
        _hasPendingEmotion = true;
        return EmotionAdmission.Coalesced;
    }

    public bool TryTakePendingEmotion(double nowSeconds, out int selection)
    {
        selection = default;
        if (!_hasPendingEmotion || !IsFinite(nowSeconds) || nowSeconds < _nextEmotionAt)
            return false;

        selection = _pendingEmotion;
        CommitEmotion(selection, nowSeconds);
        return true;
    }

    public void Reset()
    {
        _grabPressed = false;
        _nextGrabAt = 0d;
        _hasAppliedEmotion = false;
        _lastAppliedEmotion = default;
        _hasPendingEmotion = false;
        _pendingEmotion = default;
        _nextEmotionAt = 0d;
    }

    private void CommitEmotion(int selection, double nowSeconds)
    {
        _lastAppliedEmotion = selection;
        _hasAppliedEmotion = true;
        _hasPendingEmotion = false;
        _nextEmotionAt = nowSeconds + EmotionApplyIntervalSeconds;
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);
}
