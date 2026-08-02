# Immutable Double-Entry Wallet Journal - Refactor Specification

> Status: **Proposed.**
>
> Scope: `payment-service`, its PostgreSQL schema, and payment-service tests. Existing
> calendar-service saga contracts and frontend payment behavior remain compatible.

## 1. Purpose

Replace mutable wallet balances as the financial source of truth with an append-only,
double-entry journal.

Every approved money movement must:

1. Create one journal entry.
2. Create balanced debit and credit lines whose totals are equal.
3. Commit the journal, wallet balance projection, payment transaction, escrow transition, and
   payment-result outbox in one PostgreSQL transaction.
4. Be idempotent when a saga or request is retried.

Wallet balances may remain materialized for fast reads and insufficient-funds checks, but they
become a rebuildable projection. The immutable journal is authoritative.

## 2. Current implementation

The payment service currently stores:

- `user_wallets`: one mutable `Balance` per user.
- `payment_transactions`: an audit record for approved and declined payment attempts.
- `escrows`: the authoritative per-booking escrow lifecycle and transaction links.
- `payment_result_outbox`: durable publication of asynchronous payment results.

Approved payments currently mutate balances directly:

```text
payer.Balance -= amount
payee.Balance += amount
```

This occurs in two paths:

- `PaymentRequestProcessor` for asynchronous escrow funding, release, and refund.
- `WalletSimulationPaymentGateway` for the legacy synchronous payment flow.

The mutations and payment records are transactional, but the previous wallet values are
overwritten. A direct or accidental balance update has no mandatory balancing record.

## 3. Goals

- Preserve every approved movement as immutable accounting history.
- Guarantee that total debits equal total credits for every posted entry.
- Prevent duplicate postings when a request or Kafka message is retried.
- Continue rejecting payments that would make a user or custody wallet negative.
- Reconstruct a wallet balance at the current time or at a historical timestamp.
- Rebuild and verify cached balances from journal lines.
- Link journal entries to payment transactions, sagas, bookings, and escrows.
- Support corrections through explicit reversing entries rather than updates or deletes.
- Preserve current payment APIs and saga message contracts unless an additive field is useful.
- Keep declined attempts in `payment_transactions` without creating money movement entries.

## 4. Non-goals

- Implementing a complete general-ledger or GAAP accounting platform.
- Supporting multiple currencies in one account.
- Currency conversion or exchange-rate accounting.
- Editing or deleting posted financial history.
- Replacing `payment_transactions`, `escrows`, or the saga outbox.
- Reconstructing pre-cutover history when the existing data cannot prove every historical
  balance mutation.

## 5. Accounting model

This is a closed stored-value subledger. Wallet-like accounts use the following convention:

- A **credit** increases the account's available balance.
- A **debit** decreases the account's available balance.
- Current balance = total credits - total debits.

Every entry is balanced:

```text
sum(debit lines) = sum(credit lines)
```

### 5.1 Account types

| Type | Purpose |
|---|---|
| `USER_WALLET` | One account per user and currency |
| `ESCROW_CUSTODY` | Configured custody account holding funded escrow value |
| `SYSTEM_ISSUANCE` | **Simulation/mock only.** Offset account for the artificial starting balance granted to test users |

`SYSTEM_ISSUANCE` exists only because this learning project creates simulated money when a user
registers. It is not intended to represent a real bank, payment provider, or production funding
source. A real deployment must replace it with explicitly modeled external funding, promotional
credit, cash, or settlement accounts. The simulated issuance account may have a negative net
balance. User and custody accounts must never have negative available balances.

### 5.2 Posting examples

Create a user's simulated/mock starting balance:

| Account | Direction | Amount |
|---|---|---:|
| System issuance | Debit | 1,000.00 USD |
| User wallet | Credit | 1,000.00 USD |

Fund escrow:

| Account | Direction | Amount |
|---|---|---:|
| Requester wallet | Debit | 100.00 USD |
| Escrow custody | Credit | 100.00 USD |

Release escrow:

| Account | Direction | Amount |
|---|---|---:|
| Escrow custody | Debit | 100.00 USD |
| TaskMaster wallet | Credit | 100.00 USD |

Refund escrow:

| Account | Direction | Amount |
|---|---|---:|
| Escrow custody | Debit | 100.00 USD |
| Requester wallet | Credit | 100.00 USD |

## 6. Proposed PostgreSQL schema

### 6.1 `ledger_accounts`

| Column | Type | Rules |
|---|---|---|
| `Id` | UUID | Primary key |
| `OwnerUserId` | varchar(200), nullable | User or configured custody identifier |
| `AccountType` | varchar(30) | Valid account type |
| `Currency` | varchar(3) | Uppercase ISO-style code |
| `Status` | varchar(20) | `ACTIVE` or `CLOSED` |
| `CreatedAt` | timestamptz | Required |
| `ClosedAt` | timestamptz, nullable | Set only when closed |

Constraints and indexes:

- Unique `(OwnerUserId, AccountType, Currency)` when `OwnerUserId` is not null.
- One simulation-only `SYSTEM_ISSUANCE` account per currency.
- An account's currency and type cannot change after creation.
- Closed accounts remain queryable but cannot receive new postings.

### 6.2 `journal_entries`

| Column | Type | Rules |
|---|---|---|
| `Id` | UUID | Primary key |
| `IdempotencyKey` | varchar(200) | Unique |
| `PaymentTransactionId` | UUID, nullable | Unique link to `payment_transactions` |
| `SagaId` | UUID, nullable | Indexed audit link |
| `EscrowId` | UUID, nullable | Indexed audit link |
| `BookingId` | varchar(100), nullable | Indexed audit link |
| `Operation` | varchar(30) | Posting operation |
| `Currency` | varchar(3) | Must match every account line |
| `Description` | varchar(500) | Non-sensitive audit description |
| `ReversesJournalEntryId` | UUID, nullable | Unique link to the entry being reversed |
| `CreatedAt` | timestamptz | Required posting time |

Suggested operations:

- `OPENING_BALANCE`
- `USER_REGISTRATION_CREDIT`
- `LEGACY_PAYMENT`
- `FUND_ESCROW`
- `RELEASE_ESCROW`
- `REFUND_ESCROW`
- `REVERSAL`
- `ADMIN_ADJUSTMENT`

There is no `UpdatedAt`. Posted entries cannot be changed.

### 6.3 `journal_lines`

| Column | Type | Rules |
|---|---|---|
| `Id` | UUID | Primary key |
| `JournalEntryId` | UUID | Required FK to `journal_entries` |
| `LineNumber` | smallint | Positive and unique within the entry |
| `AccountId` | UUID | Required FK to `ledger_accounts` |
| `Direction` | varchar(6) | `DEBIT` or `CREDIT` |
| `Amount` | numeric(18,2) | Greater than zero |
| `CreatedAt` | timestamptz | Same posting time as the entry |

Constraints and indexes:

- Unique `(JournalEntryId, LineNumber)`.
- Index `(AccountId, CreatedAt, Id)` for balance and history queries.
- Foreign keys use `ON DELETE RESTRICT`.
- An entry must contain at least two lines.
- Every line account must have the same currency as the journal entry.
- Debit total must equal credit total.

### 6.4 Wallet balance projection

Retain `user_wallets` during the migration, but change its meaning:

| Existing/new column | Purpose |
|---|---|
| `UserId` | Existing wallet identity |
| `LedgerAccountId` | Unique FK to `ledger_accounts` |
| `Balance` | Cached current balance, not source of truth |
| `ProjectionVersion` | Monotonic count or sequence of applied postings |
| `LastJournalEntryId` | Last entry included in the cached balance |
| `CreatedAt`, `UpdatedAt` | Projection metadata |

The final model may rename `Balance` to `CachedBalance`, but that rename is not required during
the first rollout. Application code must stop assigning it outside the ledger posting service.

## 7. Database invariants

Application validation alone is insufficient. PostgreSQL must enforce:

1. Journal entries and lines cannot be updated or deleted after insertion.
2. Every committed entry has at least two lines.
3. Every committed entry is balanced.
4. Every line amount is positive.
5. Every line uses an account with the entry's currency.
6. Account identity, type, and currency are immutable.
7. `IdempotencyKey` is unique.
8. `PaymentTransactionId` is unique when present.
9. A journal entry can be reversed at most once.
10. A reversal has equal and opposite lines to the original entry.

Use PostgreSQL triggers or a database posting function for cross-row invariants that cannot be
expressed as normal check constraints. The balance check must be deferred until transaction
commit so all lines can be inserted before validation.

Add append-only triggers that reject `UPDATE` and `DELETE` on `journal_entries` and
`journal_lines`. Migrations may temporarily disable these protections only through an explicit,
documented administrative procedure.

## 8. Application design

### 8.1 Ledger posting service

Add an `ILedgerService` implemented by `LedgerService`.

Primary operation:

```csharp
Task<LedgerPostingResult> PostTransferAsync(
    LedgerTransfer transfer,
    CancellationToken cancellationToken = default);
```

`LedgerTransfer` includes:

- Idempotency key
- Payment transaction ID
- Optional saga, escrow, and booking IDs
- Operation
- Currency and amount
- Debit account owner/type
- Credit account owner/type
- Description
- Posting timestamp supplied through `TimeProvider`

The service must:

1. Validate identifiers, amount, currency, operation, and distinct accounts.
2. Load and lock involved account projections in deterministic account-ID order.
3. Return the existing posting when the idempotency key already exists and the request matches.
4. Reject reuse of an idempotency key with different posting terms.
5. Check the debit account's projected available balance.
6. Insert the journal entry and balanced lines.
7. Update cached balances and projection metadata.
8. Participate in the caller's existing EF/PostgreSQL transaction.

It must not independently commit when called from payment processing.

### 8.2 Account service

Add an `ILedgerAccountService` responsible for:

- Idempotently creating user wallet accounts.
- Creating the configured custody account at zero.
- Creating one simulation-only system issuance account per currency.
- Returning account metadata without exposing mutation methods.

Creating a normal user wallet posts a `USER_REGISTRATION_CREDIT` entry from the simulation-only
system issuance account to the new wallet. Duplicate `USER_REGISTERED` events must return the
existing account and must not post the mocked starting balance twice.

### 8.3 Balance query service

Wallet reads should normally return the cached projection. Add an internal authoritative query:

```text
balance(account, asOf) =
    sum(CREDIT amounts at or before asOf)
  - sum(DEBIT amounts at or before asOf)
```

Required query operations:

- Current projected balance.
- Current journal-derived balance.
- Historical balance as of a timestamp.
- Paginated account statement ordered by `(CreatedAt, JournalEntryId, LineNumber)`.

The public wallet response may remain backward compatible with `UserId`, `Balance`,
`CreatedAt`, and `UpdatedAt`.

## 9. Payment flow changes

### 9.1 Asynchronous escrow processor

Refactor `PaymentRequestProcessor`:

- Replace direct `payer.Balance` and `payee.Balance` assignments with one ledger transfer.
- Use `SagaId` as the posting idempotency key, prefixed by operation if stored as text.
- Continue creating one `PaymentTransaction` for approved or declined attempts.
- Create no journal entry for a decline.
- For approval, commit these together:
  - Payment transaction
  - Journal entry and lines
  - Cached wallet projection updates
  - Escrow state transition
  - Payment result outbox row

Any failure must roll back every item.

### 9.2 Legacy synchronous gateway

Refactor `WalletSimulationPaymentGateway` so it no longer mutates wallets directly.

- Require a stable idempotency key from `PaymentService`.
- Post a `LEGACY_PAYMENT` transfer through `ILedgerService`.
- Preserve simulated-card and insufficient-funds declines.
- Preserve the existing compatibility behavior for requests without a payer only until all
  callers provide wallet identities.
- Requests without a payer or payee cannot create a balanced wallet transfer. They should either:
  - be explicitly modeled against a configured external-clearing account, or
  - remain outside the wallet ledger behind a temporary compatibility flag.

The compatibility flag must default off after migration.

### 9.3 Escrow ledger

The existing `escrows` table remains the source of truth for booking-level ownership and state.
The financial journal records actual movement.

Each funded/released/refunded escrow transaction must link to exactly one approved payment
transaction and one journal entry with matching:

- Escrow ID
- Booking ID
- Operation
- Amount
- Currency
- Payer
- Payee

## 10. Concurrency and idempotency

- Lock account projections in deterministic account-ID order to avoid deadlocks.
- Perform the available-balance check while holding the debit account lock.
- Use the journal idempotency unique constraint as the final duplicate-posting defense.
- A concurrent duplicate that loses the insert race must reload and validate the winning entry.
- Reusing a key for different accounts, amount, currency, or operation is an error.
- A duplicate saga returns the original payment result and does not add journal lines.
- Database transaction isolation must prevent two concurrent debits from spending the same
  available balance.

## 11. Corrections and reversals

Posted rows are never edited.

A normal business refund is not an accounting reversal. For example, when a requester cancels a
funded booking, the system posts a `REFUND_ESCROW` entry that debits custody and credits the
requester. The original funding entry remains valid historical evidence that custody previously
received the money.

A `REVERSAL` is reserved for correcting an erroneous posting, such as transferring the wrong
amount or crediting the wrong account. It explicitly cancels the financial effect of that
incorrect journal entry.

To correct an approved transfer:

1. Create a new `REVERSAL` journal entry.
2. Reference the original through `ReversesJournalEntryId`.
3. Copy every original line with the opposite direction.
4. Use a new idempotency key.
5. Apply normal balance and concurrency checks.
6. Record the business reason in structured audit metadata or description.

If a corrected transfer is still required, post it as a third entry after the reversal.

Administrative adjustment endpoints are out of scope until authorization, reason codes,
approval policy, and audit logging are explicitly designed.

## 12. Migration and rollout

Historical mutable balances cannot automatically prove every movement, especially for lazy
wallet creation and legacy requests without complete payer/payee identifiers. Do not invent
historical journal detail.

### Phase 1 - Add schema and shadow posting

- Add ledger tables and EF models.
- Add immutable/balance database protections.
- Add ledger services and tests.
- Continue serving reads from current wallet balances.
- In non-production environments, dual-write new approved movements to the journal and existing
  projections in one transaction.
- Compare journal deltas with projection deltas.

### Phase 2 - Establish a cutover epoch

- Pause payment consumers and synchronous payment writes.
- Record a `LedgerEpochAt` timestamp.
- Create ledger accounts for every existing wallet.
- For each wallet, create an `OPENING_BALANCE` entry equal to its balance at cutover, offset
  against the simulation-only system issuance account.
- Link every wallet projection to its ledger account.
- Verify journal-derived balance equals every stored balance.
- Verify custody balance equals funded escrow value.
- Resume writes with journal posting mandatory.

Pre-cutover `payment_transactions` remain the historical audit source. The journal is
authoritative beginning at `LedgerEpochAt`.

### Phase 3 - Make journal authoritative

- Serve balance reads from the cached projection backed by the journal.
- Run continuous projection-to-journal reconciliation.
- Remove all direct balance mutation code.
- Add database permissions or triggers preventing application roles from directly changing
  wallet balances outside the approved projection update path.
- Disable the temporary legacy no-payer compatibility path.

### Phase 4 - Optional optimization

- Rename `Balance` to `CachedBalance`.
- Add periodic balance snapshots for faster long-range historical queries.
- Partition `journal_lines` by posting date only if table size and query measurements justify it.
- Add archival policies that preserve journal immutability and audit access.

## 13. Reconciliation and anomaly detection

Extend `CustodyReconciliationWorker` or introduce `LedgerReconciliationWorker` to check:

1. Every journal entry is balanced.
2. Every cached wallet balance equals its journal-derived balance.
3. Total custody journal balance equals total value of `FUNDED` escrows.
4. Every approved payment transaction that moves money has exactly one journal entry.
5. Declined payment transactions have no journal entry.
6. Escrow transaction IDs link to matching approved transactions and journal entries.
7. No escrow has both release and refund postings.
8. No user or custody projection is negative.
9. No journal posting uses a closed account.
10. No journal rows have been updated or deleted.

Emit metrics for:

- Reconciliation mismatch count and value
- Unbalanced entry count
- Missing or duplicate journal link count
- Projection lag
- Posting and reversal counts by operation/currency
- Posting failures by reason
- Oldest unreconciled entry age

Critical financial invariant failures must log at critical severity and produce an operational
alert. They must not be silently repaired.

## 14. Security and audit requirements

- Never store card numbers, CVVs, payment tokens, credentials, or authorization headers in
  ledger descriptions or metadata.
- Use structured identifiers rather than free-form sensitive data.
- Restrict journal write permissions to the payment-service application role.
- Restrict direct table access for normal application paths.
- Record reversals and future administrative adjustments with actor and reason identifiers.
- Journal query endpoints require explicit authorization and pagination.
- Logs may contain journal, saga, escrow, booking, and masked transaction identifiers, but no
  payment secrets.

## 15. Testing requirements

### Unit and integration tests

- User registration creates exactly one starting-balance entry.
- Duplicate registration does not duplicate the credit.
- Approved transfer creates one balanced entry and two lines.
- Declined transfer creates no journal entry or balance movement.
- Fund, release, and refund create the expected account lines.
- Duplicate saga returns the original result without posting again.
- Conflicting idempotency-key reuse is rejected.
- Concurrent debits cannot overdraw an account.
- Concurrent transfers lock accounts without deadlock.
- Journal, escrow, transaction, and outbox changes roll back together on failure.
- Reversal creates exact opposite lines and cannot be repeated.
- Journal rows reject update and delete.
- Cross-currency postings are rejected.
- Closed accounts reject postings.
- Historical balance queries return the correct value at each timestamp.
- Projection rebuild reproduces the cached current balance.

### Migration tests

- Every existing wallet receives one account and one opening-balance entry.
- Zero-balance custody accounts migrate correctly.
- Migration is idempotent when restarted.
- Cutover reconciliation blocks startup on any mismatch.
- Pre-cutover transactions remain queryable.
- New post-cutover transactions link to journal entries.

### End-to-end tests

- Funding moves value from requester to custody.
- Release moves value from custody to TaskMaster.
- Refund moves value from custody back to requester.
- Browser-visible balances remain backward compatible.
- Kafka retries do not duplicate journal postings.
- Service restart between database commit and Kafka publication does not duplicate movement.

## 16. Acceptance criteria

The refactor is complete when:

- No production code directly increments or decrements `UserWallet.Balance`.
- Every approved wallet movement has exactly one immutable, balanced journal entry.
- Every declined attempt has no journal movement.
- Journal, payment transaction, escrow transition, projection, and outbox commit atomically.
- Current and historical balances can be calculated from the journal.
- Cached balances can be rebuilt and continuously reconcile to the journal.
- Duplicate and concurrent requests cannot double-spend or double-post.
- Posted journal rows cannot be updated or deleted by the application role.
- Custody journal balance reconciles with all funded escrows.
- Existing payment and wallet APIs remain compatible through rollout.

## 17. Implementation task breakdown

1. Add ledger account, journal entry, and journal line models and migrations.
2. Add PostgreSQL append-only and deferred balancing protections.
3. Implement account creation and starting-balance issuance.
4. Implement idempotent transfer posting and balance projection updates.
5. Refactor `PaymentRequestProcessor` to post through the ledger.
6. Refactor `WalletSimulationPaymentGateway` and legacy payment idempotency.
7. Add authoritative balance and account-statement queries.
8. Add opening-balance cutover migration and reconciliation tooling.
9. Extend custody reconciliation to journal and projection invariants.
10. Add observability, alerts, security restrictions, and operational documentation.
11. Execute phased rollout and remove all direct balance mutation paths.
