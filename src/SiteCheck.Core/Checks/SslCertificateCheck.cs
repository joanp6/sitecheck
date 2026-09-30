using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using SiteCheck.Certificates;

namespace SiteCheck.Checks;

/// <summary>
/// Tuning for <see cref="SslCertificateCheck"/>.
/// </summary>
/// <param name="WarnWithinDays">
/// How close to expiry a certificate has to be before it is worth raising. Thirty
/// days is a renewal window a small business can still act on.
/// </param>
public sealed record SslCertificateCheckOptions(int WarnWithinDays = 30)
{
    public int WarnWithinDays { get; } = WarnWithinDays >= 0
        ? WarnWithinDays
        : throw new ArgumentOutOfRangeException(nameof(WarnWithinDays), WarnWithinDays, "The warning window cannot be negative.");
}

/// <summary>
/// Reports whether the site is served over a TLS certificate that is trusted,
/// currently valid, and not about to expire.
/// </summary>
/// <remarks>
/// A host that cannot be reached, or whose handshake breaks off, is a <see cref="CheckStatus.Fail"/>:
/// a visitor could not get in either. That matches <see cref="LoadTimeCheck"/>, so one dead site
/// does not read as a site defect in one line of the report and a tooling gap in the next.
/// Anything unexpected still propagates and becomes an <see cref="CheckStatus.Error"/>.
/// </remarks>
public sealed class SslCertificateCheck : ISiteCheck
{
    private readonly ICertificateProvider _certificates;
    private readonly TimeProvider _timeProvider;
    private readonly SslCertificateCheckOptions _options;

    public SslCertificateCheck(
        ICertificateProvider certificates,
        TimeProvider timeProvider,
        SslCertificateCheckOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _certificates = certificates;
        _timeProvider = timeProvider;
        _options = options ?? new SslCertificateCheckOptions();
    }

    public string Name => "ssl-certificate";

    public async Task<CheckOutcome> RunAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        // An http:// address is checked at its https equivalent rather than failed on sight. Any
        // site worth the name sends http visitors on to https, and whether it does is the
        // https-redirect check's question; this one asks whether the https site they land on has a
        // certificate worth trusting. Failing here instead graded a correctly redirecting site as
        // having no certificate, just because its owner typed the address without the s.
        var secure = url.Scheme == Uri.UriSchemeHttps
            ? url
            : new UriBuilder(url) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;

        CertificateInfo info;

        try
        {
            info = await _certificates.GetAsync(secure, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return CheckOutcome.Fail($"{url.Host} could not be reached, so visitors cannot open the site over HTTPS either.");
        }
        catch (TimeoutException)
        {
            return CheckOutcome.Fail($"{url.Host} did not answer the secure connection in time.");
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException)
        {
            // The handshake itself broke off: a visitor's browser would hit the same wall.
            return CheckOutcome.Fail($"The secure connection to {url.Host} could not be established: {ex.Message}");
        }

        using var certificate = info.Certificate;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var notBefore = certificate.NotBefore.ToUniversalTime();
        var notAfter = certificate.NotAfter.ToUniversalTime();

        // Every outcome from here on knows the expiry date, whatever the verdict, so it is
        // attached once instead of being repeated on each branch — and so that no branch
        // can be added later that forgets it.
        return Evaluate() with { ValidUntil = new DateTimeOffset(notAfter) };

        CheckOutcome Evaluate()
        {
            // Dates before policy errors, and the order matters. Expiry has no flag of its
            // own: it reaches us as RemoteCertificateChainErrors, the same value an unknown
            // issuer produces. Testing the policy errors first answers every expired
            // certificate with the generic "chain is not valid", which sends the owner to
            // replace the wrong thing. See https://github.com/joanp6/sitecheck/issues/1.
            if (now < notBefore)
            {
                return CheckOutcome.Fail($"The certificate is not valid until {Date(notBefore)}, so the site is unreachable over HTTPS today.");
            }

            if (now >= notAfter)
            {
                return CheckOutcome.Fail($"The certificate expired on {Date(notAfter)}, {WholeDaysBetween(notAfter, now)} day(s) ago.");
            }

            if (info.PolicyErrors != SslPolicyErrors.None)
            {
                return CheckOutcome.Fail($"Browsers will not trust the certificate for {url.Host}: {Describe(info.PolicyErrors)}.");
            }

            var daysLeft = WholeDaysBetween(now, notAfter);

            return daysLeft <= _options.WarnWithinDays
                ? CheckOutcome.Warn($"The certificate expires on {Date(notAfter)}, in {daysLeft} day(s). Renew it now.")
                : CheckOutcome.Pass($"The certificate is valid until {Date(notAfter)}, {daysLeft} day(s) from now.");
        }
    }

    private static string Describe(SslPolicyErrors errors)
    {
        var reasons = new List<string>(capacity: 3);

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            reasons.Add("the server presented no certificate");
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            reasons.Add("it was issued for a different host name");
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
        {
            reasons.Add("its chain is not valid (self-signed or an unknown issuer)");
        }

        return string.Join("; ", reasons);
    }

    private static int WholeDaysBetween(DateTime from, DateTime to) => (int)(to - from).TotalDays;

    /// <summary>
    /// Renders a date for a person to read: ISO order, invariant, and explicitly UTC.
    /// </summary>
    /// <remarks>
    /// The zone is spelled out because a certificate that expires just before midnight
    /// reads as the previous day to a reader in UTC+2, and "the date in your report does not
    /// match the one my host shows me" is a support conversation nobody needs. Machines read
    /// <see cref="CheckOutcome.ValidUntil"/> instead and never see this string.
    /// </remarks>
    private static string Date(DateTime value) => value.ToString("yyyy-MM-dd 'UTC'", CultureInfo.InvariantCulture);
}
