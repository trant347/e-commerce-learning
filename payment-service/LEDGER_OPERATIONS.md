# Ledger Operations

## Deployment and cutover

1. Stop payment consumers and synchronous payment writers.
2. Back up PostgreSQL.
3. Apply EF Core migrations with a database-owner deployment credential.
4. Confirm `LedgerCutover:Enabled` is `true`.
5. Start one payment-service instance. Startup creates opening entries, records
   `LedgerEpochAt`, and blocks if wallet or custody reconciliation fails.
6. Start the remaining instances. They verify the existing cutover instead of repeating it.
7. Confirm `/health` reports the `ledger` check as healthy before resuming traffic.

Leave `LedgerCutover:Enabled` enabled during the rollout so every restart verifies the cutover.
The operation is idempotent and must never be repaired by editing or deleting journal rows.
Keep `LegacyPayments:AllowUnledgeredPaymentsWithoutParties` set to `false`; requests without
both wallet identities must not bypass the journal after cutover.

## Runtime database role

Use separate migration-owner and runtime credentials. The runtime role should not own ledger
tables or have `UPDATE`, `DELETE`, or `TRUNCATE` privileges on journal history.

Example role grants, executed by the database owner:

```sql
CREATE ROLE payment_service_app LOGIN PASSWORD '<managed-secret>';
GRANT CONNECT ON DATABASE paymentdb TO payment_service_app;
GRANT USAGE ON SCHEMA public TO payment_service_app;

GRANT SELECT, INSERT ON journal_entries, journal_lines TO payment_service_app;
REVOKE UPDATE, DELETE, TRUNCATE
    ON journal_entries, journal_lines
    FROM payment_service_app;

GRANT SELECT, INSERT, UPDATE
    ON ledger_accounts, user_wallets, payment_transactions, escrows,
       payment_result_outbox, payment_method_tokens, ledger_cutover_state
    TO payment_service_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO payment_service_app;
```

Store the runtime password in the deployment secret manager, not in repository configuration.
Run future migrations before starting the service because the restricted runtime role is not a
schema owner.

## Monitoring and alerts

Alert immediately when `/health` reports the `ledger` check as unhealthy or logs contain
`Ledger reconciliation failed`.

Monitor these OpenTelemetry metrics:

| Metric | Alert condition |
|---|---|
| `payment_ledger.anomalies` | Any value greater than zero |
| `payment_ledger.projection.mismatch` | Any non-zero value |
| `payment_ledger.reconciliation.unbalanced_entries` | Any value greater than zero |
| `payment_ledger.reconciliation.missing_links` | Any value greater than zero |
| `payment_ledger.reconciliation.oldest_unreconciled_age` | Increasing across two reconciliation passes |
| `payment_ledger.posting.failures` | Sustained increase, grouped by `reason` |
| `payment_saga.custody.mismatch` | Any non-zero value |

Posting metrics include `operation`, `currency`, and `outcome` attributes. Do not place card
numbers, CVVs, tokens, credentials, or authorization headers in metric attributes, descriptions,
or logs.

## Incident response

1. Pause payment writers.
2. Preserve logs and a database snapshot.
3. Compare cached balances with journal-derived balances and funded escrow value.
4. Identify the first affected journal entry, transaction, saga, booking, and escrow.
5. Correct an erroneous posting with a new reversal and corrected entry. Never update or delete
   posted journal history.
6. Resume traffic only after reconciliation and the ledger health check are healthy.
