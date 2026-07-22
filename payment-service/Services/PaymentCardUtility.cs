namespace payment_service.Services
{
    public static class PaymentCardUtility
    {
        public static string ValidateAndNormalizeNumber(string cardNumber)
        {
            var normalized = new string((cardNumber ?? string.Empty)
                .Where(character => character != ' ' && character != '-')
                .ToArray());

            if (normalized.Length is < 12 or > 19 || normalized.Any(character => !char.IsDigit(character)))
            {
                throw new ArgumentException("Card number must contain 12 to 19 digits.", nameof(cardNumber));
            }

            var sum = 0;
            var doubleDigit = false;
            for (var index = normalized.Length - 1; index >= 0; index--)
            {
                var digit = normalized[index] - '0';
                if (doubleDigit)
                {
                    digit *= 2;
                    if (digit > 9)
                    {
                        digit -= 9;
                    }
                }

                sum += digit;
                doubleDigit = !doubleDigit;
            }

            if (sum % 10 != 0)
            {
                throw new ArgumentException("Card number is invalid.", nameof(cardNumber));
            }

            return normalized;
        }

        public static void ValidateExpiryDate(string expiryDate, DateTime utcNow)
        {
            var parts = (expiryDate ?? string.Empty).Split('/');
            if (parts.Length != 2
                || !int.TryParse(parts[0], out var month)
                || !int.TryParse(parts[1], out var year)
                || month is < 1 or > 12)
            {
                throw new ArgumentException("Expiry date must use MM/YY or MM/YYYY format.", nameof(expiryDate));
            }

            if (year is >= 0 and <= 99)
            {
                year += 2000;
            }
            else if (year < 2000 || year > 9999)
            {
                throw new ArgumentException("Expiry date must use MM/YY or MM/YYYY format.", nameof(expiryDate));
            }

            var lastValidDay = new DateTime(
                year,
                month,
                DateTime.DaysInMonth(year, month),
                23,
                59,
                59,
                DateTimeKind.Utc);
            if (lastValidDay < utcNow)
            {
                throw new ArgumentException("Card has expired.", nameof(expiryDate));
            }
        }

        public static void ValidateCvv(string cvv)
        {
            if (string.IsNullOrWhiteSpace(cvv)
                || cvv.Length is < 3 or > 4
                || cvv.Any(character => !char.IsDigit(character)))
            {
                throw new ArgumentException("CVV must contain 3 or 4 digits.", nameof(cvv));
            }
        }

        public static string ValidateOwnerName(string ownerName)
        {
            var normalized = ownerName?.Trim() ?? string.Empty;
            if (normalized.Length is < 1 or > 200)
            {
                throw new ArgumentException("Owner name is required and cannot exceed 200 characters.", nameof(ownerName));
            }

            return normalized;
        }

        public static string Mask(string? cardNumber) =>
            string.IsNullOrEmpty(cardNumber) || cardNumber.Length <= 4
                ? "****"
                : new string('*', cardNumber.Length - 4) + cardNumber[^4..];
    }
}
