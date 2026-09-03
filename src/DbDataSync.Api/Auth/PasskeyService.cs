using System.Text;
using System.Text.Json;
using DbDataSync.State;
using Fido2NetLib;
using Fido2NetLib.Objects;

namespace DbDataSync.Api.Auth;

/// <summary>
/// WebAuthn registration and assertion, on top of fido2-net-lib.
/// <para>
/// The library rather than the specification: WebAuthn's attestation formats, COSE key parsing and
/// signature counters are not something to implement from a document for a two-role admin tool.
/// </para>
/// <para>
/// **What is stored is a public key.** That is why it lives in <c>UserCredentials.Secret</c> in the
/// state database rather than in the secret store — there is no secret to protect. The column's name
/// invites the opposite assumption, which is exactly why this says so.
/// </para>
/// </summary>
public sealed class PasskeyService(PasskeyOptions options, UserStore users)
{
    private readonly IFido2 _fido2 = new Fido2(new Fido2Configuration
    {
        ServerDomain = options.RelyingPartyId,
        ServerName = options.RelyingPartyName,
        Origins = options.Origins,
    });

    /// <summary>What a browser needs to create a passkey, and the challenge to check it against.</summary>
    public (CredentialCreateOptions Options, string State) BeginRegistration(string userId, string displayName)
    {
        var user = new Fido2User
        {
            Id = Encoding.UTF8.GetBytes(userId),
            Name = displayName,
            DisplayName = displayName,
        };

        var existing = users.CredentialsOf(userId)
            .Where(c => c.Method == CredentialMethods.Passkey)
            .Select(c => new PublicKeyCredentialDescriptor(Base64Url.Decode(c.Subject)))
            .ToList();

        var created = _fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = user,
            // So a key already enrolled here cannot be enrolled twice and appear as two credentials
            // that are one thing.
            ExcludeCredentials = existing,
            AuthenticatorSelection = AuthenticatorSelection.Default,
            AttestationPreference = AttestationConveyancePreference.None,
        });

        return (created, created.ToJson());
    }

    /// <summary>
    /// Completes a registration and returns the credential to store.
    /// <para>
    /// The challenge comes from the state this server issued, never from the client — round-tripping
    /// it through the browser would let a caller choose the challenge their own response answers.
    /// </para>
    /// </summary>
    public async Task<(string CredentialId, string PublicKey)> CompleteRegistrationAsync(
        string state, AuthenticatorAttestationRawResponse response, CancellationToken cancellationToken)
    {
        var original = CredentialCreateOptions.FromJson(state);

        var result = await _fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
        {
            AttestationResponse = response,
            OriginalOptions = original,
            IsCredentialIdUniqueToUserCallback = (_, _) => Task.FromResult(true),
        }, cancellationToken);

        return (Base64Url.Encode(result.Id), Convert.ToBase64String(result.PublicKey));
    }

    public (AssertionOptions Options, string State) BeginAssertion()
    {
        // No allow-list: the browser offers whichever key it holds for this relying party, which is
        // what makes signing in a passkey-only flow rather than "type your username, then use a key".
        var assertion = _fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Preferred,
        });

        return (assertion, assertion.ToJson());
    }

    /// <summary>The user a signed assertion belongs to, or null when it does not verify.</summary>
    public async Task<UserRecord?> CompleteAssertionAsync(
        string state, AuthenticatorAssertionRawResponse response, CancellationToken cancellationToken)
    {
        // Already base64url, which is what registration stored — the library encodes the raw id on
        // the way out and hands it back the same way. Re-encoding either side would produce a
        // credential that can be registered and never found again.
        var credentialId = response.Id;
        var user = users.FindByCredential(CredentialMethods.Passkey, credentialId);
        if (user is null)
            return null;

        var stored = users.CredentialsOf(user.Id)
            .FirstOrDefault(c => c.Method == CredentialMethods.Passkey && c.Subject == credentialId);
        if (stored?.Secret is null)
            return null;

        try
        {
            await _fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = AssertionOptions.FromJson(state),
                StoredPublicKey = Convert.FromBase64String(stored.Secret),
                StoredSignatureCounter = 0,
                IsUserHandleOwnerOfCredentialIdCallback = (_, _) => Task.FromResult(true),
            }, cancellationToken);
        }
        catch (Fido2VerificationException)
        {
            return null;
        }

        return user.Enabled ? user : null;
    }

    /// <summary>Base64url, which is what WebAuthn speaks and what a credential id is stored as.</summary>
    private static class Base64Url
    {
        public static string Encode(byte[] value) =>
            Convert.ToBase64String(value).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        public static byte[] Decode(string value)
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
        }
    }

    /// <summary>Serialises whatever the ceremony has to remember between its two halves.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value);
}
