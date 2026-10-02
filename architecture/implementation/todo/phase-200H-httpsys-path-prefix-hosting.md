# Phase 200H — Serving the console under a path prefix

**Status**: Not started, and may not be wanted. Follow-up to phase 196H.

## Why

HTTP.sys can share a port by path as well as by host name (`http://host:80/dbdatasync/`), and so can a
reverse proxy in front of Kestrel. Today neither works: HTTP.sys sets `PathBase` from the prefix, but the
SPA, its assets and its `/api` and `/hubs` calls assume the root of the origin. Phase 196H documents sharing
by host name only and warns at startup when a path prefix is bound.

## Scope, if wanted

- The Vite build emits relative asset URLs or the server rewrites a `<base href>` into `index.html` from
  `PathBase`; the SPA's API client and SignalR connection derive their root from it.
- `MapFallback`'s `/api` and `/hubs` exclusion, `UseStaticFiles` and `UseDefaultFiles` honour `PathBase`.
- Cookies get `Path` from the base, not `/`. Passkey origins and relying-party id are unchanged (they are
  host-scoped) but the docs need to say so. Redirects and `UseHttpsRedirection` keep the prefix.
- Benefits the Kestrel-behind-a-proxy case equally, so write it server-agnostically.

## Decision first

Is path-based sharing a real requirement, or is host-name sharing enough? If the latter, close this doc
unbuilt and drop the startup warning's mention of it.
