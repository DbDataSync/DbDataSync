/**
 * The browser half of WebAuthn: base64url between the wire and the `ArrayBuffer`s the platform API
 * insists on.
 *
 * Hand-written rather than a library, because it is forty lines of encoding and a dependency here
 * would be a dependency in the bundle for the two screens that use it.
 */
export function isSupported() {
  return typeof window !== 'undefined' && !!window.PublicKeyCredential
}

function toBuffer(base64Url: string): ArrayBuffer {
  const padded = base64Url.replace(/-/g, '+').replace(/_/g, '/')
  const binary = atob(padded.padEnd(Math.ceil(padded.length / 4) * 4, '='))
  const bytes = new Uint8Array(binary.length)
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i)
  return bytes.buffer
}

function toBase64Url(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer)
  let binary = ''
  for (const byte of bytes) binary += String.fromCharCode(byte)
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

/** The server speaks base64url; `navigator.credentials` speaks ArrayBuffer. */
export async function createCredential(options: any) {
  const credential = (await navigator.credentials.create({
    publicKey: {
      ...options,
      challenge: toBuffer(options.challenge),
      user: { ...options.user, id: toBuffer(options.user.id) },
      excludeCredentials: (options.excludeCredentials ?? []).map((c: any) => ({
        ...c,
        id: toBuffer(c.id),
      })),
    },
  })) as PublicKeyCredential

  const response = credential.response as AuthenticatorAttestationResponse
  return {
    id: credential.id,
    rawId: toBase64Url(credential.rawId),
    type: credential.type,
    extensions: credential.getClientExtensionResults(),
    response: {
      attestationObject: toBase64Url(response.attestationObject),
      clientDataJson: toBase64Url(response.clientDataJSON),
    },
  }
}

export async function getCredential(options: any) {
  const credential = (await navigator.credentials.get({
    publicKey: {
      ...options,
      challenge: toBuffer(options.challenge),
      allowCredentials: (options.allowCredentials ?? []).map((c: any) => ({
        ...c,
        id: toBuffer(c.id),
      })),
    },
  })) as PublicKeyCredential

  const response = credential.response as AuthenticatorAssertionResponse
  return {
    id: credential.id,
    rawId: toBase64Url(credential.rawId),
    type: credential.type,
    extensions: credential.getClientExtensionResults(),
    response: {
      authenticatorData: toBase64Url(response.authenticatorData),
      clientDataJson: toBase64Url(response.clientDataJSON),
      signature: toBase64Url(response.signature),
      userHandle: response.userHandle ? toBase64Url(response.userHandle) : null,
    },
  }
}
