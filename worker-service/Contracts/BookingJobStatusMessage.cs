namespace worker_service.Contracts
{
    public class BookingJobStatusMessage
    {
        public string BookingId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty; // e.g., "Pending", "Confirmed", "Cancelled"
        public string Message { get; set; } = string.Empty;
    }
}
