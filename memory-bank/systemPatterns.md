# System Patterns

## Intended architecture
- Domain contains business rules and value objects; Application contains use cases and interfaces; Infrastructure implements storage; API maps HTTP endpoints. Dependency direction is described in `docs/03-ARCHITECTURE-RULES.md`.
- Keep tenant and actor identity server-derived for protected operations. Enforce tenant isolation and permissions in data access as well as at endpoints.
- Use controlled domain failures, cancellation tokens on async work, and focused tests. The numbered docs describe CQRS, MediatR, FluentValidation, and EF Core as target conventions; do not assume those packages or patterns have already been implemented.

## Implemented authentication slice
- API endpoints in `src/LifeDrop.API/Program.cs`: `POST /auth/devices/register`, `/auth/login`, `/auth/refresh`, `/auth/logout`, `/devices/{deviceId}/approve`, `/devices/{deviceId}/block`, and `/users/{userId}/roles/{roleId}`.
- Login authenticates a tenant user. Roles may require an approved device; registration returns a one-time device secret. Access tokens carry tenant, user, session family, and optional device claims. Refresh tokens rotate; logout revokes the family.
- PostgreSQL implementations are in `src/LifeDrop.Infrastructure/{Auth,Devices,Roles}`. SQL migrations `db/migrations/0001` through `0003` define the current schema. Domain models cover users, roles, audit, devices, and refresh tokens.
- This slice currently uses Npgsql and direct application services. The Android MAUI authentication app is present; do not describe EF Core, MediatR, Redis, Blazor, full mobile visit flows, or iOS runtime as implemented without new evidence.

## Change discipline
Keep changes small, preserve existing work, use the isolated `TEST_DATABASE_URL` for database tests, and never run a write test against a general development database. Verify code and live behavior separately.
For prompt scope and phase gates, use `docs/ANTIGRAVITY-EXECUTION.md`. Do not paste whole files or the full roadmap into each task prompt.
