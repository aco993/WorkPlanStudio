# 13. Personas through the real authorization pipeline, without a backend

- **Status:** Accepted
- **Date:** 2026-09-08

## Context

Every user of the demo had every power: edit master data, release orders,
reset the database. A planning tool is not used like that — a supervisor
releases orders but does not redefine routings, a visitor looks and touches
nothing. Roles with visible consequences were asked for, and the app has no
server: it is a static site on GitHub Pages with nothing to authenticate
against and nowhere to keep a secret.

Three options:

1. **Add a backend** for identity. It would buy real authentication, at the
   price of hosting, a token flow and an API layer the app otherwise does not
   need — and the demo would stop being a static site that anyone can open.
2. **Fake it in the UI** with an `if (role == Planner)` around each button.
   Cheap, and nothing but a convention: the next page forgets the check, the
   service layer never had one.
3. **Use the real pipeline with a demo identity.** ASP.NET Core's authorization
   stack (`AuthorizeView`, policies, `IAuthorizationService`) does not care
   where the `ClaimsPrincipal` comes from. Feed it a persona chosen in the UI.

## Decision

Option 3. `DemoAuthenticationStateProvider` is an `AuthenticationStateProvider`
whose principal carries a role claim for the chosen persona — Planner,
Supervisor or Guest — remembered per browser and switchable from the top bar
without a reload. The identity's authentication type is `demo-persona`, so
nothing can mistake it for a login.

What each persona may do is one table, `Permissions.RolesFor`, registered as
named policies. Pages use `AuthorizeView Policy="…"` to hide what the persona
cannot do and a `ReadOnlyNotice` to say so, naming the persona that could.
**Every mutating service method asks the same policy first**, through an
`IPermissionGuard` backed by `IAuthorizationService`, and returns
`Forbidden` otherwise. A hidden button is a courtesy; the guard is the check.

Because the guard is the standard service, swapping the identity source is
one class: an OIDC provider would replace `DemoAuthenticationStateProvider`
and nothing else — the policies, the views and the guard stay.

## Consequences

- ✅ Roles have visible consequences on every page and at the service
  boundary, and the two cannot drift: the same policy table feeds both.
- ✅ The pipeline is the real one, so the code reads like a production Blazor
  app rather than a demo-specific switch. The seam for a real identity
  provider is explicit.
- ✅ Testable at every level: the policy matrix through `IAuthorizationService`,
  services returning `Forbidden` for a guest, pages rendering without their
  actions, a persona switch re-rendering without a reload, and Playwright
  proving the same in a browser.
- ➖ **This is not security.** The persona lives in `localStorage` and the code
  runs in the visitor's browser; anyone can be the planner by choosing to. The
  design demonstrates authorization plumbing, not access control — a plain
  statement in `docs/SECURITY.md`. Real protection needs a server that owns
  the data and re-checks every write.
- ➖ One more concept to explain on first visit, handled by the persona menu's
  descriptions. First-time visitors start as the planner so the demo is fully
  usable before they have found the menu.
