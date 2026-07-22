using payment_service.Contracts;

namespace payment_service.Services
{
    public interface IPaymentMethodTokenService
    {
        Task<PaymentMethodTokenResponse> TokenizeAsync(
            CreditCardInfo creditCard,
            CancellationToken cancellationToken = default);

        Task<RedeemedPaymentMethod> RedeemAsync(
            string paymentMethodToken,
            CancellationToken cancellationToken = default);
    }

    public sealed record RedeemedPaymentMethod(
        string MaskedCardNumber,
        string OwnerName,
        bool SimulatesDecline);
}
