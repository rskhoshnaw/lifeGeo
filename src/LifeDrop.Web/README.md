# LifeDrop web panel

React + TypeScript (strict) + Tailwind CSS v4 on Vite (plan step 1.16, decisions D10, D12).

```bash
npm ci
cp .env.example .env.local   # set VITE_API_BASE_URL
npm run dev                  # http://localhost:5173
npm run lint && npm run typecheck && npm test && npm run build
```

The API must allow the panel's origin: set `Cors:AllowedOrigins` (for example `Cors__AllowedOrigins__0=https://panel.example.com`).

## Sign-in
Managers sign in with company code, email and password. Field roles bound to a phone get "sign in only from its
approved phone". The access token stays in memory; the refresh token is kept in `sessionStorage` (this tab only)
and rotates on each refresh.

## Cloudflare Pages (decision D13) — owner setup
1. Cloudflare dashboard → Workers & Pages → Create → Pages → connect the private GitHub repo.
2. Build settings: root directory `src/LifeDrop.Web`, build command `npm ci && npm run build`, output `dist`.
3. Environment variable `VITE_API_BASE_URL` = the API address (production and preview).
4. Zero Trust → Access → add an application for the Pages domain (and `*.pages.dev` previews) so only staff can open it.
5. Add the Pages domain to the API's `Cors:AllowedOrigins`.
