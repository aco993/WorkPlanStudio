# Security policy

## Reporting a vulnerability

Please report a suspected vulnerability privately through GitHub's
[Report a vulnerability](https://github.com/aco993/WorkPlanStudio/security/advisories/new)
form rather than in a public issue, and allow a few days for a first response.
This is a portfolio project maintained by one person in their own time, so
there is no service-level commitment behind that.

Include what you did, what happened, and why you believe it is a security
problem rather than a limitation of the design — the two are easy to confuse
here, because a great deal of this application is deliberately not protected.

## What is in scope, and what is not

**The deployed demo** is a **static site that runs entirely in the visitor's
browser**. It has no server, no accounts, no shared storage and no secrets of its
own. The repository also contains an **optional ASP.NET Core backend**
(`src/WorkPlanStudio.Api`) with real accounts, password hashing and token handling;
it is off unless configured, and the deployed demo does not configure it. The full
reasoning, including the trust boundaries and the risks that were accepted rather
than fixed, is in **[`docs/SECURITY.md`](docs/SECURITY.md)** — read that first.

Three things are documented there and are therefore *not* vulnerabilities:

- **The personas are not access control.** Planner, supervisor and guest run
  through the real ASP.NET Core authorization pipeline, but the identity behind
  them is a choice stored in the visitor's own browser and the code enforcing it
  runs there too. Anyone can be the planner. That is the demonstration, not a
  flaw ([ADR 0013](docs/adr/0013-personas-through-the-real-authorization-pipeline.md)).
- **Data lives in `localStorage`.** It is readable by any script on the origin,
  it is not encrypted, and the application says so on its own About page.
- **A bring-your-own-key API key lives in `localStorage` too**, and any script on
  that origin can read it. The application states this in the settings dialog
  itself, stores the key per provider, never reads it back into the page, and
  offers one-click removal. That it is readable at all is a property of a
  browser-only app, not a defect in this one
  ([ADR 0025](docs/adr/0025-hostile-input-on-the-model-path.md)).

In scope: anything that lets a page, a link or a file cause the application to act
against the visitor's intent — script injection through imported or stored data, a
bring-your-own-key credential leaving the browser for anywhere but the provider the
user chose, a request whose target can be steered by user-supplied configuration, a
path into the optional backend that bypasses its policies, or a supply-chain problem
in a pinned dependency or a pinned action.

## Supported versions

The deployed site is built from `main`. There are no maintained release
branches, so the answer to "is version X patched" is: only `main` is.
