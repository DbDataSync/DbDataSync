using DataSync.Api.Auth;
using DataSync.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DataSync.Api.Controllers;

/// <summary>
/// The Certificates section of the Admin screen (phase 83) — a second door onto every operation phase
/// 82's <c>datasync cert …</c> already exposes (<see cref="AdminCertificateService"/> does the actual
/// work; this is deliberately thin, matching <see cref="AdminConfigController"/>'s own shape).
/// <para>
/// <c>[Authorize(Policies.Admin)]</c> stated explicitly on every action, including the read — the same
/// reasoning <see cref="AdminConfigController"/> already states out loud: this screen reveals the
/// deployment's hostnames, CA and certificate inventory, a stricter bar than the <c>Viewer</c> policy
/// most read endpoints use.
/// </para>
/// </summary>
[ApiController]
[Route("api/admin/certificate")]
public sealed class AdminCertificateController(AdminCertificateService service, CurrentUser currentUser) : ControllerBase
{
    [Authorize(Policies.Admin)]
    [HttpGet]
    public ActionResult<AdminCertificateStatus> Get() => Ok(service.GetStatus());

    /// <summary>Server-auth certificates already in <c>LocalMachine\My</c> — what the Bind action picks
    /// from. Empty (not an error) on a non-Windows host or when the store holds none.</summary>
    [Authorize(Policies.Admin)]
    [HttpGet("candidates")]
    public ActionResult<IReadOnlyList<CertificateCandidate>> Candidates() => Ok(service.GetCandidates());

    [Authorize(Policies.Admin)]
    [HttpPost("self-signed")]
    public ActionResult<CertificateActionResult> CreateSelfSigned([FromBody] SelfSignedRequest body)
    {
        var result = service.CreateSelfSigned(body.DnsNames, body.ValidityDays);
        return result.Succeeded ? Ok(result) : BadRequest(new { error = result.Message });
    }

    /// <summary>May come back with <c>Succeeded</c> true and a <c>RequestId</c> set but no certificate
    /// installed yet — a template requiring approval. That is not a failure; see
    /// <see cref="CertificateActionResult.Succeeded"/>'s own doc comment.</summary>
    [Authorize(Policies.Admin)]
    [HttpPost("enroll")]
    public ActionResult<CertificateActionResult> Enroll([FromBody] EnrollRequest body)
    {
        var result = service.Enroll(body.DnsNames, body.Template, body.CaConfig);
        return result.Succeeded ? Ok(result) : BadRequest(new { error = result.Message });
    }

    [Authorize(Policies.Admin)]
    [HttpPost("retrieve")]
    public ActionResult<CertificateActionResult> Retrieve([FromBody] RetrieveRequest body)
    {
        var result = service.Retrieve(body.RequestId);
        return result.Succeeded ? Ok(result) : BadRequest(new { error = result.Message });
    }

    /// <summary>Does not take effect until the DataSync service restarts — <see cref="AdminCertificateService.Bind"/>
    /// never attempts to, matching <c>CertificateBinding.Bind</c>'s own doc comment.</summary>
    [Authorize(Policies.Admin)]
    [HttpPost("bind")]
    public ActionResult<CertificateActionResult> Bind([FromBody] BindRequest body)
    {
        var result = service.Bind(body.Thumbprint, body.AllowInvalid, currentUser.Author);
        return result.Succeeded ? Ok(result) : BadRequest(new { error = result.Message });
    }
}

public sealed record SelfSignedRequest(IReadOnlyList<string> DnsNames, int? ValidityDays);

/// <param name="CaConfig">Overrides <c>DataSync:Certificates:CaConfig</c> for this one enrollment when
/// set; falls back to the configured value otherwise.</param>
public sealed record EnrollRequest(IReadOnlyList<string> DnsNames, string Template, string? CaConfig);

public sealed record RetrieveRequest(string RequestId);

/// <param name="AllowInvalid">Null defers to <see cref="AdminCertificateService"/>'s own default
/// resolution (the phase 82 doc's "report it, don't touch it") — explicit true/false always wins.</param>
public sealed record BindRequest(string Thumbprint, bool? AllowInvalid);
