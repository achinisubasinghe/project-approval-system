# Blind-Match PAS

Blind-Match PAS is an ASP.NET Core Blazor Server application for allocating final-year project supervisors without exposing student identities during proposal review. Students submit proposals by research area, supervisors browse anonymous proposals that match their expertise, and confirmed matches reveal both parties.

## Features

- **Student workspace.** Create, edit, withdraw, and track project proposals by research area.
- **Supervisor workspace.** Maintain expertise areas, review anonymous proposals, express interest, and confirm matches within capacity limits.
- **Module leader workspace.** Manage users, research areas, proposals, and manual supervisor assignments.
- **Matching rules.** Enforces supervisor expertise matching, duplicate interest prevention, first-confirmed match wins, and per-supervisor capacity limits in `ProjectMatchingService`.
- **Role-aware assistant.** Optional Groq API chat assistant with role-scoped read and confirmed write tools.
- **Automated persistence and testing.** Entity Framework Core SQL Server migrations run and seed baseline data on startup, with xUnit tests running against an in-memory SQLite database.

## Tech stack

- .NET 10 and ASP.NET Core Blazor Server
- MudBlazor
- ASP.NET Core Identity
- Entity Framework Core with SQL Server
- Groq API (`qwen/qwen3.8-27b`)
- xUnit with SQLite in-memory tests

## Getting started

### Prerequisites

- .NET 10 SDK
- SQL Server, SQL Server Express, or a local SQL Server container
- `dotnet-ef` CLI tool when running manual migration commands
- Groq API key when using the chat assistant

Install the EF Core CLI tool globally if needed:

```bash
dotnet tool install --global dotnet-ef
```

### Configuration

Development settings live in `appsettings.Development.json`. Store database credentials and API keys in .NET user secrets:

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost;Database=BlindMatchPas;User Id=sa;Password=YOUR_PASSWORD;TrustServerCertificate=True;"
dotnet user-secrets set "IdentityDefaults:ModuleLeader:Password" "ChangeMe!123"
dotnet user-secrets set "Groq:ApiKey" "gsk_..."
```

### Run locally

```bash
git clone https://github.com/achinisubasinghe/project-approval-system.git
cd project-approval-system
dotnet restore
dotnet run
```

The development profile listens on `http://localhost:5022` and `https://localhost:7220`. On startup, `DbInitializer.SeedAsync` applies pending EF Core migrations and seeds development accounts.

### Seeded accounts

| Role | Email | Password |
| --- | --- | --- |
| Module Leader | `lead@pas.local` | `ChangeMe!123` |
| Student | `student@pas.local` | `Student!123` |
| Supervisor | `supervisor@pas.local` | `Supervisor!123` |

Startup seeding also creates the Artificial Intelligence, Web Development, Cybersecurity, and Cloud Computing research areas, and assigns Artificial Intelligence and Web Development expertise to `supervisor@pas.local`.

## Database migrations

Apply migrations manually to `BlindMatchPas`:

```bash
dotnet ef database update
```

Create a new migration after changing the EF Core model:

```bash
dotnet ef migrations add MigrationName --output-dir Data/Migrations
```

## Testing

Run the xUnit test suite:

```bash
dotnet test
```

## Workflow

1. A student submits a proposal with a title, abstract, technical stack, and research area.
2. A supervisor configures expertise areas and browses matching proposals without seeing student names.
3. The supervisor expresses interest, moving the proposal into review.
4. The supervisor confirms the match if capacity is available.
5. Confirmation locks the allocation and reveals student and supervisor contact details to both parties.
6. A module leader monitors allocations and assigns remaining unmatched proposals.

## Project structure

```text
project-approval-system/
├── Components/
│   ├── Account/
│   ├── Layout/
│   └── Pages/
│       ├── Admin/
│       ├── Student/
│       └── Supervisor/
├── Data/
│   └── Migrations/
├── Services/
│   └── Chatbot/
│       └── Tools/
├── project-approval-system.Tests/
├── wwwroot/
├── Program.cs
├── appsettings.json
└── project-approval-system.csproj
```
