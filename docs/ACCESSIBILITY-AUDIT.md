# Accessibility audit

Audit date: 2026-07-19

## Automated evidence

- bUnit verifies modal dialog semantics, localized close name, Escape handling and focus hand-off.
- Playwright verifies keyboard modal operation, focus return, dynamic `html lang` and the mobile drawer.
- The hosted login was inspected through Chrome's accessibility tree after a real Release WASM startup. The tree exposed one `main`, one level-one heading (`Sign in`), named `Email` and `Password` textboxes, and named submit/register buttons.
- A DOM audit found no duplicate IDs, no visible unnamed interactive controls and no text contrast failures on the hosted login surface. Its focus order is email, password, sign in, create account.
- The shared layout now includes a localized skip link, named primary navigation and a programmatically focusable main landmark. Production scheduling uses a table caption, named checkboxes/progress/cancel controls, live status/error regions and explicit empty states.
- Authenticated production Playwright checks now verify named login fields and autocomplete metadata, the document language, skip link/main landmark, named primary navigation, account-security route, German switching and mobile drawer semantics against the running production container.

## Manual screen-reader protocol

Run this before claiming WCAG conformance or a regulated deployment:

1. NVDA + current Chrome on Windows: navigate the login, account-security and production-schedule pages using headings, landmarks, form fields and tables.
2. Verify error, queued, progress and cancellation announcements without moving focus.
3. Verify TOTP/recovery-code setup, recovery-code warning and validation errors at 200% and 400% zoom.
4. Repeat the critical flow with VoiceOver + Safari on macOS/iOS.
5. Record version, browser, route, result and issue link for every checkpoint.

The repository contains accessibility-tree, DOM and keyboard evidence, but it does **not** claim WCAG conformance or that a human NVDA/VoiceOver audit has been completed.

The sign-off record is maintained in [EXTERNAL-ASSURANCE-CHECKLIST.md](EXTERNAL-ASSURANCE-CHECKLIST.md). A named tester must record assistive-technology/browser versions, findings and retest evidence there. This boundary prevents an automated DOM inspection from being misrepresented as human screen-reader experience.
