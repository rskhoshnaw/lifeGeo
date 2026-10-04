# Tech Stack - LifeDrop

## Backend
- ASP.NET Core (Latest LTS version)
- C#
- Clean Architecture
- CQRS + MediatR
- FluentValidation
- Entity Framework Core + Dapper (as needed)
- JWT + Refresh Token
- Device Binding

## Database
- PostgreSQL
- PostGIS (for geospatial data and geofencing)

## Cache
- Redis

## Web Panel
- React + TypeScript (strict), built with Vite (product decision D10, 2026-10-04)
- Tailwind CSS v4 for styling, with logical (RTL-safe) utilities (decision D12)
- Calls the ASP.NET Core API directly; server-side permission checks remain authoritative

## Mobile
- .NET MAUI
- Primary target: Android
- Secondary target: iOS

## Map
- MapLibre + OpenStreetMap (OSM)

## Other Tools
- Docker + Docker Compose
- xUnit + Moq / NSubstitute
- Git + GitHub
- GitHub Actions (CI/CD)
- Serilog (Logging)
- RabbitMQ (for future phases)

## Technology Selection Principles
- C# for backend and mobile; TypeScript for the web panel
- Minimal dependency on non-essential technologies
- Scalability to support up to 150 field users

## Status
This file lists target technology. Installed and verified components are tracked in `memory-bank/techContext.md`; MediatR, FluentValidation, EF Core, Redis, Serilog and RabbitMQ are not yet in use.
