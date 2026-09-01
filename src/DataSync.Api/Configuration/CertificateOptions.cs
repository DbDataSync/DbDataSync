namespace DataSync.Api.Configuration;

/// <summary>
/// The <c>DataSync:Certificates:*</c> settings phase 82 owns. Separate from <see cref="ApiOptions"/>
/// the same way <see cref="AuthOptions"/>/<see cref="PasskeyOptions"/> already are — one options class
/// per feature area that has more than a couple of settings, rather than one growing bag everything
/// reads from.
/// <para>
/// <c>CaConfig</c> and <c>Template</c> (the AD CS enrollment settings the phase 82 doc names) are
/// deliberately **not** here — they are read by <c>datasync cert enroll</c>/<c>templates</c>/<c>renew</c>
/// directly off <c>datasync.config.yaml</c> (<see cref="DataSync.Core.Config.DataSyncConfigFile.Read"/>),
/// a CLI-process concern the same way <c>DataSync:Url</c> is (see <c>ServeCommand</c>) — the running API
/// process never needs to know which CA a certificate came from, only which one is bound.
/// </para>
/// </summary>
public sealed class CertificateOptions
{
    /// <summary>How many days before <c>NotAfter</c> the daily expiry check starts raising
    /// <c>CertificateExpiring</c> — see <c>DataSync.Api.Services.CertificateExpiryService</c>.</summary>
    public int ExpiryWarningDays { get; init; } = 30;

    public static CertificateOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("DataSync:Certificates");

        return new CertificateOptions
        {
            ExpiryWarningDays = int.TryParse(section["ExpiryWarningDays"], out var days) && days > 0 ? days : 30,
        };
    }
}
