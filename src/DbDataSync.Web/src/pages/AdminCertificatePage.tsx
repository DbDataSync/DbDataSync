import { useState } from 'react'
import { AdminTabs } from '../components/AdminTabs'
import { AppShell } from '../components/AppShell'
import { ErrorBanner } from '../components/ErrorBanner'
import { Field } from '../components/Field'
import { RestartRequiredBanner } from '../components/RestartRequiredBanner'
import { useIsAdmin } from '../components/useIsAdmin'
import {
  useAdminCertificate, useAdminCertificateCandidates, useBindCertificate,
  useCreateSelfSignedCertificate, useEnrollCertificate, useRetrieveCertificate,
} from '../api/hooks'
import type {
  BindingInfo, CertificateCandidate, CurrentCertificateInfo, KeyAccessInfo,
  KeyAccessState, PendingEnrollmentSummary, TemplateListResult,
} from '../api/types'

const KEY_ACCESS_DOT: Record<KeyAccessState, string> = { Ok: 'dot-ok', Warning: 'dot-warn', Unknown: 'dot-idle' }

/** "Days remaining" is a status colour, not a bare date someone has to subtract from today — the phase
 * 83 doc's own words. The threshold is the same one the daily expiry check raises a notification at
 * (`expiryWarningDays`), so a red badge here means exactly what a notification about it would mean. */
function expiryDot(daysRemaining: number, warningDays: number): string {
  if (daysRemaining < 0) return 'dot-bad'
  if (daysRemaining <= warningDays) return 'dot-warn'
  return 'dot-ok'
}

type DialogKind = 'self-signed' | 'enroll' | 'bind'

/**
 * The Certificates section of the Admin screen (phase 83) — visibility into the two states an operator
 * otherwise cannot discover until they cause an outage: how long the bound certificate has left, and
 * whether the service account can actually read its private key. Every action here is a second door onto
 * an operation `dbdatasync cert …` (phase 82) already performs; nothing here can do anything that CLI
 * cannot.
 */
export function AdminCertificatePage() {
  const isAdmin = useIsAdmin()
  const { data: status, isLoading, error } = useAdminCertificate()
  const [restartNeeded, setRestartNeeded] = useState(false)
  const [mutationError, setMutationError] = useState<unknown>(null)
  const [dialog, setDialog] = useState<DialogKind | null>(null)
  // Renew now = the bound certificate's own subject/SANs, carried into whichever dialog a fresh issuance
  // would use — CertCommand.Renew's own re-enroll-or-self-signed-fallback shape, expressed here as "open
  // the right dialog pre-filled" rather than a seventh endpoint the phase 83 doc's API surface does not
  // list.
  const [renewSeed, setRenewSeed] = useState<string[] | null>(null)

  const selfSigned = useCreateSelfSignedCertificate()
  const enroll = useEnrollCertificate()
  const retrieve = useRetrieveCertificate()
  const bind = useBindCertificate()

  if (!isAdmin) {
    return (
      <AppShell crumbs={[{ label: 'Admin' }]} tabs={<AdminTabs />}>
        <div className="pane">
          <div className="empty">This screen is for administrators.</div>
        </div>
      </AppShell>
    )
  }

  const closeDialog = () => { setDialog(null); setRenewSeed(null) }

  // Self-signed and enroll only add a certificate to the store — nothing dbdatasync.config.yaml names
  // changes until Bind runs, so only Bind (and a pending-enrollment Retrieve that ends in an install)
  // marks the restart banner.
  const doSelfSigned = async (dnsNames: string[], validityDays: number | null) => {
    setMutationError(null)
    try {
      await selfSigned.mutateAsync({ dnsNames, validityDays })
      closeDialog()
    } catch (err) {
      setMutationError(err)
    }
  }

  const doEnroll = async (dnsNames: string[], template: string, caConfig: string | null) => {
    setMutationError(null)
    try {
      await enroll.mutateAsync({ dnsNames, template, caConfig })
      closeDialog()
    } catch (err) {
      setMutationError(err)
    }
  }

  const doRetrieve = async (requestId: string) => {
    setMutationError(null)
    try {
      await retrieve.mutateAsync(requestId)
    } catch (err) {
      setMutationError(err)
    }
  }

  const doBind = async (thumbprint: string, allowInvalid: boolean | null) => {
    setMutationError(null)
    try {
      await bind.mutateAsync({ thumbprint, allowInvalid })
      setRestartNeeded(true)
      closeDialog()
    } catch (err) {
      setMutationError(err)
    }
  }

  const openRenew = () => {
    if (!status?.certificate) return
    setRenewSeed(status.certificate.dnsNames)
    // A CA is configured only when the template listing was actually attempted against one — the same
    // "CaConfigNotSet" reason the free-text fallback shows for the Template field.
    setDialog(status.templates && status.templates.reason !== 'CaConfigNotSet' ? 'enroll' : 'self-signed')
  }

  return (
    <AppShell crumbs={[{ label: 'Admin' }]} tabs={<AdminTabs />}>
      <div className="pane">
        <div className="page-head">
          <h1 className="page-title">Certificate</h1>
          <span className="page-note">
            The TLS certificate DbDataSync serves, how long it has left, and whether this host can actually
            read its private key. Every action here is a second door onto <code className="mono">dbdatasync
            cert …</code> — see CONFIG.md.
          </span>
        </div>

        <RestartRequiredBanner show={restartNeeded} />
        <ErrorBanner error={error ?? mutationError} />

        {isLoading && <div className="empty">Loading…</div>}

        {status && !status.available && (
          <div className="banner" data-testid="certificate-unavailable">
            <span className="mark">!</span>
            <span>{status.unavailableReason}</span>
          </div>
        )}

        {status?.available && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
            <CurrentCertificateCard certificate={status.certificate} expiryWarningDays={status.expiryWarningDays} />
            <BindingCard binding={status.binding} />
            <KeyAccessCard keyAccess={status.keyAccess} />
            {status.pendingEnrollments.length > 0 && (
              <PendingEnrollmentsCard
                pending={status.pendingEnrollments}
                busy={retrieve.isPending}
                onRetrieve={doRetrieve}
              />
            )}

            <div className="card" data-testid="certificate-actions">
              <div className="card-head"><span className="card-title">Actions</span></div>
              <div className="card-body row" style={{ gap: 8, flexWrap: 'wrap' }}>
                <button type="button" className="btn" onClick={() => setDialog('self-signed')} data-testid="action-self-signed">
                  New self-signed…
                </button>
                <button type="button" className="btn" onClick={() => setDialog('enroll')} data-testid="action-enroll">
                  Enroll from CA…
                </button>
                {status.certificate && (
                  <button type="button" className="btn" onClick={openRenew} data-testid="action-renew">
                    Renew now…
                  </button>
                )}
                <button type="button" className="btn btn-primary" onClick={() => setDialog('bind')} data-testid="action-bind">
                  Bind…
                </button>
              </div>
            </div>
          </div>
        )}
      </div>

      {dialog === 'self-signed' && (
        <SelfSignedDialog
          initialDnsNames={renewSeed}
          busy={selfSigned.isPending}
          onCancel={closeDialog}
          onSubmit={doSelfSigned}
        />
      )}

      {dialog === 'enroll' && (
        <EnrollDialog
          initialDnsNames={renewSeed}
          templates={status?.available ? status.templates : null}
          busy={enroll.isPending}
          onCancel={closeDialog}
          onSubmit={doEnroll}
        />
      )}

      {dialog === 'bind' && (
        <BindDialog
          busy={bind.isPending}
          onCancel={closeDialog}
          onSubmit={doBind}
        />
      )}
    </AppShell>
  )
}

function CurrentCertificateCard({ certificate, expiryWarningDays }: {
  certificate: CurrentCertificateInfo | null
  expiryWarningDays: number
}) {
  return (
    <div className="card" data-testid="certificate-current">
      <div className="card-head"><span className="card-title">Current certificate</span></div>
      <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        {!certificate && <span className="faint">No certificate is bound yet.</span>}
        {certificate && (
          <>
            <div className="row" style={{ gap: 10, alignItems: 'baseline' }}>
              <span className="mono" data-testid="certificate-subject">{certificate.subjectCommonName}</span>
              <span className={`badge ${certificate.selfSigned ? 'badge-primary' : 'badge-accent'}`}>
                {certificate.selfSigned ? 'SELF-SIGNED' : 'CA-ISSUED'}
              </span>
            </div>
            <div className="hint">SANs: {certificate.dnsNames.join(', ')}</div>
            <div className="hint">Issuer: {certificate.issuer}</div>
            <div className="hint mono">Thumbprint: {certificate.thumbprint}</div>
            <span className="status" data-testid="certificate-expiry">
              <span className={`dot ${expiryDot(certificate.daysRemaining, expiryWarningDays)}`} />
              {certificate.daysRemaining < 0
                ? `expired ${-certificate.daysRemaining} day(s) ago (${certificate.notAfter.slice(0, 10)})`
                : `${certificate.daysRemaining} day(s) remaining (expires ${certificate.notAfter.slice(0, 10)})`}
            </span>
          </>
        )}
      </div>
    </div>
  )
}

function BindingCard({ binding }: { binding: BindingInfo | null }) {
  return (
    <div className="card" data-testid="certificate-binding">
      <div className="card-head"><span className="card-title">Binding</span></div>
      <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        {(!binding || binding.subject === null) && (
          <span className="faint">dbdatasync.config.yaml names no certificate.</span>
        )}
        {binding && binding.subject !== null && (
          <>
            <span>
              dbdatasync.config.yaml binds subject <span className="mono">{binding.subject}</span> in{' '}
              <span className="mono">{binding.location}\{binding.store}</span>.
            </span>
            {!binding.certificateFound && (
              <div className="banner warn" role="alert" data-testid="binding-not-found">
                No certificate matching that subject is in the store right now.
              </div>
            )}
            <span className="hint">AllowInvalid: {binding.allowInvalid ? 'true' : 'false'}</span>
          </>
        )}
      </div>
    </div>
  )
}

function KeyAccessCard({ keyAccess }: { keyAccess: KeyAccessInfo | null }) {
  if (!keyAccess) return null

  return (
    <div className="card" data-testid="certificate-key-access">
      <div className="card-head"><span className="card-title">Private key access</span></div>
      <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        <span className="status" data-testid="key-access-state">
          <span className={`dot ${KEY_ACCESS_DOT[keyAccess.state]}`} />
          {keyAccess.state.toLowerCase()}
          {keyAccess.account && <span className="mono" style={{ marginLeft: 6 }}>({keyAccess.account})</span>}
        </span>
        {keyAccess.detail && <span className="hint">{keyAccess.detail}</span>}
      </div>
    </div>
  )
}

function PendingEnrollmentsCard({ pending, busy, onRetrieve }: {
  pending: PendingEnrollmentSummary[]
  busy: boolean
  onRetrieve: (requestId: string) => void
}) {
  return (
    <div className="card" data-testid="certificate-pending">
      <div className="card-head"><span className="card-title">Pending enrollment{pending.length > 1 ? 's' : ''}</span></div>
      <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        {pending.map((p) => (
          <div key={p.requestId} className="row" style={{ gap: 10, alignItems: 'center', justifyContent: 'space-between' }}>
            <span>
              <span className="mono">{p.subjectCommonName}</span> — request id{' '}
              <span className="mono">{p.requestId}</span>, submitted {p.submittedAtUtc.slice(0, 10)}
            </span>
            <button
              type="button"
              className="btn btn-sm"
              disabled={busy}
              onClick={() => onRetrieve(p.requestId)}
              data-testid={`retrieve-pending-${p.requestId}`}
            >
              Retrieve pending request
            </button>
          </div>
        ))}
      </div>
    </div>
  )
}

function SelfSignedDialog({ initialDnsNames, busy, onSubmit, onCancel }: {
  initialDnsNames: string[] | null
  busy: boolean
  onSubmit: (dnsNames: string[], validityDays: number | null) => void
  onCancel: () => void
}) {
  const [dnsNames, setDnsNames] = useState((initialDnsNames ?? []).join(', '))
  const [days, setDays] = useState('')

  const submit = () => {
    const names = dnsNames.split(',').map((n) => n.trim()).filter(Boolean)
    if (names.length === 0) return
    const parsedDays = parseInt(days, 10)
    onSubmit(names, Number.isFinite(parsedDays) && parsedDays > 0 ? parsedDays : null)
  }

  return (
    <ModalShell title="New self-signed certificate" onCancel={onCancel} testId="self-signed-dialog">
      <Field label="DNS names (comma-separated)">
        <input
          className="input"
          autoFocus
          value={dnsNames}
          onChange={(e) => setDnsNames(e.target.value)}
          placeholder="dbdatasync.corp.example.com, dbdatasync"
          data-testid="self-signed-dns-input"
        />
      </Field>
      <Field label="Validity, in days (optional — defaults to 397)">
        <input
          className="input"
          value={days}
          onChange={(e) => setDays(e.target.value)}
          placeholder="397"
          data-testid="self-signed-days-input"
        />
      </Field>
      <DialogActions busy={busy} disabled={!dnsNames.trim()} onCancel={onCancel} onConfirm={submit} confirmLabel="Issue" testId="self-signed" />
    </ModalShell>
  )
}

function EnrollDialog({ initialDnsNames, templates, busy, onSubmit, onCancel }: {
  initialDnsNames: string[] | null
  templates: TemplateListResult | null
  busy: boolean
  onSubmit: (dnsNames: string[], template: string, caConfig: string | null) => void
  onCancel: () => void
}) {
  const [dnsNames, setDnsNames] = useState((initialDnsNames ?? []).join(', '))
  const [template, setTemplate] = useState('')
  const [caConfig, setCaConfig] = useState('')

  // The picker only when the listing actually worked and found something — otherwise free text, showing
  // phase 82's own reason, per the phase 83 doc: never disabled, never blocking enrollment on discovery
  // having worked.
  const hasPickableTemplates = !!templates && templates.reason === 'Available' && templates.templates.length > 0

  const submit = () => {
    const names = dnsNames.split(',').map((n) => n.trim()).filter(Boolean)
    if (names.length === 0 || !template.trim()) return
    onSubmit(names, template.trim(), caConfig.trim() || null)
  }

  return (
    <ModalShell title="Enroll from CA" onCancel={onCancel} testId="enroll-dialog">
      <Field label="DNS names (comma-separated)">
        <input
          className="input"
          autoFocus
          value={dnsNames}
          onChange={(e) => setDnsNames(e.target.value)}
          placeholder="dbdatasync.corp.example.com, dbdatasync"
          data-testid="enroll-dns-input"
        />
      </Field>

      <Field label="Template">
        {hasPickableTemplates ? (
          <select
            className="input"
            value={template}
            onChange={(e) => setTemplate(e.target.value)}
            data-testid="enroll-template-select"
          >
            <option value="">Choose a template…</option>
            {templates!.templates.map((t) => <option key={t} value={t}>{t}</option>)}
          </select>
        ) : (
          <input
            className="input"
            value={template}
            onChange={(e) => setTemplate(e.target.value)}
            placeholder={templates?.detail ? `Type a template name — ${templates.detail}` : 'Type a template name'}
            data-testid="enroll-template-input"
          />
        )}
      </Field>

      <Field label="CA override (optional — uses DbDataSync:Certificates:CaConfig otherwise)">
        <input
          className="input mono"
          value={caConfig}
          onChange={(e) => setCaConfig(e.target.value)}
          placeholder="CASERVER\CA Name"
          data-testid="enroll-ca-input"
        />
      </Field>

      <DialogActions
        busy={busy}
        disabled={!dnsNames.trim() || !template.trim()}
        onCancel={onCancel}
        onConfirm={submit}
        confirmLabel="Enroll"
        testId="enroll"
      />
    </ModalShell>
  )
}

function BindDialog({ busy, onSubmit, onCancel }: {
  busy: boolean
  onSubmit: (thumbprint: string, allowInvalid: boolean | null) => void
  onCancel: () => void
}) {
  const { data: candidates, isLoading, error } = useAdminCertificateCandidates(true)
  const [thumbprint, setThumbprint] = useState('')
  // Three-way: undefined defers to AdminCertificateService's own default (the phase 82 doc's "report it,
  // don't touch it"), true/false is an explicit override.
  const [allowInvalid, setAllowInvalid] = useState<'default' | 'true' | 'false'>('default')

  return (
    <ModalShell title="Bind a certificate" onCancel={onCancel} testId="bind-dialog">
      <ErrorBanner error={error} />
      {isLoading && <span className="faint">Loading certificates in LocalMachine\My…</span>}
      {candidates && candidates.length === 0 && (
        <span className="faint">No server-authentication certificates found in LocalMachine\My.</span>
      )}

      {candidates && candidates.length > 0 && (
        <Field label="Certificate">
          <select
            className="input"
            value={thumbprint}
            onChange={(e) => setThumbprint(e.target.value)}
            data-testid="bind-candidate-select"
          >
            <option value="">Choose a certificate…</option>
            {candidates.map((c: CertificateCandidate) => (
              <option key={c.thumbprint} value={c.thumbprint}>
                {c.subjectCommonName} — expires {c.notAfter.slice(0, 10)} ({c.selfSigned ? 'self-signed' : 'CA-issued'})
              </option>
            ))}
          </select>
        </Field>
      )}

      <Field label="AllowInvalid">
        <select
          className="input"
          value={allowInvalid}
          onChange={(e) => setAllowInvalid(e.target.value as 'default' | 'true' | 'false')}
          data-testid="bind-allow-invalid-select"
        >
          <option value="default">Leave as configured (or default for a first bind)</option>
          <option value="true">true</option>
          <option value="false">false</option>
        </select>
      </Field>

      <DialogActions
        busy={busy}
        disabled={!thumbprint}
        onCancel={onCancel}
        onConfirm={() => onSubmit(thumbprint, allowInvalid === 'default' ? null : allowInvalid === 'true')}
        confirmLabel="Bind"
        testId="bind"
      />
    </ModalShell>
  )
}

function ModalShell({ title, testId, onCancel, children }: {
  title: string
  testId: string
  onCancel: () => void
  children: React.ReactNode
}) {
  return (
    <div className="modal-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget) onCancel() }}>
      <div className="modal" role="dialog" aria-modal="true" aria-label={title} data-testid={testId}>
        <div className="card-head"><span className="card-title">{title}</span></div>
        <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          {children}
        </div>
      </div>
    </div>
  )
}

function DialogActions({ busy, disabled, onCancel, onConfirm, confirmLabel, testId }: {
  busy: boolean
  disabled: boolean
  onCancel: () => void
  onConfirm: () => void
  confirmLabel: string
  testId: string
}) {
  return (
    <div className="row" style={{ gap: 8, justifyContent: 'flex-end' }}>
      <button type="button" className="btn" onClick={onCancel} data-testid={`${testId}-cancel`}>Cancel</button>
      <button
        type="button"
        className="btn btn-primary"
        disabled={busy || disabled}
        onClick={onConfirm}
        data-testid={`${testId}-confirm`}
      >
        {busy ? 'Working…' : confirmLabel}
      </button>
    </div>
  )
}
