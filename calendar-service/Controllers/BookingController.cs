using calendar_service.MessageQueue;
using calendar_service.Model;
using calendar_service.Services.Contracts;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace calendar_service.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BookingController : ControllerBase
{

    private readonly IBookingService bookingService;
    private readonly IBookingProducer<string, Booking> _bookingProducer;

    public BookingController(IBookingService bookingService, IBookingProducer<string, Booking> bookingProducer)
    {
        this.bookingService = bookingService;
        this._bookingProducer = bookingProducer;
    }

    [HttpPost("")]
    public async Task<IActionResult> CreateBooking([FromBody] Booking booking)
    {
        if (booking == null)
        {
            return BadRequest("Empty booking is not valid");
        }

        try
        {
            var addedBooking = await this.bookingService.CreateBookingAsync(booking);
            Console.WriteLine($"Booking created with id: {addedBooking.Id} {addedBooking.Description}");
            await _bookingProducer.ProduceBookingAsync(addedBooking.Id, addedBooking);
            return Ok(booking);
        }
        catch (MongoException e)
        {
            Console.WriteLine($"MongDB error: {e.Message}");
            return StatusCode(500, "An error occured");
        }
        catch (Exception e)
        {
            Console.WriteLine($"Exception error: {e.Message}");
            return StatusCode(500, "An error occured");
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetAlBookings()
    {
        try
        {
            var list = await this.bookingService.GetAllBookingsAsync();
            return Ok(list);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Exception error: {e.Message}");
            return StatusCode(500, "An error occured");
        }
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetBooking(string id)
    {
        try
        {
            var booking = await this.bookingService.GetBookingAsync(id);
            if (booking == null)
            {
                return NotFound();
            }
            return Ok(booking);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Exception error: {e.Message}");
            return StatusCode(500, "An error occured");
        }
    }

}
