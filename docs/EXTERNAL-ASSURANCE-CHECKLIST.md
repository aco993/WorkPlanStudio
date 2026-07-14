# External assurance checklist

These items require evidence from people or infrastructure outside the source repository. They must not be marked complete by an automated coding agent.

## Screen-reader sign-off

- Tester name/date/device and application commit:
- NVDA + Firefox/Chrome version on Windows:
- VoiceOver + Safari version on macOS/iOS:
- Sign-in, password reset, MFA, primary navigation, production-order and scheduling flows completed without pointer input:
- Announced headings, landmarks, validation errors, busy/progress status, modal focus and table context verified:
- Defects, severity, recordings and retest result:
- Accessibility owner approval:

## Independent penetration test

- Supplier and rules of engagement approved:
- Target environment/data classification and test window:
- Authenticated and unauthenticated web/API testing:
- Tenant isolation, CSRF, session/MFA/recovery, rate limits, SSRF, injection and business-logic abuse covered:
- Report identifier and critical/high/medium findings:
- Remediation commits and independent retest:
- Security owner risk acceptance:

## HA and regulated-production evidence

- Target architecture and responsible service owners:
- Multi-zone replica/database/key-store placement:
- Measured worker and database failover drill:
- Backup restore/PITR drill with measured RPO/RTO:
- Capacity/soak evidence using representative traffic and data volume:
- Monitoring, paging, incident, change, access-review and retention procedures:
- Privacy/legal/regulatory control mapping and auditor approval:

Repository automation may attach supporting artifacts, but only the named accountable people can sign these sections.
