# Kiro

Tracks your Kiro subscription usage using the login you already have from the Kiro IDE or `kiro-cli`.

## What it tracks

| Metric | Meaning |
|---|---|
| Requests | Agentic-request usage for the current billing period vs. your plan's included allowance |
| Bonus Credits | Any bonus credit grant Kiro has attached to your account, with its own usage and expiry |
| Extra Usage | Pay-as-you-go overage spend once your included allowance runs out |

When Kiro reports your subscription name (e.g. "KIRO PRO+"), AIUsage.NET shows it beside the
provider name.

Extra Usage shows differently depending on your account's overage setting:

- **Overage on, with a spending cap** — a dollar meter (spent vs. cap).
- **Overage on, no cap declared** — a plain "$X.XX spent" badge; there's nothing to divide a meter by.
- **Overage off, or the account isn't overage-capable** — a "Disabled" badge, and no charge risk.

## Where credentials come from

AIUsage.NET never asks for a token — it reads whichever of these is present, desktop file first:

1. **Kiro IDE** — `~/.aws/sso/cache/kiro-auth-token.json`. Kiro borrows the AWS SSO cache directory
   for this file, but it isn't a real SSO cache entry: it's a plain JSON file the IDE writes and
   refreshes itself (`accessToken`, `refreshToken`, `profileArn`, `region`). Enterprise/IdC logins add
   a `clientIdHash` pointing at a sibling device-registration file in the same directory, which
   AIUsage.NET reads for context but doesn't need to refresh (the desktop endpoint only takes the
   refresh token).
2. **`kiro-cli`** — its SQLite database at `%LOCALAPPDATA%\Kiro-Cli\data.sqlite3` (the Windows
   counterpart of `~/.local/share/kiro-cli/data.sqlite3` on Linux/macOS), table `auth_kv`. Social
   logins are stored under `kirocli:social:token`; AWS SSO OIDC logins (Builder ID, IAM Identity
   Center) under `kirocli:odic:token` with a paired `kirocli:odic:device-registration` entry holding
   the `clientId`/`clientSecret` needed to refresh. Legacy `codewhisperer:odic:*` keys are read too.
   The resolved CodeWhisperer profile ARN is cached separately in the `state` table
   (`api.codewhisperer.profile`) and read from there when present.

Nothing extra to install or configure — signing in through either surface is enough.

### Two regions, not one

Kiro's SSO/auth region (where you logged in) and its CodeWhisperer data-plane region (where your
account's usage actually lives) can differ — on this maintainer's own account, the SSO region is
`eu-west-1` while the profile's region is `eu-central-1`, and `eu-west-1` has no
`q.eu-west-1.amazonaws.com` data-plane host at all. AIUsage.NET always calls the data-plane host
derived from the resolved profile ARN, falling back to `us-east-1` only when no profile ARN is known
yet — never the SSO region. The SSO region is used exclusively to reach the token-refresh endpoint.

## Troubleshooting

- **"Not logged in"** — sign in to the Kiro IDE, or run `kiro-cli login`, then refresh.
- **"Session expired"** — both the desktop and CLI refresh tokens are single-use; if a different
  tool (or a second copy of AIUsage.NET) already redeemed the refresh token this account had stored,
  the next refresh attempt fails and the affected app should show a login prompt on its own next
  launch. Sign in again with whichever surface you use day to day.
- **Bonus Credits missing** — not every account has a bonus grant; the row simply doesn't appear
  when Kiro's response carries none.

## Under the hood

> Reverse-engineered, undocumented API. Endpoints and payload shapes may change without notice.

`GET /getUsageLimits?origin=AI_EDITOR&resourceType=AGENTIC_REQUEST&isEmailRequired=true&profileArn=...`
against the CodeWhisperer REST host in `us-east-1`
(`https://codewhisperer.us-east-1.amazonaws.com`) or the regional Amazon Q host elsewhere
(`https://q.{region}.amazonaws.com`) — both expose the same operation. The response's
`usageBreakdownList` (per-resource `currentUsage`/`usageLimit`, plus any `bonuses`) and
`subscriptionInfo.subscriptionTitle` map directly to the Requests meter, Bonus Credits row, and plan
name. The same breakdown entries carry `overageCap`/`overageRate`/`currentOverages`, gated by
`overageConfiguration.overageStatus`, which map to the Extra Usage row. When no profile ARN is
cached yet, `POST /ListAvailableProfiles` resolves one before the usage call. Token refresh is
desktop-flavored (`POST https://prod.{region}.auth.desktop.kiro.dev/refreshToken` with just a
refresh token) or AWS SSO OIDC (`POST https://oidc.{region}.amazonaws.com/token` with
`clientId`/`clientSecret`/`refreshToken`), matching whichever credential source answered.
