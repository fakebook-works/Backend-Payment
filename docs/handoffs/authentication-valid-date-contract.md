# Authentication Handoff: Premium Valid Date

Authentication owns the user table and adds exactly one payment-related field:

```sql
ALTER TABLE fb.id_user
ADD COLUMN valid_date timestamptz NULL;
```

Expose `validDate: DateTime` on user projections.

Provide direct internal GraphQL operations for Backend-Payment:

```graphql
type PaymentPremiumState {
  userId: ID!
  validDate: DateTime
}

input SetPaymentValidDateInput {
  userId: ID!
  validDate: DateTime!
}

extend type Query {
  paymentPremiumState(userId: ID!): PaymentPremiumState!
}

extend type Mutation {
  setPaymentValidDate(input: SetPaymentValidDateInput!): PaymentPremiumState!
}
```

Both operations require `X-Payment-Secret`, configured independently from browser and PayOS credentials and compared in constant time.

The mutation must be idempotent and never shorten validity:

```sql
UPDATE fb.id_user
SET valid_date = GREATEST(
        COALESCE(valid_date, '-infinity'::timestamptz),
        @ValidDate
    ),
    updated_at = now()
WHERE user_id = @UserId;
```

No payment order, transaction, provider, plan, webhook, or outbox data is stored in Authentication.

The Gateway team must mark these two fields `@internal` so they are absent from the public Fusion schema.
