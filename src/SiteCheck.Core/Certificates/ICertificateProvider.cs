namespace SiteCheck.Certificates;

/// <summary>
/// Retrieves the TLS certificate a host presents.
/// </summary>
/// <remarks>
/// This seam exists so that <see cref="Checks.SslCertificateCheck"/> holds the
/// certificate policy and nothing else, and can therefore be unit tested against
/// certificates built in memory instead of a live TLS connection.
/// </remarks>
public interface ICertificateProvider
{
    /// <summary>
    /// Connects to the host behind <paramref name="url"/> and returns the certificate
    /// it presents, valid or not.
    /// </summary>
    Task<CertificateInfo> GetAsync(Uri url, CancellationToken cancellationToken = default);
}
