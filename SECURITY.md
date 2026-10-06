# Security policy

## Supported versions

Waybill is experimental and has no stable release yet. Security fixes go to the latest published version only.

## Reporting a vulnerability

Please **do not** open a public issue for security problems.

Report privately through GitHub's [private vulnerability reporting](https://github.com/Joseleno/Waybill/security/advisories/new).
Include what you found, how to reproduce it, and the impact you expect.

What to expect:

- First response within 72 hours.
- An assessment and, if confirmed, a fix plan within 14 days.
- Credit in the advisory and the changelog, unless you prefer otherwise.

Waybill is maintained by a single person. If you do not hear back within 72 hours, please follow up on the same report.

## Scope

In scope: anything in this repository that could lose, duplicate, leak or corrupt messages beyond what the documented
guarantees allow, and the usual classes of library vulnerabilities (injection, unsafe deserialization, secrets in logs).

Out of scope: misconfiguration of PostgreSQL or the broker that the documentation already warns about.
