using payment_service.Models;

namespace payment_service.Services
{
    public sealed record LedgerPostingResult(
        JournalEntry JournalEntry,
        bool WasAlreadyPosted);
}
