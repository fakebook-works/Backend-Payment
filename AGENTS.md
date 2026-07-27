# Repository Guidelines

## Project Structure & Module Organization

`fakebookPayment/` contains the .NET 10 ASP.NET Core payment service. Code is grouped by responsibility: `GraphQL/` exposes checkout operations, `Endpoints/` handles the internal PayOS webhook, `Services/` contains payment and authentication workflows, `Repositories/` owns database access, and `Workers/` runs schema initialization and premium activation. Domain records live in `Models/`, configuration bindings in `Configuration/`, and request validation helpers in `Security/`. The PostgreSQL schema is in `fakebookPayment/schema.sql`.

`fakebookPayment.Tests/` contains unit, endpoint, schema, and repository integration tests. Architecture and cross-service contracts are documented under `docs/handoffs/`; implementation plans are under `docs/superpowers/plans/`.

## Build, Test, and Development Commands

- `dotnet restore fakebookPayment.sln` restores NuGet dependencies.
- `dotnet build fakebookPayment.sln --no-restore` compiles the service and tests.
- `dotnet test fakebookPayment.sln` runs all xUnit tests; Docker must be available for PostgreSQL Testcontainers tests.
- `dotnet run --project fakebookPayment/fakebookPayment.csproj` starts the API locally on port `1007`.
- `dotnet test fakebookPayment.Tests/fakebookPayment.Tests.csproj --filter FullyQualifiedName~PremiumPaymentServiceTests` runs a focused test class.

## Coding Style & Naming Conventions

Use four-space indentation and standard C# conventions: `PascalCase` for types, methods, and public members; `camelCase` for locals and parameters; and an `I` prefix for interfaces. Nullable reference types and implicit usings are enabled. Keep endpoint wiring thin, business rules in services, and SQL access in repositories. Prefer async APIs, propagate `CancellationToken`, and use dependency injection rather than constructing infrastructure dependencies inline. Run `dotnet format fakebookPayment.sln` before submitting broad formatting changes.

## Testing Guidelines

Tests use xUnit, `Microsoft.AspNetCore.Mvc.Testing`, and Testcontainers. Name files and classes after the subject under test, such as `PaymentRepositoryIntegrationTests`, and use behavior-focused test method names. Add unit tests for domain rules and integration tests when changing SQL, GraphQL, webhook, or persistence behavior. Integration tests sharing PostgreSQL belong to `PostgreSqlIntegrationCollection` and remain non-parallel.

## Commit & Pull Request Guidelines

Follow the repository’s concise conventional style: `feat: implement ...`, `test: cover ...`, or `fix: handle ...`. Keep each commit focused. Pull requests should explain behavior changes, list verification commands, link the relevant issue or contract, and call out schema or configuration changes. Include example GraphQL requests or webhook payloads when API behavior changes; screenshots are generally unnecessary for this backend.

## Security & Configuration

When embedded in the Fakebook workspace, also read the root API security contract.

- Derive the caller from IGatewayRequestContextAccessor, never from input userId.
- A client cannot choose authoritative price, entitlement, owner or webhook result.
- Preserve exact-body PayOS signature verification, body/content-type limits and webhook
  rate limiting. Never forward browser cookies/auth/trusted headers to the internal handler.
- Internal REST calls use HMAC signing and Redis nonce replay protection.
- Automatic retry stays disabled for unsafe HTTP methods.
- Runtime DB access uses the payment-scoped role and startup schema application stays off.
- Do not log checkout signatures, secrets, raw webhook bodies or payment credentials.

API changes require wrong-user, tampered-webhook, duplicate/idempotency and failure tests.

Copy variable names from `.env.example`, but keep real PayOS, gateway, authentication, and database secrets out of Git, logs, frontend variables, and committed appsettings files. Leave `Payment__PaymentsEnabled=false` until dependent services and webhook routing are verified.
