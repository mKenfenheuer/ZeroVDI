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

Policy is applied at three points, so it cannot be bypassed from the browser:

1. **Console defaults** are clamped to the policy before auto-connect.
2. **The console UI** renders locked features as disabled checkboxes with a lock icon.
3. **The save endpoint clamps before persisting**, so a crafted JSON save can't store a
   locked-off feature.

The policy is cached and invalidated immediately when you save changes, so updates take effect on the
next connection without a restart.

## Boundary / limitation

Enforcement is the locked UI plus the server-side save clamp. The WebSocket relay is currently a
transparent byte tunnel and does **not** yet strip channels at the protocol level. True DLP against a
non-browser client (protocol-level channel stripping) is a planned follow-up.

## Auditing

Policy changes are recorded as `DevicePolicyUpdated` in the [audit log](audit).

## Related

- [Resources](resources) · [Security & MFA](security)
