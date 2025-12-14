namespace worker_service.Contracts
{
    public class BookingJobMessage
    {
        public string Id { get; set; } = string.Empty;
        public string Description { get; set; }
        public string UserId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
    }
}
