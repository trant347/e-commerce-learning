using System.ComponentModel.DataAnnotations;

namespace payment_service.Models
{
    public class PaymentMethodTokenRecord
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [MaxLength(64)]
        public string TokenHash { get; set; } = string.Empty;

        [MaxLength(32)]
        public string MaskedCardNumber { get; set; } = string.Empty;

        [MaxLength(200)]
        public string OwnerName { get; set; } = string.Empty;

        public bool SimulatesDecline { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime? RedeemedAt { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
