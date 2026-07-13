CREATE SCHEMA IF NOT EXISTS payment;
CREATE SEQUENCE IF NOT EXISTS payment.order_code_seq AS bigint MINVALUE 1 MAXVALUE 9007199254740991 NO CYCLE;

CREATE TABLE IF NOT EXISTS payment.payment_order (
    id bigint PRIMARY KEY,
    order_code bigint NOT NULL DEFAULT nextval('payment.order_code_seq') UNIQUE,
    user_id bigint NOT NULL,
    plan text NOT NULL CONSTRAINT ck_payment_order_plan CHECK (plan IN ('MONTHLY', 'YEARLY')),
    amount bigint NOT NULL CONSTRAINT ck_payment_order_amount CHECK (amount > 0),
    currency varchar(3) NOT NULL DEFAULT 'VND' CONSTRAINT ck_payment_order_currency CHECK (currency = 'VND'),
    status text NOT NULL CONSTRAINT ck_payment_order_status CHECK (status IN ('CREATED','PENDING','PAID','ACTIVATION_PENDING','ACTIVATED','CANCELLED','EXPIRED','FAILED')),
    provider_payment_link_id text NULL,
    checkout_url text NULL,
    expires_at timestamptz NOT NULL,
    paid_at timestamptz NULL,
    activated_at timestamptz NULL,
    target_valid_date timestamptz NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_payment_order_user_open ON payment.payment_order (user_id)
    WHERE status IN ('CREATED','PENDING');
CREATE UNIQUE INDEX IF NOT EXISTS ux_payment_order_payment_link
    ON payment.payment_order (provider_payment_link_id) WHERE provider_payment_link_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_payment_order_user_created ON payment.payment_order (user_id, created_at DESC);

CREATE TABLE IF NOT EXISTS payment.payment_transaction (
    id bigint PRIMARY KEY,
    order_id bigint NOT NULL REFERENCES payment.payment_order(id),
    provider_reference text NOT NULL UNIQUE,
    provider_payment_link_id text NOT NULL,
    amount bigint NOT NULL,
    currency varchar(3) NOT NULL,
    provider_code varchar(32) NOT NULL,
    provider_description varchar(255) NOT NULL,
    is_canonical boolean NOT NULL DEFAULT true,
    paid_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS payment.outbox_message (
    id bigint PRIMARY KEY,
    order_id bigint NOT NULL UNIQUE REFERENCES payment.payment_order(id),
    event_key text NOT NULL UNIQUE,
    event_type text NOT NULL CHECK (event_type = 'ACTIVATE_PREMIUM'),
    user_id bigint NOT NULL,
    target_valid_date timestamptz NULL,
    attempt_count integer NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    processed_at timestamptz NULL,
    last_error_code text NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_outbox_ready ON payment.outbox_message (next_attempt_at, created_at) WHERE processed_at IS NULL;

-- Reconcile databases initialized by pre-release drafts. These statements are
-- idempotent and intentionally remain alongside the baseline schema because
-- this service self-hosts and applies schema.sql at startup.
DROP INDEX IF EXISTS payment.ux_payment_order_user_unfinished;

ALTER TABLE payment.payment_order
    ALTER COLUMN user_id TYPE bigint USING user_id::bigint;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_payment_order_plan' AND conrelid = 'payment.payment_order'::regclass) THEN
        ALTER TABLE payment.payment_order ADD CONSTRAINT ck_payment_order_plan CHECK (plan IN ('MONTHLY', 'YEARLY'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_payment_order_amount' AND conrelid = 'payment.payment_order'::regclass) THEN
        ALTER TABLE payment.payment_order ADD CONSTRAINT ck_payment_order_amount CHECK (amount > 0);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_payment_order_currency' AND conrelid = 'payment.payment_order'::regclass) THEN
        ALTER TABLE payment.payment_order ADD CONSTRAINT ck_payment_order_currency CHECK (currency = 'VND');
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_payment_order_status' AND conrelid = 'payment.payment_order'::regclass) THEN
        ALTER TABLE payment.payment_order ADD CONSTRAINT ck_payment_order_status CHECK (status IN ('CREATED','PENDING','PAID','ACTIVATION_PENDING','ACTIVATED','CANCELLED','EXPIRED','FAILED'));
    END IF;
END $$;

ALTER TABLE payment.payment_transaction
    ADD COLUMN IF NOT EXISTS provider_code varchar(32),
    ADD COLUMN IF NOT EXISTS provider_description varchar(255),
    ADD COLUMN IF NOT EXISTS is_canonical boolean NOT NULL DEFAULT true;
UPDATE payment.payment_transaction SET provider_code = '00' WHERE provider_code IS NULL;
UPDATE payment.payment_transaction SET provider_description = 'legacy verified payment' WHERE provider_description IS NULL;
ALTER TABLE payment.payment_transaction
    ALTER COLUMN provider_code SET NOT NULL,
    ALTER COLUMN provider_description SET NOT NULL;

ALTER TABLE payment.outbox_message
    ADD COLUMN IF NOT EXISTS event_key text,
    ADD COLUMN IF NOT EXISTS event_type text,
    ALTER COLUMN user_id TYPE bigint USING user_id::bigint,
    ALTER COLUMN target_valid_date DROP NOT NULL;
UPDATE payment.outbox_message message
SET event_key = 'premium-activation:' || payment_order.order_code,
    event_type = 'ACTIVATE_PREMIUM'
FROM payment.payment_order
WHERE payment_order.id = message.order_id
  AND (message.event_key IS NULL OR message.event_type IS NULL);
ALTER TABLE payment.outbox_message
    ALTER COLUMN event_key SET NOT NULL,
    ALTER COLUMN event_type SET NOT NULL;
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_outbox_event_type'
          AND conrelid = 'payment.outbox_message'::regclass
    ) THEN
        ALTER TABLE payment.outbox_message
            ADD CONSTRAINT ck_outbox_event_type CHECK (event_type = 'ACTIVATE_PREMIUM');
    END IF;
END $$;

WITH ranked AS (
    SELECT id, row_number() OVER (PARTITION BY order_id ORDER BY paid_at, created_at, id) AS position
    FROM payment.payment_transaction
)
UPDATE payment.payment_transaction payment_row
SET is_canonical = (ranked.position = 1)
FROM ranked
WHERE ranked.id = payment_row.id;
CREATE UNIQUE INDEX IF NOT EXISTS ux_payment_transaction_canonical_order
    ON payment.payment_transaction (order_id) WHERE is_canonical;
CREATE UNIQUE INDEX IF NOT EXISTS ux_outbox_event_key
    ON payment.outbox_message (event_key);
