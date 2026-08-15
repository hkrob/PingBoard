using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace PingBoard.Core;

/// <summary>
/// What a target's TLS certificate says about itself.
/// </summary>
/// <param name="Trusted">
/// Whether the certificate validated against the machine's trust store and the requested host
/// name. Recorded rather than enforced — see <see cref="CertificateCheck"/> for why.
/// </param>
/// <param name="Error">Empty on success; otherwise why no certificate could be read.</param>
public readonly record struct CertificateInfo(
    string Subject,
    string Issuer,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    bool Trusted,
    string Error,
    DateTimeOffset CheckedAt)
{
    /// <summary>False when the check failed and there is nothing to report but the error.</summary>
    public bool HasCertificate => Error.Length == 0 && NotAfter != default;

    /// <summary>
    /// Whole days of validity left, rounded down, negative once expired. Floor rather than round:
    /// a certificate with eleven hours left has zero days, not one.
    /// </summary>
    public int DaysRemaining(DateTimeOffset now) =>
        (int)Math.Floor((NotAfter - now).TotalDays);

    public bool IsExpired(DateTimeOffset now) => HasCertificate && NotAfter <= now;

    /// <summary>True when expiry is inside <paramref name="warnDays"/>, or already past.</summary>
    public bool IsExpiring(DateTimeOffset now, int warnDays) =>
        HasCertificate && DaysRemaining(now) <= warnDays;

    /// <summary>The common name if there is one, else the whole subject. For a narrow column.</summary>
    public string ShortSubject
    {
        get
        {
            const string cn = "CN=";
            var i = Subject.IndexOf(cn, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return Subject;

            var rest = Subject[(i + cn.Length)..];
            var comma = rest.IndexOf(',');
            return comma < 0 ? rest.Trim() : rest[..comma].Trim();
        }
    }

    public static CertificateInfo Failed(string error, DateTimeOffset when) =>
        new("", "", default, default, false, error, when);
}

/// <summary>
/// Reads the TLS certificate a target presents, without going through the probe path.
/// <para>
/// Kept separate from <see cref="HttpProbe"/> on purpose. The obvious alternative — hooking
/// certificate validation on the shared <c>HttpClient</c> — has two problems: the handler is shared
/// by every HTTPS target, so the callback cannot tell which target it fired for, and it would run
/// on every request to re-read a value that changes a few times a year. A separate connection on a
/// multi-hour cadence costs far less and asks a question the probe is not trying to answer.
/// </para>
/// </summary>
public static class CertificateCheck
{
    /// <summary>
    /// Opens a TLS connection, reads the server's certificate, and closes it again. No application
    /// data is ever sent.
    /// </summary>
    /// <param name="host">
    /// The configured name. Sent as SNI and used for the name check — connecting by address alone
    /// makes a virtual host answer with the wrong certificate entirely.
    /// </param>
    public static async Task<CertificateInfo> InspectAsync(
        string host,
        IPAddress address,
        int port,
        int timeoutMs,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.Now;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);

        // Captured inside the validation callback. Every field is read there rather than holding
        // onto the certificate object, whose lifetime ends with the handshake.
        var subject = "";
        var issuer = "";
        DateTimeOffset notBefore = default;
        DateTimeOffset notAfter = default;
        var trusted = false;
        var sawCertificate = false;

        TcpClient? client = null;
        SslStream? tls = null;

        try
        {
            client = new TcpClient(address.AddressFamily);
            await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);

            // Accepts whatever it is shown, and records the verdict instead of acting on it.
            //
            // This is not a validation bypass in any meaningful sense: nothing is transmitted over
            // this connection and no trust decision rests on it. It is also the only way to answer
            // the question worth asking — an expired or self-signed certificate would abort the
            // handshake, and reporting "handshake failed" for a certificate that expired yesterday
            // tells you the one thing you already suspected and none of the detail you need.
            //
            // Every field is read here rather than by keeping the certificate, whose lifetime ends
            // with the handshake.
            bool Capture(object _, X509Certificate? cert, X509Chain? __, SslPolicyErrors errors)
            {
                if (cert is X509Certificate2 full)
                {
                    sawCertificate = true;
                    subject = full.Subject;
                    issuer = full.Issuer;

                    // NotBefore/NotAfter come back as local-kind DateTime, so this conversion
                    // carries the right offset rather than assuming UTC.
                    notBefore = new DateTimeOffset(full.NotBefore);
                    notAfter = new DateTimeOffset(full.NotAfter);
                    trusted = errors == SslPolicyErrors.None;
                }

                return true;
            }

            tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);

            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = host.Length > 0 ? host : address.ToString(),
                    RemoteCertificateValidationCallback = Capture,
                },
                timeout.Token).ConfigureAwait(false);

            return sawCertificate
                ? new CertificateInfo(subject, issuer, notBefore, notAfter, trusted, "", now)
                : CertificateInfo.Failed("no certificate presented", now);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own deadline. If the callback already ran we have everything we came for, so a
            // slow handshake after the fact is not a failure worth reporting.
            return sawCertificate
                ? new CertificateInfo(subject, issuer, notBefore, notAfter, trusted, "", now)
                : CertificateInfo.Failed("timed out", now);
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException)
        {
            return sawCertificate
                ? new CertificateInfo(subject, issuer, notBefore, notAfter, trusted, "", now)
                : CertificateInfo.Failed(Describe(ex), now);
        }
        finally
        {
            tls?.Dispose();
            client?.Dispose();
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        SocketException s when s.SocketErrorCode == SocketError.ConnectionRefused => "connection refused",
        SocketException s when s.SocketErrorCode is SocketError.HostUnreachable
            or SocketError.NetworkUnreachable => "unreachable",
        SocketException => "connect failed",
        AuthenticationException => "TLS handshake failed",
        _ => "unavailable",
    };
}
