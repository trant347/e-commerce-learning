using Microsoft.AspNetCore.Mvc;
using Moq;
using payment_service.Controllers;
using payment_service.Models;
using payment_service.Services;
using Xunit;

namespace payment_service.Tests
{
    /// <summary>
    /// Covers the <c>GET /api/payment/transaction/{sagaId}</c> endpoint added for
    /// PAYMENT_SAGA_SPEC.md's migration step 3.
    /// </summary>
    public class PaymentControllerTransactionLookupTests
    {
        [Fact]
        public async Task GetTransactionBySagaId_Found_ReturnsOkWithTransaction()
        {
            var sagaId = Guid.NewGuid();
            var transaction = new PaymentTransaction { SagaId = sagaId, Amount = 42m };
            var serviceMock = new Mock<IPaymentService>();
            serviceMock.Setup(s => s.GetTransactionBySagaIdAsync(sagaId)).ReturnsAsync(transaction);
            var controller = new PaymentController(serviceMock.Object);

            var result = await controller.GetTransactionBySagaId(sagaId);

            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.Same(transaction, okResult.Value);
        }

        [Fact]
        public async Task GetTransactionBySagaId_NotFound_ReturnsNotFound()
        {
            var sagaId = Guid.NewGuid();
            var serviceMock = new Mock<IPaymentService>();
            serviceMock.Setup(s => s.GetTransactionBySagaIdAsync(sagaId)).ReturnsAsync((PaymentTransaction?)null);
            var controller = new PaymentController(serviceMock.Object);

            var result = await controller.GetTransactionBySagaId(sagaId);

            Assert.IsType<NotFoundResult>(result);
        }
    }
}
