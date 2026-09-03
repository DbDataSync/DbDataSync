using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DbDataSync.Certificates;

public enum EnrollmentOutcome
{
    Issued,
    /// <summary>A template requiring certificate-manager approval — a real, expected state, not a
    /// failure. See <see cref="AdcsEnrollment"/>'s own doc comment.</summary>
    Pending,
    Denied,
    Failed,
}

/// <param name="RequestId">Set for <see cref="EnrollmentOutcome.Issued"/> and
/// <see cref="EnrollmentOutcome.Pending"/> — null for <see cref="EnrollmentOutcome.Denied"/> and
/// <see cref="EnrollmentOutcome.Failed"/>, where there is nothing to retrieve later.</param>
/// <param name="Certificate">Set only for <see cref="EnrollmentOutcome.Issued"/>.</param>
public sealed record EnrollmentResult(
    EnrollmentOutcome Outcome, string? RequestId, X509Certificate2? Certificate, string Message);

/// <summary>
/// Submits a CSR to an enterprise CA over the <c>CertificateAuthority.Request</c> COM interface
/// (<c>ICertRequest</c>/<c>ICertRequest2</c>, from <c>certcli.dll</c>, installed with the Windows AD CS
/// client tools) — see the phase 82 doc's "AD CS enrollment." Resolved by ProgID and called dynamically
/// rather than shelling <c>certreq.exe</c>: <c>certreq</c> reports failure by printing to stdout and
/// needs temp files for the request and the response, while the COM call returns a disposition code
/// that actually distinguishes issued, denied, and — the case that matters most — pending.
/// <para>
/// **Pending is a real state, not an error.** A template requiring certificate-manager approval returns
/// <c>CR_DISP_UNDER_SUBMISSION</c> with a request id; that id is what <c>dbdatasync cert retrieve</c>
/// later feeds to <see cref="Retrieve"/>. Treating pending as a failure would make this feature unusable
/// in exactly the environments strict enough to require approval — the ones most likely to want an
/// enterprise CA in the first place.
/// </para>
/// <para>
/// **Not tested.** This needs a real enterprise CA to exercise — this project's own sandbox and this
/// repo's CI have neither — so this class is exercised manually against a real CA, the same honesty
/// phase 51 stated plainly for Windows service registration ("Windows service registration is
/// untested") rather than a test that asserts a COM call would have been made. The disposition-code
/// constants below (<c>CR_DISP_*</c>, <c>CR_IN_BASE64HEADER</c>) are the documented, stable values from
/// <c>certcli.h</c> / <c>certenroll</c>, not independently verified against a live server here.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class AdcsEnrollment
{
    private const string ProgId = "CertificateAuthority.Request";

    // ICertRequest disposition/encoding constants, from certcli.h.
    private const int CrInBase64Header = 0;
    private const int CrDispIssued = 3;
    private const int CrDispIssuedOutOfBand = 4;
    private const int CrDispUnderSubmission = 5;
    private const int CrDispDenied = 2;

    /// <summary>Submits a PEM-encoded CSR (see <see cref="CertificateBuilder.CreateSigningRequest"/>)
    /// against <paramref name="template"/> on the CA named by <paramref name="caConfig"/> (the
    /// <c>CASERVER\CA Name</c> shape <c>DbDataSync:Certificates:CaConfig</c> holds).</summary>
    public static EnrollmentResult Submit(string caConfig, string template, string csrPem)
    {
        using var certRequestHandle = OpenCertRequest();
        dynamic certRequest = certRequestHandle.Instance;

        var attributes = $"CertificateTemplate:{template}";
        var disposition = (int)certRequest.Submit(CrInBase64Header, csrPem, attributes, caConfig);

        return ToResult(certRequest, disposition);
    }

    /// <summary>Collects a request that came back <see cref="EnrollmentOutcome.Pending"/> earlier —
    /// <c>dbdatasync cert retrieve --request-id</c>.</summary>
    public static EnrollmentResult Retrieve(string caConfig, string requestId)
    {
        if (!int.TryParse(requestId, out var id))
            return new EnrollmentResult(EnrollmentOutcome.Failed, requestId, null, $"'{requestId}' is not a valid request id (expected an integer).");

        using var certRequestHandle = OpenCertRequest();
        dynamic certRequest = certRequestHandle.Instance;

        var disposition = (int)certRequest.RetrievePending(id, caConfig);
        return ToResult(certRequest, disposition);
    }

    private static EnrollmentResult ToResult(dynamic certRequest, int disposition)
    {
        switch (disposition)
        {
            case CrDispIssued or CrDispIssuedOutOfBand:
                string base64Certificate = certRequest.GetCertificate(CrInBase64Header);
                var certificate = X509CertificateLoader.LoadCertificate(Encoding.ASCII.GetBytes(base64Certificate));
                var issuedRequestId = certRequest.GetRequestId().ToString();
                return new EnrollmentResult(EnrollmentOutcome.Issued, issuedRequestId, certificate, "Certificate issued.");

            case CrDispUnderSubmission:
                var pendingRequestId = certRequest.GetRequestId().ToString();
                return new EnrollmentResult(
                    EnrollmentOutcome.Pending, pendingRequestId, null,
                    $"Enrollment is pending approval (request id {pendingRequestId}). Collect it later with " +
                    $"'dbdatasync cert retrieve --request-id {pendingRequestId}'.");

            case CrDispDenied:
                return new EnrollmentResult(
                    EnrollmentOutcome.Denied, null, null, $"Enrollment was denied: {DispositionMessage(certRequest)}");

            default:
                return new EnrollmentResult(
                    EnrollmentOutcome.Failed, null, null,
                    $"Enrollment failed (disposition {disposition}): {DispositionMessage(certRequest)}");
        }
    }

    private static string DispositionMessage(dynamic certRequest)
    {
        try
        {
            return (string)certRequest.GetDispositionMessage();
        }
        catch (COMException)
        {
            return "no further detail was available from the CA.";
        }
    }

    /// <summary>
    /// A tiny <see cref="IDisposable"/> wrapper around the COM object so <see cref="Submit"/> and
    /// <see cref="Retrieve"/> can each use a <c>using</c> statement rather than repeating a
    /// try/finally + <see cref="Marshal.FinalReleaseComObject"/> pair.
    /// </summary>
    private readonly struct CertRequestHandle(object instance) : IDisposable
    {
        public dynamic Instance { get; } = instance;

        public void Dispose() => Marshal.FinalReleaseComObject(Instance);
    }

    private static CertRequestHandle OpenCertRequest()
    {
        var type = Type.GetTypeFromProgID(ProgId)
            ?? throw new PlatformNotSupportedException(
                $"COM class '{ProgId}' is not registered on this machine. AD CS enrollment needs the " +
                "Certificate Services client tools present locally (installed with the AD CS role, or " +
                "the 'AD CS Tools' Windows feature).");

        var instance = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"Could not create an instance of COM class '{ProgId}'.");

        return new CertRequestHandle(instance);
    }
}
