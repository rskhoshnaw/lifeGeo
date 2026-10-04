# Coding Conventions

- Use file-scoped namespaces.
- All async methods must include a `CancellationToken`.
- Use `AsNoTracking` for database read operations.
- Define DTOs using `record` types.
- Implement validation using FluentValidation.
- Use MediatR for Commands and Queries.
- Naming: PascalCase for classes and methods; camelCase for local variables.
- Write tests using the Arrange-Act-Assert pattern.
- Avoid magic strings and magic numbers.
- Log events using Serilog with appropriate log levels.
- Code must be readable, simple, and testable.