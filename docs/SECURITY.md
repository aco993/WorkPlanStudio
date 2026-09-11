# Security posture

**English** · [Deutsch](SECURITY.de.md)

WorkPlan Studio ships in two shapes, and they have different security stories.

- **The published demo** — <https://aco993.github.io/WorkPlanStudio/> — is a static
  site with no server, no accounts and no shared storage. It has no authorization
  boundary and no trusted storage, and this document says so in more detail below.
- **An optional backend** (`src/WorkPlanStudio.Api`) exists and has real accounts,
  real password hashing and real token handling. It is **off unless configured**,
  and the published demo does not configure it.

Nothing below is a claim that the demo protects data. It does not.

## Data and trust boundaries (the static build)

- Work plans, master data and settings stay in the current browser origin's
  `localStorage`.
- `localStorage` is readable by any JavaScript on that origin. It is not a secret
  vault and is inappropriate for shared machines or valuable credentials.
- Database payloads are untrusted on startup. Version, Base64 shape, SQLite
  header, minimum size, `PRAGMA quick_check` and expected schema access are all
  verified before the UI is enabled.
- Application code issues fixed EF-generated queries; it does not accept SQL from
  the user.
- Storage incompatibility or corruption never silently reseeds over the old
  payload. The recovery screen supports export, import and an explicit two-step
  reset — and the reset and the import both ask the authorization policy, because
  replacing every row is a mutation whatever the call site looks like.

### Imported and uploaded content

The CSV import reads a file the visitor chose, which is the one place untrusted
bytes enter the application. It refuses a file over 8 MB at the door, refuses a
file containing a NUL byte as "not a text file", bounds a field at 64 KB, a row at
1 024 columns and a file at 200 000 records, and refuses an unterminated quote by
line number. Values go through the same validators the forms use, which reject
control characters in names, codes and order numbers. Nothing imported is ever
executed, and the exported CSV escapes leading `=`, `+`, `-` and `@` so a
spreadsheet does not interpret a cell as a formula.

## Personas are not access control

The static build offers three personas — planner, supervisor, guest — through the
standard ASP.NET Core authorization pipeline (policies, `AuthorizeView`, a guard on
every mutating service, enforced as a closed set by a reflection test). The identity
behind them is a persona chosen in the UI and stored in `localStorage`; its
authentication type is `demo-persona`. Anyone can be the planner by choosing to,
and the code enforcing the policies runs in the visitor's own browser.

This demonstrates authorization plumbing and its seam for a real identity provider
([ADR 0013](adr/0013-personas-through-the-real-authorization-pipeline.md)); **it
protects nothing**.

That seam is no longer only a claim. When `Api:BaseAddress` is configured, the same
policy table — the *same source file*, compiled into both hosts — is enforced by a
server that owns the data, against a principal from a JWT it issued, and the persona
switcher disappears from the UI. See
[ADR 0020](adr/0020-optional-backend-and-real-auth.md). The published demo is still
the persona build.

## The optional backend

When it is deployed, `src/WorkPlanStudio.Api` does the following, and the
88-test integration suite exercises each one against a real SQLite file:

- **Identity** for the user store and password hashing; accounts lock out after a
  configured number of failures, and a lockout defeats even the correct password.
- **JWT access tokens** (10 minutes by default) with **refresh tokens** of 256
  bits from `RandomNumberGenerator`, stored only as SHA-256, rotated on every
  exchange. Presenting a spent refresh token revokes every live session of that
  account — reuse is treated as theft, not as a retry.
- An unknown account answers **byte-for-byte like a wrong password**, so the login
  endpoint does not enumerate users.
- **Start-up refuses** a signing key that is missing, shorter than 32 bytes, or the
  development sample outside Development. It is options validation resolved before
  the database is touched, so the host does not start half-configured.
- Fixed-window **rate limiting** on the auth routes; registration is off by default
  and additionally requires a privileged caller when on.
- **CORS from a configured origin list**, never `AllowAnyOrigin` with credentials —
  and no credentials are used at all, because the token travels in a bearer header
  rather than a cookie.
- **ProblemDetails** (RFC 9457) on every failure, a 256 KB request-body limit,
  security headers on every response, response compression with
  `EnableForHttps = false` (BREACH), and `/health` + `/health/ready`.
- A multi-stage **Dockerfile** running as a non-root user with no secret baked in.
- **Optimistic concurrency**: a write without a concurrency stamp is refused, and a
  stale one is a 409 rather than a silent overwrite.

What it deliberately does not do: multi-tenancy, an offline write queue, conflict
merging beyond 409, or two-way sync. Master data is pulled one way; the browser
never sends its local rows back, and the UI says so rather than faking
last-write-wins.

## Optional models (bring your own key)

The core application, the deterministic explanation and the on-device chat work
without any model. If a provider is enabled:

- the key is stored in browser `localStorage`, **under a name scoped to the
  provider** (`assistant.key.<Provider>`) and separately from the rest of the
  assistant settings, which carry `[JsonIgnore]` on the key so it cannot ride
  along in the settings blob. One shared key field for three providers meant that
  switching provider pointed the key entered for one host at a different host;
  it no longer does;
- the settings dialog **never reads the key back into the page**. The field is
  empty when the dialog opens and a blank field means "keep the stored key", so
  the secret does not re-enter the DOM where any script on the origin could read
  it. "Forget this key" is one click;
- the residual risk is stated in the dialog itself, not only here: storage on this
  origin is readable by script, so do not enable a model on a shared computer;
- nothing in the assistant is logged, at all, and two source-scanning tests keep
  it that way;
- only an absolute HTTPS endpoint is accepted; HTTP is allowed only for loopback
  development. User-info, query and fragment components are rejected to reduce
  accidental credential routing;
- **the model name is validated and escaped too.** It is a path segment in the
  Gemini API, so an unvalidated one could rewrite the request target — the same
  hole the endpoint rules were written to close, reopened through a different
  field. It must now match `^[A-Za-z0-9._:@-]{1,100}$` and is percent-escaped at
  the point of use;
- the key travels in the header each provider expects (`Authorization: Bearer`,
  `x-api-key`, `x-goog-api-key`), never in the URL. An API key containing a
  control character or a non-ASCII character is rejected, because
  `TryAddWithoutValidation` is exactly the API that would let a `\r\n` through;
- calls go from the browser to the provider. Anthropic requires an explicit
  `anthropic-dangerous-direct-browser-access` header for that, which the client
  sends; the name is the warning, and it is why the key is the user's own and
  never shipped with the app;
- **one 20-second budget covers the whole exchange**, the request *and* the
  reading of the response body. It used to cover only the send, so a body that
  never finished arriving hung past the budget; a linked token now governs both.
  Caller cancellation stays distinct from a timeout;
- a response larger than **1 MiB** is refused rather than buffered, and an
  oversized `Content-Length` is refused before a byte is read;
- **every** provider failure falls back to the on-device answer with a note. Not a
  list of seven exception types — every one, including the malformed bodies that
  used to arrive as a `NullReferenceException`;
- only structured schedule facts, the conversation and the on-device answer are
  sent, and those facts are **fenced as data**: wrapped in delimiters, stripped of
  markup characters, bounded (40 orders, 20 work centres, 12 operations per
  centre, 16 000 characters) with each cut stated in the prompt so a truncated
  list cannot be read as the whole shop, and followed by a restatement that the
  fenced region is data and never an instruction.

**Prompt injection is reduced, not solved.** The content inside that fence is free
text a user typed into part names, work-centre names and order references. The app
cannot promise "no personal data" about a field a person can type anything into;
it can promise that the field is sanitised, bounded, fenced and never given
authority over the instruction. See
[ADR 0025](adr/0025-hostile-input-on-the-model-path.md).

A production design should put the provider behind a backend proxy, keep the key in
server-side secret storage, enforce tenant authorization and add audit and rate
controls.

## Tracked SQLite advisory — SEC-001, exit criterion met

**Status on 2026-09-11: the advisory no longer applies, and the suppression is now
obsolete.**

The history: `Microsoft.EntityFrameworkCore.Sqlite` used to bring
`SQLitePCLRaw.lib.e_sqlite3` **2.1.11**, for which NuGet audit reported
[GHSA-2m69-gcr7-jv3q / CVE-2025-6965](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)
as high severity — memory corruption in SQLite before 3.50.2 involving excessive
aggregate terms. It was accepted rather than fixed, with a written justification
and a falsifiable exit criterion, and suppressed as a single advisory in
`Directory.Build.props` so that every *other* NuGet advisory still failed the audit.

The packages are now at `Microsoft.EntityFrameworkCore.Sqlite` **10.0.11**, which
brings `SQLitePCLRaw` **2.1.12**. Measured against a throwaway project that
references EF 10.0.11 and carries **no suppression at all**:

```
dotnet list package --vulnerable --include-transitive
  → no vulnerable packages for the given project
```

SEC-001's exit criterion was: *"`dotnet list WorkPlanStudio.slnx package
--vulnerable --include-transitive` no longer reports the advisory with a supported
package graph, all SQLite/WASM/E2E tests pass, and the suppression is removed in
the same change."* The first two are met — the graph is clean and every suite is
green on this tip, and the third is met too: the `NuGetAuditSuppress` line is gone
from `Directory.Build.props`. A restore with the NuGet audit fully strict and
nothing suppressed reports no advisory, and the solution builds warning-free —
which is the same check, because warnings are errors here.

**SEC-001 is closed.** The record stays because a risk acceptance with a
falsifiable exit criterion is only worth anything if the exit is written down
when it happens.

## Reporting

Do not put secrets or exploit payloads in a public issue. Use GitHub's private
vulnerability reporting if enabled for the repository; otherwise contact the
repository owner privately. See [SECURITY.md](../SECURITY.md) at the repository
root for what is in scope and what is documented design rather than a defect.
