# Folder Structure

Updated: 2026-10-04. `[present]` exists in the repository; `[planned]` is a step in `docs/IMPLEMENTATION-PLAN.md`.

lifeGeo/
├── src/
│   ├── LifeDrop.Domain/                 [present]
│   ├── LifeDrop.Application/            [present]
│   ├── LifeDrop.Infrastructure/         [present]
│   ├── LifeDrop.API/                    [present]
│   ├── LifeDrop.Mobile/                 [present] .NET MAUI app (Android first)
│   ├── LifeDrop.Mobile.Auth/            [present] shared mobile auth client
│   ├── LifeDrop.Mobile.Auth.Android/    [present] Android secure storage
│   └── LifeDrop.Web/                    [planned, step 1.16] React + TypeScript web panel
├── tests/                               [present] Domain, Application, Database, Mobile.Auth tests
├── tools/LifeDrop.DevBootstrap/         [present]
├── db/migrations/                       [present] plain SQL migrations
├── docs/
├── memory-bank/
├── LifeDrop.slnx / LifeDrop.CI.slnf     [present]
└── README.md

Root-level `package.json`, `server.ts`, `vite.config.ts`, `index.html`, `src/*.jsx` and `src/components/` are inherited template files. They are not the LifeDrop panel; remove them only with explicit approval.
