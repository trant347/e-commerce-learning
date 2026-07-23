namespace Payment.Contracts.V1;

public static class EscrowStatus
{
    public const string Pending = "PENDING";
    public const string Funded = "FUNDED";
    public const string Released = "RELEASED";
    public const string Refunded = "REFUNDED";
}
