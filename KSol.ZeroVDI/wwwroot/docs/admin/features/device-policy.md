# Device policy

Device policy is a **central, admin-enforced** policy for peripheral redirection in console sessions.
It overrides per-connection and per-resource preferences. Managed at **Admin → Device policy**
(`/admin/device-policy`, Admin only).

## Controlled features

Each of the following has an independent tri-state mode:

- **Clipboard**
- **Audio** (playback)
- **Microphone**
- **Camera**

## Modes

| Mode | Behavior |
|---|---|
| **UserControlled** | The user (and resource defaults) decide; policy does not interfere. |
| **Disabled** | The feature is forced **off** and locked in the console UI. |
| **Forced** | The feature is forced **on** and locked in the console UI. |

## How enforcement works

Policy is applied at four points, so it cannot be bypassed from the browser or by a modified client:

1. **Console defaults** are clamped to the policy before auto-connect.
2. **The console UI** renders locked features as disabled checkboxes with a lock icon.
3. **The save endpoint clamps before persisting**, so a crafted JSON save can't store a
   locked-off feature.
4. **The relay refuses the session at the protocol level.** For every feature set to *Disabled*, the
   gateway inspects the relayed RDP stream: a client that lists the feature's static channel
   (`cliprdr` for clipboard, `rdpsnd` for audio) in its connection request, or that *accepts* the
   feature's dynamic channel when the host offers it (`AUDIO_INPUT` for the microphone,
   `AUDIO_PLAYBACK_DVC` for audio, the `RDCamera_Device_Enumerator` for the camera), is disconnected
   before any channel data can flow. The user sees *"… redirection is disabled by your
   administrator's device policy"*.

The stock browser client never triggers point 4 — it already omits disabled static channels and
rejects disabled dynamic channels — so this only ever affects a tampered or third-party client.
*Forced* (feature on) is a UI lock only; there is nothing to enforce on the wire for a feature the
client is free to ignore.

The policy is cached and invalidated immediately when you save changes, so updates take effect on the
next connection without a restart. Sessions that are already connected keep the channels they
negotiated; disconnect them from **Active sessions** if the change must apply at once.

## Auditing

Policy changes are recorded as `DevicePolicyUpdated` in the [audit log](audit). A session refused by
the relay is recorded as a failed `SessionRejectedPolicy` event (category *Session*) carrying the
feature and the channel name.

## Related

- [Resources](resources) · [Security & MFA](security)
