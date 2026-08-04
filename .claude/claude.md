# GameLauncher Development Rules

## Architecture
- Follow MVVM strictly.
- No business logic in ViewModels.
- Services contain application logic.
- Use dependency injection.

## Code Style
- Nullable enabled.
- Async everywhere.
- Use interfaces for services.
- Use Dapper for database access.

## Projects
Desktop:
WPF .NET 8

Relay:
ASP.NET Core 8 Minimal API

Shared:
DTO contracts only.

## Before modifying code
Always inspect existing implementations first.

## Do not:
- Create duplicate services.
- Add unnecessary dependencies.
- Leave TODOs.
- Replace working architecture with shortcuts.