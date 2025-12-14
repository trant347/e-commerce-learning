using worker_service.Contracts;
using worker_service.DAO;
using worker_service.Model;

namespace worker_service.Services
{
    public class ProcessBookingService : IProcessBookingService
    {
        private readonly ILogger<ProcessBookingService> _logger;
        private readonly IBookingService _bookingService;

        public ProcessBookingService(ILogger<ProcessBookingService> logger, IBookingService bookingService)
        {
            _logger = logger;
            _bookingService = bookingService;
        }

        public async Task<BookingResult> ProcessBookingAsync(BookingJobMessage bookingMessage)
        {
            if (bookingMessage == null)
            {
                throw new ArgumentNullException(nameof(bookingMessage));
            }
            _logger.LogInformation("Processing booking with ID: {BookingId}", bookingMessage.Id);
            try
            {
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
                Booking booking = await _bookingService.GetBookingByIdAsync(bookingMessage.Id);
#pragma warning restore CS8600 // Converting null literal or possible null value to non-nullable type.
                if (booking == null)
                {
                    return new BookingResult
                    {
                        Id = bookingMessage.Id,
                        Description = bookingMessage.Description,
                        Status = "Failed",
                        ErrorMessage = "Booking not found"
                    };
                }
                if (booking.Status == "Completed")
                {
                    return new BookingResult
                    {
                        Id = bookingMessage.Id,
                        Description = bookingMessage.Description,
                        Status = "Completed",
                        ErrorMessage = "Booking has already been processed"
                    };
                }
                if (booking.StartTime > DateTime.UtcNow)
                {
                    return new BookingResult
                    {
                        Id = bookingMessage.Id,
                        Description = bookingMessage.Description,
                        Status = "Failed",
                        ErrorMessage = "Booking start time is invalid"
                    };
                }
                var updatedBooking = new Booking
                {
                    Id = booking.Id,
                    Description = booking.Description,
                    StartTime = booking.StartTime,
                    EndTime = booking.EndTime,
                    Status = "Completed"
                };
                await _bookingService.UpdateBookingAsync(updatedBooking);
                return new BookingResult()
                {
                    Id = bookingMessage.Id,
                    Description = bookingMessage.Description,
                    Status = "Completed",
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing booking with ID: {BookingId}", bookingMessage.Id);
                return new BookingResult
                {
                    Id = bookingMessage.Id,
                    Description = bookingMessage.Description,
                    Status = "Failed",
                    ErrorMessage = ex.Message
                };
            }
        }
    }
}
