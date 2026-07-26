using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using payment_service.Data;
using payment_service.Models;
using Payment.Contracts.V1;

namespace payment_service.Services
{
    public sealed class PaymentRequestProcessor : IPaymentRequestProcessor
    {
        private readonly PaymentDbContext _dbContext;
        private readonly IPaymentMethodTokenService _paymentMethodTokens;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<PaymentRequestProcessor> _logger;

        public PaymentRequestProcessor(
            PaymentDbContext dbContext,
            IPaymentMethodTokenService paymentMethodTokens,
            TimeProvider timeProvider,
            ILogger<PaymentRequestProcessor> logger)
        {
            _dbContext = dbContext;
            _paymentMethodTokens = paymentMethodTokens;
            _timeProvider = timeProvider;
            _logger = logger;
        }

        public async Task<PaymentResultV1> ProcessAsync(
            PaymentRequestedV1 request,
            CancellationToken cancellationToken = default)
        {
            request = NormalizeAndValidate(request);

            var duplicate = await _dbContext.Transactions
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    transaction => transaction.SagaId == request.SagaId,
                    cancellationToken);
            if (duplicate != null)
            {
                EnsureSameCommand(duplicate, request);
                return ToResult(duplicate);
            }

            IDbContextTransaction? dbTransaction = null;
            if (_dbContext.Database.IsRelational())
            {
                dbTransaction = await _dbContext.Database.BeginTransactionAsync(
                    cancellationToken);
            }

            try
            {
                var result = await ProcessNewAsync(request, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);
                if (dbTransaction != null)
                {
                    await dbTransaction.CommitAsync(cancellationToken);
                }

                return result;
            }
            catch (DbUpdateException)
            {
                if (dbTransaction != null)
                {
                    await dbTransaction.RollbackAsync(cancellationToken);
                }

                _dbContext.ChangeTracker.Clear();
                duplicate = await _dbContext.Transactions
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        transaction => transaction.SagaId == request.SagaId,
                        cancellationToken);
                if (duplicate == null)
                {
                    throw;
                }

                EnsureSameCommand(duplicate, request);
                _logger.LogInformation(
                    "Deduped concurrent escrow command sagaId={SagaId} transactionId={TransactionId}",
                    request.SagaId,
                    duplicate.Id);
                return ToResult(duplicate);
            }
            catch
            {
                if (dbTransaction != null)
                {
                    await dbTransaction.RollbackAsync(cancellationToken);
                }

                throw;
            }
            finally
            {
                if (dbTransaction != null)
                {
                    await dbTransaction.DisposeAsync();
                }
            }
        }

        private async Task<PaymentResultV1> ProcessNewAsync(
            PaymentRequestedV1 request,
            CancellationToken cancellationToken)
        {
            var transaction = NewTransaction(request);
            EscrowRecord escrow;

            if (request.Operation == PaymentOperation.FundEscrow)
            {
                escrow = await GetEscrowForUpdateAsync(
                    request.EscrowId,
                    cancellationToken)
                    ?? CreatePendingEscrow(request);
                ValidateEscrowTerms(escrow, request);
                RequireEscrowStatus(escrow, EscrowRecord.StatusPending, request.Operation);

                RedeemedPaymentMethod redeemed;
                try
                {
                    redeemed = await _paymentMethodTokens.RedeemAsync(
                        request.PaymentMethodToken!,
                        cancellationToken);
                }
                catch (PaymentMethodTokenException ex)
                {
                    Decline(transaction, ex.Message);
                    _dbContext.Transactions.Add(transaction);
                    return ToResult(transaction);
                }

                transaction.MaskedCardNumber = redeemed.MaskedCardNumber;
                transaction.OwnerName = redeemed.OwnerName;
                if (redeemed.SimulatesDecline)
                {
                    Decline(transaction, "Simulated decline test card");
                    _dbContext.Transactions.Add(transaction);
                    return ToResult(transaction);
                }
            }
            else
            {
                escrow = await GetEscrowForUpdateAsync(
                    request.EscrowId,
                    cancellationToken)
                    ?? throw new KeyNotFoundException(
                        $"Escrow '{request.EscrowId:D}' was not found.");
                ValidateEscrowTerms(escrow, request);
                RequireEscrowStatus(escrow, EscrowRecord.StatusFunded, request.Operation);
                ValidateTransferParties(escrow, request);
            }

            var wallets = await LockWalletsAsync(
                request.PayerUserId,
                request.PayeeUserId,
                cancellationToken);
            var payer = wallets[request.PayerUserId];
            var payee = wallets[request.PayeeUserId];
            if (payer.Balance < request.Amount)
            {
                Decline(
                    transaction,
                    $"Insufficient balance for {request.PayerUserId}: "
                    + $"{payer.Balance:F2} {request.Currency} available, "
                    + $"{request.Amount:F2} {request.Currency} required.");
                _dbContext.Transactions.Add(transaction);
                return ToResult(transaction);
            }

            var now = UtcNow();
            payer.Balance -= request.Amount;
            payer.UpdatedAt = now;
            payee.Balance += request.Amount;
            payee.UpdatedAt = now;
            ApplyEscrowTransition(escrow, transaction.Id, request.Operation, now);

            transaction.Status = PaymentTransaction.StatusApproved;
            _dbContext.Transactions.Add(transaction);
            return ToResult(transaction);
        }

        private EscrowRecord CreatePendingEscrow(PaymentRequestedV1 request)
        {
            var now = UtcNow();
            var escrow = new EscrowRecord
            {
                Id = request.EscrowId,
                BookingId = request.BookingId,
                Amount = request.Amount,
                Currency = request.Currency,
                RequesterUserId = request.PayerUserId,
                TaskMasterUserId = request.TaskMasterUserId,
                CustodyUserId = request.PayeeUserId,
                Status = EscrowRecord.StatusPending,
                CreatedAt = now,
                UpdatedAt = now
            };
            _dbContext.Escrows.Add(escrow);
            return escrow;
        }

        private async Task<EscrowRecord?> GetEscrowForUpdateAsync(
            Guid escrowId,
            CancellationToken cancellationToken)
        {
            if (_dbContext.Database.IsRelational())
            {
                return await _dbContext.Escrows
                    .FromSqlInterpolated(
                        $"SELECT * FROM escrows WHERE \"Id\" = {escrowId} FOR UPDATE")
                    .SingleOrDefaultAsync(cancellationToken);
            }

            return await _dbContext.Escrows.SingleOrDefaultAsync(
                escrow => escrow.Id == escrowId,
                cancellationToken);
        }

        private async Task<Dictionary<string, UserWallet>> LockWalletsAsync(
            string payerUserId,
            string payeeUserId,
            CancellationToken cancellationToken)
        {
            var wallets = new Dictionary<string, UserWallet>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var userId in new[] { payerUserId, payeeUserId }
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(userId => userId, StringComparer.Ordinal))
            {
                UserWallet? wallet;
                if (_dbContext.Database.IsRelational())
                {
                    wallet = await _dbContext.Wallets
                        .FromSqlInterpolated(
                            $"SELECT * FROM user_wallets WHERE \"UserId\" = {userId} FOR UPDATE")
                        .SingleOrDefaultAsync(cancellationToken);
                }
                else
                {
                    wallet = await _dbContext.Wallets.SingleOrDefaultAsync(
                        candidate => candidate.UserId == userId,
                        cancellationToken);
                }

                wallets[userId] = wallet
                    ?? throw new KeyNotFoundException(
                        $"Wallet for user '{userId}' was not found.");
            }

            return wallets;
        }

        private static void ValidateEscrowTerms(
            EscrowRecord escrow,
            PaymentRequestedV1 request)
        {
            if (escrow.BookingId != request.BookingId
                || escrow.Amount != request.Amount
                || !string.Equals(
                    escrow.Currency,
                    request.Currency,
                    StringComparison.Ordinal)
                || !string.Equals(
                    escrow.TaskMasterUserId,
                    request.TaskMasterUserId,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    escrow.CustodyUserId,
                    FundingCustody(request),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Payment command does not match the escrow's immutable details.");
            }

            var requesterFromCommand = request.Operation switch
            {
                PaymentOperation.FundEscrow => request.PayerUserId,
                PaymentOperation.RefundEscrow => request.PayeeUserId,
                _ => null
            };
            if (requesterFromCommand != null
                && !string.Equals(
                    escrow.RequesterUserId,
                    requesterFromCommand,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Payment command does not match the escrow requester.");
            }
        }

        private static string FundingCustody(PaymentRequestedV1 request) =>
            request.Operation == PaymentOperation.FundEscrow
                ? request.PayeeUserId
                : request.PayerUserId;

        private static void ValidateTransferParties(
            EscrowRecord escrow,
            PaymentRequestedV1 request)
        {
            if (request.Operation == PaymentOperation.ReleaseEscrow)
            {
                if (!string.Equals(
                        request.PayerUserId,
                        escrow.CustodyUserId,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        request.PayeeUserId,
                        escrow.TaskMasterUserId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Release command parties do not match the escrow.");
                }

                return;
            }

            if (request.Operation == PaymentOperation.RefundEscrow
                && (!string.Equals(
                        request.PayerUserId,
                        escrow.CustodyUserId,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        request.PayeeUserId,
                        escrow.RequesterUserId,
                        StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    "Refund command parties do not match the escrow.");
            }
        }

        private static void RequireEscrowStatus(
            EscrowRecord escrow,
            string requiredStatus,
            string operation)
        {
            if (escrow.Status != requiredStatus)
            {
                throw new InvalidOperationException(
                    $"{operation} requires escrow status {requiredStatus}, "
                    + $"but escrow is {escrow.Status}.");
            }
        }

        private static void ApplyEscrowTransition(
            EscrowRecord escrow,
            Guid transactionId,
            string operation,
            DateTime now)
        {
            switch (operation)
            {
                case PaymentOperation.FundEscrow:
                    escrow.Status = EscrowRecord.StatusFunded;
                    escrow.FundingTransactionId = transactionId;
                    escrow.FundedAt = now;
                    break;
                case PaymentOperation.ReleaseEscrow:
                    escrow.Status = EscrowRecord.StatusReleased;
                    escrow.ReleaseTransactionId = transactionId;
                    escrow.ReleasedAt = now;
                    break;
                case PaymentOperation.RefundEscrow:
                    escrow.Status = EscrowRecord.StatusRefunded;
                    escrow.RefundTransactionId = transactionId;
                    escrow.RefundedAt = now;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(operation),
                        operation,
                        "Unsupported payment operation.");
            }

            escrow.UpdatedAt = now;
        }

        private PaymentTransaction NewTransaction(PaymentRequestedV1 request) => new()
        {
            Amount = request.Amount,
            Currency = request.Currency,
            MaskedCardNumber = "ESCROW",
            OwnerName = request.PayerUserId,
            Status = PaymentTransaction.StatusApproved,
            SagaId = request.SagaId,
            EscrowId = request.EscrowId,
            BookingId = request.BookingId,
            Operation = request.Operation,
            PayerUserId = request.PayerUserId,
            PayeeUserId = request.PayeeUserId,
            TaskMasterUserId = request.TaskMasterUserId,
            CreatedAt = UtcNow()
        };

        private static void Decline(
            PaymentTransaction transaction,
            string reason)
        {
            transaction.Status = PaymentTransaction.StatusDeclined;
            transaction.DeclineReason = reason.Length <= 200
                ? reason
                : reason[..200];
        }

        private static PaymentRequestedV1 NormalizeAndValidate(
            PaymentRequestedV1 request)
        {
            ArgumentNullException.ThrowIfNull(request);
            request = request with
            {
                BookingId = request.BookingId.Trim(),
                Currency = request.Currency.Trim().ToUpperInvariant(),
                PayerUserId = request.PayerUserId.Trim(),
                PayeeUserId = request.PayeeUserId.Trim(),
                TaskMasterUserId = request.TaskMasterUserId.Trim(),
                PaymentMethodToken = string.IsNullOrWhiteSpace(
                    request.PaymentMethodToken)
                    ? null
                    : request.PaymentMethodToken.Trim()
            };
            request.Validate();

            if (request.SchemaVersion != PaymentRequestedV1.CurrentSchemaVersion)
            {
                throw new ArgumentException(
                    $"Unsupported payment request schema version {request.SchemaVersion}.",
                    nameof(request));
            }
            if (request.SagaId == Guid.Empty || request.EscrowId == Guid.Empty)
            {
                throw new ArgumentException(
                    "SagaId and EscrowId are required.",
                    nameof(request));
            }
            if (string.IsNullOrWhiteSpace(request.BookingId)
                || request.Amount <= 0
                || request.Amount != Math.Round(
                    request.Amount,
                    2,
                    MidpointRounding.ToEven)
                || request.Currency.Length != 3)
            {
                throw new ArgumentException(
                    "Booking, positive two-decimal amount, and three-letter currency are required.",
                    nameof(request));
            }
            if (string.Equals(
                request.PayerUserId,
                request.PayeeUserId,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Payer and payee must be different.",
                    nameof(request));
            }
            if (request.Operation == PaymentOperation.FundEscrow
                && (string.Equals(
                        request.TaskMasterUserId,
                        request.PayerUserId,
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        request.TaskMasterUserId,
                        request.PayeeUserId,
                        StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(
                    "Requester, TaskMaster, and custody accounts must be distinct.",
                    nameof(request));
            }

            return request;
        }

        private static void EnsureSameCommand(
            PaymentTransaction transaction,
            PaymentRequestedV1 request)
        {
            if (transaction.EscrowId != request.EscrowId
                || transaction.BookingId != request.BookingId
                || transaction.Operation != request.Operation
                || transaction.Amount != request.Amount
                || transaction.Currency != request.Currency
                || !string.Equals(
                    transaction.PayerUserId,
                    request.PayerUserId,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    transaction.PayeeUserId,
                    request.PayeeUserId,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    transaction.TaskMasterUserId,
                    request.TaskMasterUserId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SagaId {request.SagaId:D} was already used for a different payment command.");
            }
        }

        private static PaymentResultV1 ToResult(
            PaymentTransaction transaction) => new()
        {
            SagaId = transaction.SagaId
                ?? throw new InvalidOperationException(
                    "Escrow transaction has no SagaId."),
            EscrowId = transaction.EscrowId
                ?? throw new InvalidOperationException(
                    "Escrow transaction has no EscrowId."),
            BookingId = transaction.BookingId
                ?? throw new InvalidOperationException(
                    "Escrow transaction has no BookingId."),
            Operation = transaction.Operation
                ?? throw new InvalidOperationException(
                    "Escrow transaction has no operation."),
            TransactionId = transaction.Id,
            Amount = transaction.Amount,
            Currency = transaction.Currency,
            Status = transaction.Status,
            DeclineReason = transaction.DeclineReason
        };

        private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;
    }
}
