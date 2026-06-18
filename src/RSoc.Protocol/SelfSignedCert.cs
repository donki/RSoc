using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RSoc.Protocol;

/// <summary>
/// Genera (o carga) un certificado X.509 autofirmado con clave privada, persistido como PFX.
/// Lo usan el servidor (HTTPS de la API) y el agente (servidor TLS de la sesión sobre el relay).
/// Los clientes aceptan cualquier certificado (autofirmado): el cifrado protege el transporte;
/// la confianza de extremo se delega en el token de relay y la contraseña de conexión.
/// </summary>
public static class SelfSignedCert
{
    public static X509Certificate2 LoadOrCreate(string pfxPath, string subjectCn)
    {
        if (File.Exists(pfxPath))
        {
            try { return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null, X509KeyStorageFlags.Exportable); }
            catch { /* corrupto o ilegible: se regenera */ }
        }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={subjectCn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth

        var now = DateTimeOffset.UtcNow;
        using var cert = req.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));
        var pfx = cert.Export(X509ContentType.Pfx);
        try { File.WriteAllBytes(pfxPath, pfx); } catch { /* sin disco: se usa en memoria */ }
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
    }
}
