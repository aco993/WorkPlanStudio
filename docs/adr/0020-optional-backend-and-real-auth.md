# 20. An optional backend, and the first real authentication

- **Status:** Accepted
- **Date:** 2026-09-11
- **Extends:** [ADR 0013](0013-personas-through-the-real-authorization-pipeline.md)

## Context

ADR 0013 put the personas through the real ASP.NET Core authorization
pipeline and ended with a sentence that has been true ever since:
**this is not security**. The policies are genuine, the guard at the
service boundary is genuine, and the identity behind them is a dropdown.
`docs/SECURITY.md` says so in as many words, and the open item it names
is the only one that closes the gap: a server that owns the data and
re-checks every write against a verified identity.

The constraint that produced the persona has not gone away. The app is
published as static files to GitHub Pages, which cannot host a backend,
cannot set a response header and cannot keep a secret. Anything that
makes the browser build *require* a server destroys the thing the
project is actually about — a scheduling tool that runs entirely in a
browser, openable by anyone with a link.

So the question is not "backend or no backend". It is: can real
authentication be added without the offline build noticing?

Three shapes were considered.

1. **Replace the in-browser database with an API.** Honest, conventional,
   and it ends the demo: no server, no app. Every reader who just wants
   to click a link and see a Gantt chart gets nothing.
2. **A hosted Blazor Web App with cookie authentication.** The
   comfortable ASP.NET answer — `HttpOnly` cookies, no token in
   JavaScript's reach, CSRF handled by the framework. It requires the
   app and the API to be one deployment on one origin, which is exactly
   what a static host cannot be.
3. **Leave the browser app alone and add an optional server beside it.**
   Offline stays the default and the published build; configuring an API
   address swaps the identity provider and nothing else.

## Decision

Option 3.

### The API is optional, and offline is the default

`src/WorkPlanStudio.Api` is a separate ASP.NET Core application that the
browser project does not reference. The client reads
`Api:BaseAddress` from `wwwroot/appsettings.json` — **a file that is
absent by default**, so the published build fetches a 404, finds no
address, and registers exactly what it registered before. The switch is
one method, `AddOptionalApi`, appended at the end of the client's
registrations; with nothing configured it adds a single value object.
`RemoteAuthTests` asserts that, because "we did not change the default
path" is a claim that rots the moment nobody checks it.

Connected mode is deliberately modest and says so: it reads the server's
master data into the local store, runs schedules through the server, and
puts a real account in the top bar. It does not sync.

### One authorization model, not two

The API compiles the browser application's own `Services/Auth/Permissions.cs`
— the same file, linked into the project — so `ManageMasterData` means
the same thing on both sides by construction rather than by convention.
The entities, the three validators and `ScheduleMapper` are linked for
the same reason: a server and a client that disagree about what a valid
work plan is will disagree quietly, at the worst possible moment.

Two things could not be linked and are duplicated with a test guarding
each: the persistence mapping (the API's context is an
`IdentityDbContext` and carries a concurrency token the browser has no
use for) and `PlantSettingsValidator`, which shares a file with a service
that writes to browser storage.

### JWT with refresh tokens, not cookies

A cookie session would be the safer default and it is not available
here. The client and the API are on different origins, the client is
served by a host that sets no headers, and there is no same-site proxy
to put in between. A cross-origin cookie would have to be `SameSite=None`
and would need CSRF defences the static host cannot help with.

So: a short-lived signed access token in the `Authorization` header, and
a long-lived refresh token that is rotated on every exchange. The server
stores only the SHA-256 of a refresh token, retires it on first use, and
treats a second use as theft — ending every live session of that account,
because the legitimate holder and whoever else has a copy cannot be told
apart.

**What this costs, stated plainly.** A token the browser can attach to a
request is a token injected JavaScript can steal. That is the price of
the cross-origin shape, and no browser storage avoids it — a token held
in a variable is as reachable from injected script as one in
`sessionStorage`. The exposure is bounded instead of denied:

- the access token is held in memory and never written to storage;
- the refresh token lives in `sessionStorage`, which dies with the tab,
  rather than `localStorage`, which does not;
- it is single-use, so a stolen copy is useful for one exchange and then
  announces itself;
- the access token's lifetime is ten minutes by default, which is how
  long a stolen one stays useful, since a self-contained token cannot be
  withdrawn;
- the app's Content-Security-Policy already forbids third-party script,
  which is the delivery mechanism this is defending against.

A deployment that can put the API and the client on one origin should
use cookies instead. This design is what a static host allows, not what
is best in the abstract.

### Identity, not hand-rolled auth

Password storage looks like a solved problem and is a minefield of
iteration counts, salting and constant-time comparison. ASP.NET Core
Identity owns the user store, the password hashing and the lockout
counters. The only cryptography written here is
`RandomNumberGenerator.GetBytes(32)` for token material and `SHA256` for
storing it — a fast hash is correct for a 256-bit random value with no
structure to attack, unlike a password.

### The configuration refuses to be wrong

The signing key comes from configuration and the host **throws on start**
when it is missing, shorter than 32 bytes, or still the development
sample published in this repository and running outside Development. A
warning would have been the friendlier choice and the wrong one: an API
that starts without a usable key is not degraded, it accepts tokens
anyone can mint, and a start-up warning is how that goes unnoticed for a
year. The checks run as options validation rather than as eager reads, so
they validate the configuration the application actually ends up with.

### The schedule endpoint proves the two hosts agree

`POST /api/schedule/run` runs the same engine over the same mapper and
returns the engine's canonical placement signature. The API tests assert
that the signature from the HTTP round trip equals the one from running
the mapper and engine in process on the same rows — which is what turns
"the server schedules the same way" from a claim into a test.

## Consequences

- ✅ The personas become real where a server exists: a 403 now means the
  server read a signed token, checked its roles and refused. The demo
  keeps working untouched where one does not.
- ✅ One authorization model in the repository, enforced twice. Adding a
  policy is still one line in one table.
- ✅ The seam ADR 0013 predicted turned out to be exactly one class:
  `RemoteAuthenticationStateProvider` replaced
  `DemoAuthenticationStateProvider` and no page, policy or guard changed.
- ✅ Testable end to end: 73 integration tests against the shipping
  composition root and a real SQLite file, covering lockout, rate
  limiting, rotation, reuse detection, concurrency and every endpoint's
  refusals; plus client tests for the provider swap, the renewal path and
  the persona switcher disappearing.
- ➖ **Offline writes are not solved.** Connected mode reads from the
  server and writes through it. A browser that loses the network keeps a
  read-only copy; it does not queue changes.
- ➖ **There is no conflict resolution.** The API has optimistic
  concurrency per row and answers 409; it has no merge. Two people
  editing one routing is a race one of them loses politely.
- ➖ **There is no multi-tenancy.** One plant, one set of master data,
  roles but no ownership. Adding tenants means a tenant key on every
  table and on every query, which is a different change.
- ➖ **Token theft through XSS is mitigated, not prevented.** See above.
- ➖ Two deployables where there was one, and a second database schema to
  migrate. The browser build does not depend on either, which keeps the
  cost confined to whoever chooses to run the server.
