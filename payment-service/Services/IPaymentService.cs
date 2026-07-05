using payment_service.Contracts;
using payment_service.Models;

namespace payment_service.Services
{
    public interface IPaymentService
    {
        Task<PaymentTransaction> ProcessPaymentAsync(PaymentRequest request);
    }
}
