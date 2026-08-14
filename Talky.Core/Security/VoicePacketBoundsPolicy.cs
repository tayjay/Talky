namespace Talky.Security;

/// <summary>
/// Describes why an inbound voice payload is or is not safe to pass to the native decoder.
/// </summary>
public enum VoicePacketValidationResult
{
    Valid = 0,
    MissingData = 1,
    NonPositiveLength = 2,
    LengthExceedsBuffer = 3,
    LengthExceedsProtocolMaximum = 4,
}

/// <summary>
/// Enforces the game voice protocol's packet boundary before managed data reaches native Opus.
/// </summary>
public static class VoicePacketBoundsPolicy
{
    public const int MaximumPacketBytes = 512;

    public static VoicePacketValidationResult Validate(byte[] data, int length)
    {
        if (data == null)
            return VoicePacketValidationResult.MissingData;
        if (length <= 0)
            return VoicePacketValidationResult.NonPositiveLength;
        if (length > data.Length)
            return VoicePacketValidationResult.LengthExceedsBuffer;
        if (length > MaximumPacketBytes)
            return VoicePacketValidationResult.LengthExceedsProtocolMaximum;

        return VoicePacketValidationResult.Valid;
    }
}
