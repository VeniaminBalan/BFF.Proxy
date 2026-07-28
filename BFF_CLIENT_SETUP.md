# Wiring a React SPA to the BFF Proxy

This is the step-by-step for connecting a React frontend to this BFF proxy (see
[README.md](README.md) for how the BFF itself works). The paths below (`src/config/...`,
`src/hooks/useAuth.ts`, `components/AuthGuard.tsx`) are suggested locations in your SPA - adjust
to your project's structure.

The end state: the SPA never touches Keycloak, never sees a token, and only ever talks to the
BFF's own origin. Two things make that work - a plain `<a>`/`window.location` redirect for login
(not a fetch call), and `withCredentials: true` on every axios call so the session cookie rides
along.

## 1. Environment variables

Two env vars, both pointing at the BFF (not at Keycloak, not at the resource API directly):

```bash
# .env / .env.production / .env.compose - whatever your build pipeline uses
VITE_BFF_URL=http://localhost:6080        # the BFF's own origin
VITE_API_URL=http://localhost:6080/api    # the BFF's proxy prefix for the resource API
```

`VITE_API_URL` is the BFF, not the downstream API - the BFF's YARP reverse proxy maps whatever it
receives on `/api/**` to the real resource server and attaches the bearer token itself. The SPA
never needs to know the resource API's actual address.

## 2. Two axios instances

You need two, because they serve different purposes and get different interceptors:

### `src/config/bffApi.ts` - talks to the BFF's own `/bff/*` endpoints

```ts
import axios from 'axios';
import { attachCsrfHeader, setCsrfToken } from './csrfToken';

const BFF_URL = import.meta.env.VITE_BFF_URL;

// withCredentials is required so the HttpOnly session cookie is sent cross-port in dev.
export const bffApi = axios.create({
  baseURL: BFF_URL,
  withCredentials: true,
});

bffApi.interceptors.request.use(attachCsrfHeader);

// /bff/me is the only endpoint that hands back the current CSRF token; cache it here
// so subsequent mutating requests (on this client and the proxied `api` client) can echo it.
bffApi.interceptors.response.use((response) => {
  if (response.config.url?.includes('/bff/me')) {
    setCsrfToken(response.data?.csrfToken);
  }
  return response;
});

export default bffApi;
```

### `src/config/axios.ts` - talks to the proxied resource API (`/api/**`)

```ts
import axios from 'axios';
import { attachCsrfHeader } from './csrfToken';

const API_URL = import.meta.env.VITE_API_URL;
const BFF_URL = import.meta.env.VITE_BFF_URL;

const api = axios.create({
  baseURL: API_URL,
  withCredentials: true, // send the BFF session cookie with every request
  headers: { 'Content-Type': 'application/json' },
});

api.interceptors.request.use(attachCsrfHeader);

// The BFF attaches the Bearer token from the server-side session; the browser
// never sees an access token. A 401 here means the session is gone or expired.
api.interceptors.response.use(
  (response) => response,
  (error) => {
    if (error.response?.status === 401) {
      const redirectUri = encodeURIComponent(window.location.href);
      window.location.href = `${BFF_URL}/bff/login?redirectUri=${redirectUri}`;
    }
    return Promise.reject(error);
  }
);

export default api;
```

Both set `withCredentials: true` - without it the browser won't send the session cookie
cross-origin (BFF and SPA are typically on different ports/hosts), and every call will look
unauthenticated.

## 3. CSRF header plumbing

The BFF requires an `X-CSRF-Token` header on `POST`/`PUT`/`PATCH`/`DELETE` requests, matched
against a cookie it sets (see [README.md](README.md)'s CSRF section for why). Because the BFF and
SPA are cross-origin, the SPA can't read that cookie directly - the token is handed back in the
`/bff/me` response body instead and needs to be cached and re-attached on every mutating request.

`src/config/csrfToken.ts`:

```ts
import type { InternalAxiosRequestConfig } from 'axios';

let csrfToken: string | null = null;

export const setCsrfToken = (token: string | null | undefined) => {
  csrfToken = token ?? null;
};

export const getCsrfToken = () => csrfToken;

export const CSRF_HEADER_NAME = 'X-CSRF-Token';

const UNSAFE_METHODS = new Set(['post', 'put', 'patch', 'delete']);

export const attachCsrfHeader = (config: InternalAxiosRequestConfig): InternalAxiosRequestConfig => {
  const method = config.method?.toLowerCase();
  if (method && UNSAFE_METHODS.has(method) && csrfToken) {
    config.headers.set(CSRF_HEADER_NAME, csrfToken);
  }
  return config;
};
```

Both axios instances register `attachCsrfHeader` as a request interceptor (step 2, above). The
token only gets populated once `/bff/me` has been called at least once - in practice this means
the auth hook (step 4) must run before any mutating call, which it does because it's called at
the app root.

## 4. The auth hook (`src/hooks/useAuth.ts`)

This is the only place that talks to `/bff/me`, `/bff/login`, `/bff/logout`, `/bff/orgs`, and
`/bff/orgs/switch`. Everything else in the app should go through it rather than calling `bffApi`
directly for auth concerns.

Key pieces:

- **Auth state** comes from a React Query-cached `GET /bff/me`:

  ```ts
  const { data, isLoading, isFetched } = useQuery({
    queryKey: ['auth', 'me'],
    queryFn: async () => {
      const response = await bffApi.get('/bff/me', {
        validateStatus: (status) => status === 200 || status === 401,
      });
      return response.status === 200 ? response.data : { isAuthenticated: false, claims: [] };
    },
    staleTime: 60 * 1000,
    retry: false,
    refetchOnWindowFocus: false,
  });
  ```

  `validateStatus` is important - a `401` here is an expected "not logged in" answer, not an
  error to retry or throw on.

- **`login()`** is a full-page redirect, *not* an axios call, because the BFF needs to send the
  browser through Keycloak's actual login page:

  ```ts
  const login = () => {
    const redirectUri = encodeURIComponent(window.location.href);
    window.location.href = `${BFF_URL}/bff/login?redirectUri=${redirectUri}&prompt=select_account`;
  };
  ```

- **`logout()`** is the same pattern, hitting `/bff/logout`.

- **`switchOrganization(organizationId)`** calls `PUT /bff/orgs/switch` then invalidates the
  `['auth', 'me']` query, since the BFF re-issues the session cookie with a new `org` claim and
  the cached claims need to catch up:

  ```ts
  const switchOrganization = async (organizationId: string) => {
    await bffApi.put('/bff/orgs/switch', { organizationId });
    await queryClient.invalidateQueries({ queryKey: ['auth', 'me'] });
  };
  ```

- **User/claims parsing** - `/bff/me` returns raw ASP.NET Core claims (`{ type, value }` pairs),
  not a ready-made user object, so the hook picks out the fields it needs (`sub`,
  `preferred_username`, `email`, `given_name`, `family_name`, `realm_access`, and the Keycloak
  Phase Two `org` claim, which is a JSON string and needs `JSON.parse`).

## 5. Wiring it into the app root

Two things need to happen at the top of the component tree:

1. **A `QueryClientProvider`** (`main.tsx`) - `useAuth` depends on React Query.
2. **A silent SSO check on first load** (`App.tsx`) - if the browser already has an active
   Keycloak session (from another app in the same realm), this logs the user in without a visible
   redirect flash:

   ```ts
   useEffect(() => {
     if (!isInitialized || isAuthenticated) return;
     if (sessionStorage.getItem('bff-silent-login-attempted')) return;

     sessionStorage.setItem('bff-silent-login-attempted', 'true');
     const redirectUri = encodeURIComponent(window.location.href);
     window.location.href = `${BFF_URL}/bff/login?prompt=none&redirectUri=${redirectUri}`;
   }, [isInitialized, isAuthenticated]);
   ```

   The `sessionStorage` guard is required: a silent check that fails (`prompt=none` with no
   active Keycloak session) redirects straight back to the SPA, and without the guard that would
   loop forever. The BFF's `/bff/login` handler cooperates with this by treating a failed silent
   attempt as a normal redirect back to the SPA instead of an error page (see
   [README.md](README.md)).

3. **An auth gate** (`components/AuthGuard.tsx`) wraps the routed content: shows a spinner while
   `isLoading`, an explicit "Log In" button calling `login()` when not authenticated, and renders
   `children` once `isAuthenticated`. Optionally takes a `requiredRole` prop and checks
   `hasRole(requiredRole)`.

## 6. Organization switching UI (optional)

If the app supports multi-org users, `components/OrganizationSwitcher.tsx` shows the pattern:
call `getOrganizations()` to populate a dropdown, call `switchOrganization(id)` on selection, and
let the `useAuth` hook's cache invalidation handle refreshing `user.organization` afterward - no
manual state sync needed beyond the dropdown's own selection state.

## Checklist

- [ ] `VITE_BFF_URL` and `VITE_API_URL` point at the BFF, not at Keycloak or the resource API.
- [ ] Both axios instances have `withCredentials: true`.
- [ ] `attachCsrfHeader` is registered as a request interceptor on both axios instances.
- [ ] `bffApi`'s response interceptor captures `csrfToken` from `/bff/me`.
- [ ] `login()`/`logout()` are `window.location` redirects, never `fetch`/`axios` calls.
- [ ] `/bff/me` calls use `validateStatus` to treat `401` as a normal "not logged in" response.
- [ ] The proxied API client (`axios.ts`) redirects to `/bff/login` on a `401`, so an expired
      session recovers instead of silently failing every request.
- [ ] A `sessionStorage` guard prevents the silent-SSO check from looping.
