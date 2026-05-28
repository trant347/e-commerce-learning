namespace calendar_service.Auth
{
    public class JwtSettings
    {
        public string Secret { get; set; } = "JwtSecretKey";
        public string Header { get; set; } = "Authorization";
        public string Prefix { get; set; } = "Bearer ";
    }
}
