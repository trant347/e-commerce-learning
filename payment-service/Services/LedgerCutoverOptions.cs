namespace payment_service.Services
{
    public sealed class LedgerCutoverOptions
    {
        public bool Enabled { get; set; }

        public string Currency { get; set; } = "USD";
    }
}
