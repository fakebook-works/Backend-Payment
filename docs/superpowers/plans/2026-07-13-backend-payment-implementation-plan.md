# Backend-Payment Implementation Plan

Date: 2026-07-13

Source design: `Frontend/docs/superpowers/specs/2026-07-11-backend-payment-subgraph-design.md`

## Execution order

1. Scaffold `fakebookPayment.sln`, the .NET 8 web project, and xUnit tests.
2. Add HotChocolate 16.1.3, Dapper, Npgsql, PayOS 2.1.0, and test dependencies.
3. Add fail-fast configuration, correlation IDs, trusted Gateway request context, Snowflake IDs, and health endpoints.
4. Add the Payment-owned PostgreSQL schema for orders, transactions, and outbox messages.
5. Implement plan catalogue, state model, repositories, and transactional payment ledger.
6. Implement the PayOS adapter behind `IPayOSPaymentProvider`.
7. Implement the Authentication internal GraphQL client for reading and setting `fb.id_user.valid_date`.
8. Implement GraphQL `premiumPlans`, `premiumOrder`, and `createPremiumCheckout`.
9. Implement the internal PayOS webhook endpoint with body limits, Gateway-secret validation, SDK signature verification, and idempotent ledger writes.
10. Implement the outbox worker that computes/stores an absolute target date and retries Authentication updates.
11. Export the Payment GraphQL schema and write Gateway/Auth handoff documents.
12. Run unit/integration tests, build, dependency/security checks, and a completion audit.

## Commit boundaries

1. `chore: scaffold backend payment service`
2. `feat: add payment persistence and domain`
3. `feat: add payos checkout and webhook processing`
4. `feat: deliver premium validity through outbox`
5. `test: cover payment security and workflows`
6. `docs: add gateway and authentication handoff`

## Verification commands

```powershell
dotnet restore .\fakebookPayment.sln
dotnet build .\fakebookPayment.sln -c Release --no-restore
dotnet test .\fakebookPayment.sln -c Release --no-build
dotnet run --project .\fakebookPayment\fakebookPayment.csproj --no-build -- schema export --schema-name Payment --output .\artifacts\payment.graphqls
dotnet list .\fakebookPayment\fakebookPayment.csproj package --vulnerable --include-transitive
git grep -n -E "PAYOS_(CLIENT_ID|API_KEY|CHECKSUM_KEY)=.+"
```

No API Gateway source file is changed by this implementation. Gateway integration is delivered as a team handoff plan.
