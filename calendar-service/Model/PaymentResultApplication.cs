namespace calendar_service.Model
{
    public enum PaymentResultApplicationOutcome
    {
        Applied,
        AlreadyApplied
    }

    public sealed record PaymentResultApplication(
        Booking Booking,
        PaymentResultApplicationOutcome Outcome);
}
