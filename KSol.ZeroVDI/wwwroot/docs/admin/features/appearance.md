# Appearance & branding

Appearance is a **tenant-wide** policy for how the interface looks: an optional forced color theme,
forced light/dark mode, and a custom company logo. Managed at **Admin → Appearance**
(`/admin/appearance`, Admin only).

By default each user picks their own color theme and light/dark mode (remembered in their browser).
When you force a theme and/or mode, your choice is applied for everyone — before first paint, so there
is no flash of the wrong theme — and the in-app switcher is hidden so users can't change it.

## Color theme

Turn on **Force a color theme** to apply one theme to all users. You can choose:

- **Preset theme** — one of the built-in palettes (Neutral, Blue, Green, Orange, Rose, Slate, Sky,
  Azure).
- **Custom color** — pick any color and the whole UI is re-derived from it.

### How a custom color works

A custom color is more than just the primary button color. The picked color's **hue** drives an entire
matching palette — primary, the faint tinted surfaces (secondary, muted, accent), their foregrounds,
borders and inputs, the focus ring, and the sidebar — generated for **both light and dark mode**, the
same way the built-in presets are constructed. The text-on-primary color flips between near-black and
near-white automatically for legibility.

Only the hue (and a clamped saturation) carry over; the primary's lightness is fixed so a very pale or
very dark pick still renders as a usable brand color rather than washing out the interface.

## Light / dark mode

Turn on **Force light / dark mode** to lock every user into Light or Dark. When off, users keep their
own toggle.

## Company logo

Replace the built-in wordmark with your own logo (SVG, PNG, JPG, WEBP or GIF, up to 2 MB).

- **Light / default logo** — shown in light mode, and in dark mode too unless a dark logo is set.
- **Dark-mode logo** *(optional)* — shown when the interface is in dark mode. Add this when your logo
  needs different colors to stay legible on a dark background. If not set, the light logo is used in
  both modes. The dark-logo option appears only once a light logo is uploaded.

Both logos swap instantly when the mode changes — no page reload — and removing the light logo also
clears the dark one (a dark-only logo would leave the default wordmark in light mode).

## How it's applied

Appearance is cached and invalidated immediately on save, so changes take effect on the next page load
for all users without a restart. Forced theme/mode values are rendered server-side into the page
`<head>` before any content paints.

## Auditing

Changes are recorded in the [audit log](audit) as `AppearanceUpdated`, `AppearanceLogoUpdated`, and
`AppearanceLogoRemoved`.

## Related

- [Security & MFA](security) · [Users & groups](../administration/users-and-groups)
