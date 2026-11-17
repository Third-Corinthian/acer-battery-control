# Security

## Supported versions

Only the latest released version receives fixes.

## Reporting a vulnerability

Do not open a public issue. Use **Security > Report a vulnerability** on GitHub (private report),
including the version, notebook model, steps to reproduce and impact.

## Scope

The service runs as LocalSystem and writes to the battery firmware and the power plan. Of particular
interest are:

- commands or files that let a non-administrator user obtain more than the interface allows;
- firmware writes outside the rules documented in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md);
- service crashes or hangs that a user can trigger.

Out of scope: the behavior of the Acer firmware itself and actions by a local administrator.
