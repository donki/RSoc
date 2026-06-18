namespace RSoc.Client;

/// <summary>
/// Fábrica de <see cref="HttpClient"/> para hablar con la API por HTTPS aceptando el certificado
/// autofirmado del servidor (el cifrado protege el transporte; la API exige siempre HTTPS).
/// </summary>
public static class RSocHttp
{
    public static HttpClient Create(string baseUrl, bool acceptSelfSigned = true, TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler();
        if (acceptSelfSigned)
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        // Si acceptSelfSigned es false, se usa la validación estándar del sistema (exige CA de confianza).
        return new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = timeout ?? TimeSpan.FromSeconds(15),
        };
    }
}
