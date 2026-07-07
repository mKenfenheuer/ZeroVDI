namespace KSol.ZeroVDI.Models;

/// <summary>
/// Per-feature enforcement mode for a redirected device/channel in the browser console.
/// </summary>
public enum DevicePolicyMode
{
    /// <summary>The user (and per-resource defaults) decide; the console checkbox is editable.</summary>
    UserControlled = 0,

    /// <summary>Force the feature OFF everywhere; the console checkbox is unticked and locked.</summary>
    Disabled = 1,

    /// <summary>Force the feature ON everywhere; the console checkbox is ticked and locked.</summary>
    Forced = 2,
}

/// <summary>
/// Tenant-wide, admin-enforced policy for console device/channel redirection (clipboard, audio,
/// microphone, camera). This is the central policy layer over the per-(user, resource) console toggles
/// in <see cref="ConnectionDefaults"/>: where a per-connection toggle is a <em>preference</em>, this is a
/// <em>constraint</em>. The policy is a single settings row (<see cref="Id"/> fixed to
/// <see cref="SingletonId"/>); <c>DevicePolicyService</c> loads/caches it and clamps the effective
/// <see cref="ConnectionDefaults"/> at every enforcement point so a locked feature cannot be re-enabled
/// from the UI or by a crafted save request. See [[connection-defaults-single-option-model]].
/// </summary>
public class DevicePolicy
{
    /// <summary>The only valid primary key — this table holds exactly one row.</summary>
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>Clipboard redirection (cliprdr).</summary>
    public DevicePolicyMode Clipboard { get; set; } = DevicePolicyMode.UserControlled;

    /// <summary>Remote audio playback (rdpsnd).</summary>
    public DevicePolicyMode Audio { get; set; } = DevicePolicyMode.UserControlled;

    /// <summary>Microphone redirection (audin).</summary>
    public DevicePolicyMode Microphone { get; set; } = DevicePolicyMode.UserControlled;

    /// <summary>Camera redirection (rdpecam).</summary>
    public DevicePolicyMode Camera { get; set; } = DevicePolicyMode.UserControlled;

    /// <summary>
    /// Apply this policy mode to a user-requested value: a forced/disabled mode wins, otherwise the
    /// user's choice stands.
    /// </summary>
    public static bool Clamp(DevicePolicyMode mode, bool requested) => mode switch
    {
        DevicePolicyMode.Disabled => false,
        DevicePolicyMode.Forced => true,
        _ => requested,
    };

    /// <summary>Whether a mode locks the corresponding console checkbox (not user-editable).</summary>
    public static bool IsLocked(DevicePolicyMode mode) => mode != DevicePolicyMode.UserControlled;
}
