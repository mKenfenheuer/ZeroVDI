# Identity federation (single sign-on)

ZeroVDI can delegate authentication to an OpenID Connect provider — Entra ID, Keycloak, Okta,
Authentik, Google Workspace, or anything else that speaks standard OIDC. Users then sign in with
their corporate identity, accounts are created on first sign-in, and directory groups drive who gets
which desktops.

Federation is **off by default**. With the `Oidc` section absent, ZeroVDI is a local-accounts-only
deployment and nothing on the sign-in page changes.

## Enabling it

Register ZeroVDI at your provider as a **confidential client** using the **authorization code** flow
with PKCE, and set the redirect URI to `https://your-zerovdi-host/signin-oidc`. Then:

```json
"Oidc": {
  "Enabled": true,
  "Authority": "https://login.example.com/realms/zerovdi",
  "ClientId": "zerovdi",
  "ClientSecret": "…",
  "DisplayName": "Company SSO",
  "Scopes": [ "profile", "email", "groups" ],
  "GroupsClaim": "groups",
  "AutoProvision": true,
  "RequireMappedGroup": false,
  "AdminGroups": [ "vdi-admins" ],
  "AuditorGroups": [ "vdi-auditors" ],
  "SatisfiesMfa": true
}
```

Also set [`App:PublicBaseUrl`](../reference/configuration-keys) to the address users reach ZeroVDI
on. Behind a reverse proxy the redirect URI is otherwise derived from the request host, which has to
match what you registered at the provider exactly.

A **Sign in with …** button appears under the password form once the configuration is complete. Local
passwords keep working alongside it; see [Turning local sign-in off](#turning-local-sign-in-off).

## Provisioning

| Situation | What happens |
|---|---|
| First sign-in, no matching account | An account is created with the provider's e-mail, already confirmed (`AutoProvision: true`). |
| First sign-in, an account with that e-mail exists | The identity is linked to it — the user keeps every existing grant and stored credential. |
| Later sign-ins | Matched on the provider's stable subject, so a renamed mailbox doesn't create a duplicate. |
| `AutoProvision: false` | Only accounts an administrator created in advance can sign in; everyone else is refused with an explanation. |

Federated accounts have no ZeroVDI password. They can still set one from **Account → Password** if
you want a fallback.

## Group and role mapping

The values of the `GroupsClaim` are matched — **case-insensitively, by name** — against
[user groups](../administration/users-and-groups) that already exist in ZeroVDI. Keycloak-style paths
(`/eng/vdi-users`) also match on their last segment (`vdi-users`).

- A match **adds** the user to that ZeroVDI group, so every resource the group is granted appears on
  their dashboard.
- The provider **never creates groups**. Create a ZeroVDI group with the directory group's name to
  make that directory group mean something; leave it uncreated to ignore it. This keeps a directory of
  thousands of groups from flooding the admin UI.
- Only memberships federation itself created are withdrawn when the directory stops asserting them. A
  membership an administrator added by hand is never removed by a sign-in.
- `AdminGroups` / `AuditorGroups` grant and revoke the corresponding [role](../reference/roles). Leave
  a list empty to manage that role entirely by hand — an empty list never revokes anything.
- `RequireMappedGroup: true` turns the claim into an access gate: an identity in none of the mapped
  groups is refused sign-in rather than admitted with an empty dashboard.

Every provisioning and mapping decision is [audited](audit): `ExternalUserProvisioned`,
`ExternalGroupsSynced`, `ExternalRoleSynced`, `ExternalLoginRejected`.

## MFA

Most deployments federate precisely because the provider already enforces MFA. With
`SatisfiesMfa: true` (the default) a federated session satisfies ZeroVDI's own
[MFA requirement](security#policy), so users are not pushed through a second enrollment. Set it to
`false` to demand a ZeroVDI second factor on top of the provider's.

## Turning local sign-in off

There is no switch that hides the password form. If you want federation to be the only way in, remove
or disable the local accounts you don't need and keep one break-glass administrator with a strong
password and MFA — an identity provider outage should not lock you out of your own desktops.

## Signing out

**Sign out** ends the ZeroVDI session only; the provider's session is untouched, so clicking the SSO
button again may sign the user straight back in. Ending the provider session is a provider-side
action (RP-initiated logout is not wired up).

## Related

- [Users & groups](../administration/users-and-groups) · [Access control](access-control) ·
  [Security & MFA](security) · [Configuration keys](../reference/configuration-keys)
