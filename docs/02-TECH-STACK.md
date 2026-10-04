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
- Blazor Server

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
- Focus on C# across all layers
- Minimal dependency on non-essential technologies
- Scalability to support up to 150 field users