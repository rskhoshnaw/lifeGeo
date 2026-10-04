# Product Context

## Purpose
LifeDrop manages pharmaceutical field work from assignment through travel, arrival, visit, reporting or order capture, checkout, follow-up, and analysis. Initial deployment targets about 50 field staff in Iraq; the design target is 150 without a major redesign.

## Users and experience
- Field representatives use a mobile app, with Android first and iOS also planned. Workday data and actions must survive loss of connectivity.
- Managers use a React + TypeScript web panel for users, visits, tracking, sales, and audit.
- The UI is planned for Kurdish, Arabic, Persian, and English, including RTL layouts.

## Product rules
- Isolate every tenant and enforce permissions on the server.
- The server owns visit state transitions and final GPS/geofence decisions; the default geofence radius in the PRD summary is 20 m and configurable.
- Check-out, like check-in, must be inside the customer's geofence with fresh valid GPS; recording any location is not enough (decision D2).
- Each team operates in one region defined by the sales manager. A representative may register an out-of-region customer; it stays pending until an authorized manager approves it, then belongs to that representative (decision D11).
- Offline actions need durable IDs and explicit local/sent/accepted/rejected states; sync must be idempotent.
- Sensitive actions need audit records. Never expose passwords, device secrets, or tokens in logs or documentation.

## Scope boundary
The above describes target behavior, not what is already implemented. The authoritative product PRD is `docs/سند نیازمندی محصول LifeDrop.docx` v2.0. See `phaseStatus.md` (and `archive/` for evidence). `docs/04-PRD-SUMMARY.md` is condensed context, and `docs/PRD.md` is the English working edition with the decision log; neither overrides the original PRD.
