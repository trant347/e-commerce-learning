using System.Text.Json;
using System.Text.Json.Serialization;

namespace Payment.Contracts;

public static class PaymentContractJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
