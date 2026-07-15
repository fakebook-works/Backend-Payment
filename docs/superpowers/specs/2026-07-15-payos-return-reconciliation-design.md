# PayOS Return Reconciliation

## Goal

Handle successful and cancelled PayOS browser returns using PayOS's documented query parameters while keeping verified webhooks as the authority for completed payments and Premium activation.

References:

- https://payos.vn/docs/sdks/back-end/net/
- https://payos.vn/docs/du-lieu-tra-ve/return-url/
- https://payos.vn/docs/du-lieu-tra-ve/webhook/

## Redirect URLs

Checkout creation sends both `ReturnUrl` and `CancelUrl` to the authenticated frontend route `/premium/payment`. Do not pre-populate result or order-code query parameters. PayOS appends `code`, `id`, `cancel`, `status`, and `orderCode` to the configured URL.

## Reconciliation API

Expose an authenticated GraphQL mutation named `reconcilePremiumCheckout(orderCode)`.

The Payment service:

1. Loads the order owned by the authenticated user.
2. Requests the payment link from PayOS using `PaymentRequests.GetAsync(orderCode)`.
3. Verifies the returned order code, payment-link ID, and amount against the stored order.
4. Marks a local `CREATED` or `PENDING` order as `CANCELLED` only when PayOS reports `CANCELLED`.
5. Leaves `PENDING` and `PROCESSING` orders pending.
6. Does not mark an order paid or activate Premium from return data or reconciliation. A verified PayOS webhook remains responsible for recording payment and creating the activation outbox event.

Provider failures return a generic retryable error without leaking credentials or provider response bodies.

## Frontend Behavior

When `/premium/payment` loads, the frontend parses the documented query parameters. It validates `orderCode`, stores it as the pending order, removes the query string from browser history, invokes reconciliation, and then continues the existing order polling.

The frontend may show a cancelled or processing message from return parameters, but the rendered order status comes from the authenticated backend response. A forged `status=PAID` parameter never activates Premium or changes local payment state.

## Security

- The Gateway-authenticated user identity determines ownership; the browser cannot supply a user ID.
- Query parameters are hints only and are never trusted for payment completion.
- PayOS credentials remain server-side environment variables.
- Payment-link identity and amount are checked before any state transition.
- Existing webhook signature verification and idempotent transaction handling remain unchanged.

## Verification

- Provider unit tests cover `CANCELLED`, `PENDING`, `PROCESSING`, `PAID`, mismatched order code, payment-link ID, and amount.
- Service and repository tests cover ownership and the conditional cancellation transition.
- GraphQL tests cover authenticated reconciliation and safe error codes.
- Frontend tests cover documented success/cancel query strings, URL cleanup, and forged `PAID` input.
- Existing webhook, checkout, activation, and frontend tests continue to pass.
