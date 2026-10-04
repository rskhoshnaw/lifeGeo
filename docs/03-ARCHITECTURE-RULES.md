# Architecture Rules - Clean Architecture

## Layers and Dependency Rules
- Domain: Pure business logic only. No external dependencies (EF Core, MediatR, ASP.NET, etc.).
- Application: Depends only on the Domain layer. Includes Commands, Queries, Handlers, DTOs, Validators, and Interfaces.
- Infrastructure: Implementation of Application and Domain interfaces (EF Core, Redis, JWT, File Storage, etc.).
- API / Presentation: Depends on Application and Infrastructure layers. Includes Endpoints, Middleware, and Blazor.

## Mandatory Rules
1. `TenantId` must be present in all operational entities and enforced in current SQL/RLS and application boundaries. EF query filters apply only if EF is later introduced.
2. All state changes for a "Visit" must be performed exclusively via the server-side state machine.
3. Sensitive operations (Login, Device Approval, Kill Session, modifications to User/Customer/Visit/Order/Payment, Backup deletion) must be audited.
4. Audit logs must not be deletable via the UI or API.
5. Geofence calculations and final check-in/check-out decisions must be performed exclusively on the server.
6. Use the Result Pattern or controlled domain-specific exceptions.
7. All async methods must accept a `CancellationToken`.
