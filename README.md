# Fakebook Backend Payment

.NET 8 + HotChocolate Payment subgraph for PayOS Premium checkout.

## Scope

- GraphQL subgraph schema name: `Payment`.
- Browser traffic reaches this service through Fusion/API Gateway only.
- PayOS webhook reaches `/internal/webhooks/payos` through the Gateway proxy.
- Payment owns its order, transaction, and outbox tables.
- Authentication remains the sole owner of `fb.id_user.valid_date`.
- There is no automatic renewal. An expired account starts a new checkout.

## Premium plans

| Plan | Price | Granted time |
|---|---:|---:|
| `MONTHLY` | 52,000 VND | 1 month |
| `YEARLY` | 500,000 VND | 12 months |

## Local configuration

Set the variables from `.env.example` in your local secret manager. Never copy PayOS values into frontend variables, committed appsettings files, Gateway configuration, logs, or CI output.

Use separate random secrets of at least 32 bytes for Gateway→Payment and Payment→Authentication.
Keep `Payment__PaymentsEnabled=false` until Authentication, Gateway composition, and the PayOS webhook proxy are deployed and verified.

## Run

```powershell
dotnet run --project .\fakebookPayment\fakebookPayment.csproj
```

The service uses port `5016`. Startup applies the idempotent `schema.sql` to its Payment database.

- GraphQL: `/graphql`
- PayOS webhook (internal): `/internal/webhooks/payos`
- Liveness: `/health/live`
- Readiness: `/health/ready`

See `docs/handoffs` for Gateway composition/proxy work and Authentication's `validDate` contract.
