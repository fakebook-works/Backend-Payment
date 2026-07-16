# API Gateway Handoff: Payment Subgraph

The Gateway team owns these changes.

## Fusion composition

1. Export the Payment schema from `Backend-Payment` with schema name `Payment`.
2. Add:

```text
fakebookGateway/Gateway/schema/Payment/schema.graphqls
fakebookGateway/Gateway/schema/Payment/schema-settings.json
```

3. Configure the Payment transport:

```json
{
  "name": "Payment",
  "transports": {
    "http": {
      "url": "{{PAYMENT_URL}}",
      "clientName": "payment-fusion"
    }
  },
  "environments": {
    "Development": {
      "PAYMENT_URL": "http://localhost:1007/graphql"
    },
    "Production": {
      "PAYMENT_URL": "http://payment:1007/graphql"
    }
  }
}
```

4. Register `payment-fusion` with `FusionSubgraphHeaderHandler` so Payment receives trusted user/session/correlation/Gateway-secret headers.
5. Compose Authentication and Payment into `gateway.far` for both Development and Production.
6. Confirm the public schema exposes `premiumPlans`, `premiumOrder`, and `createPremiumCheckout`.
7. Mark Authentication's `paymentPremiumState` and `setPaymentValidDate` fields `@internal` in Authentication schema extensions.

## Public PayOS webhook proxy

Expose:

```text
POST /api/webhooks/payos
```

Proxy raw bytes to:

```text
POST http://payment:1007/internal/webhooks/payos
```

Requirements:

- anonymous public route;
- HTTPS at the public edge;
- JSON content type only;
- 64 KiB maximum body;
- fixed-window IP rate limit;
- dedicated HttpClient, not a Fusion client;
- strip all caller-supplied trusted headers;
- forward only content type, correlation ID, and the real `X-Gateway-Secret`;
- never forward browser Authorization, cookies, user/session headers, or a caller-provided secret;
- preserve safe downstream status only, never downstream response bodies/headers;
- timeout/network failure returns `503`;
- no PayOS credentials in Gateway configuration.

## Required tests

- Payment schema composes with Authentication.
- Internal Auth payment fields remain hidden.
- Spoofed trusted headers never reach Payment.
- Webhook raw body is byte-for-byte preserved.
- Empty/wrong/oversized/rate-limited requests return `400/415/413/429`.
- Payment timeout returns `503` without leaking details.
