# Troubleshooting

A few common issues and how to fix them. If none of these help, contact your administrator and
mention the desktop name and roughly when the problem happened.

## The console won't open / stays blank

- Make sure pop-ups aren't blocked — the console opens in a **new tab**. Allow pop-ups for the
  ZeroVDI site and click **Connect** again.
- If the desktop was stopped, it may still be **powering on**. Wait a minute and retry.
- Pools create a personal desktop on first connect, which can take a little longer the first time.

## "Connecting…" then nothing happens

- Reload the dashboard and click **Connect** again.
- Check your internet connection; a brief drop can stall the first connection.
- If it persists, the desktop may be off or unreachable — let your administrator know.

## I keep getting asked for a username and password

That desktop doesn't have your sign-in stored. Save it once and you won't be asked again — see
[Saving your credentials](saving-credentials.md). If you *are* entering credentials and they're
rejected, double-check them with whoever manages that desktop.

## Copy and paste doesn't work

- Confirm **Clipboard** is enabled in [Connection settings](connection-settings.md).
- Your browser may have blocked clipboard access — click the padlock / site-info icon in the address
  bar, allow clipboard, and reconnect.

## No sound, or my mic/camera isn't detected

- Enable **Audio**, **Microphone**, or **Camera** in [Connection settings](connection-settings.md).
- Allow the browser permission prompt. If you denied it before, re-allow it from the address-bar
  padlock and reconnect.

## The screen froze or went black

Your network may have dropped. Close the console tab and click **Connect** again from the
dashboard — your session is usually still running where you left it.

## I can't sign in to ZeroVDI at all

- Double-check your username and password.
- If you use two-factor authentication, make sure the code is current (they expire quickly) and your
  phone's clock is accurate. Out of codes? Use a recovery code — see [Your account](your-account.md).
- Still stuck? Contact your administrator to reset your account.
