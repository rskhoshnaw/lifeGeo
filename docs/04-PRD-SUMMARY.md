# PRD Summary - LifeDrop (Condensed Version)

## Key Roles
Super Admin, Company Manager, Sales Manager, Supervisor/Area Manager, Account Manager,
Sales Representative, Scientific Representative, Warehouse Manager, Driver

## Visit Lifecycle (State Machine)
Planned → En_Route → Arrived → In_Visit → Completed
Exception outcomes: Failed and Cancelled (requires reason and authorized role)
Product decision: both outcomes are allowed from Planned, En_Route, Arrived, and In_Visit. An exception can arise before departure or after work begins. Completed, Failed, and Cancelled remain terminal; server authorization and auditing still apply.

## Key Check-in/Check-out Rules
- Performed only by the assigned representative
- Validation of visit ownership, status, device binding, and GPS
- Default geofence radius: 20 meters (configurable)
- Check-out also requires fresh, valid GPS inside the customer's geofence radius; recording any location is not enough (decision D2, anti-fraud)
- Final decision made server-side only

## Regions and Teams (decision D11)
- Each team operates in one region; the sales manager defines regions and assigns them to teams.
- A representative may register a customer outside their region. It is saved as pending out-of-region and, once approved by an authorized manager (sales manager or supervisor), is assigned to that representative. Approval or rejection is audited.

## Offline Capabilities
- Storage of relevant workday data on the device
- Persistent queue with unique IDs and accurate status tracking
- Idempotent data transmission upon reconnection

## Security Principles
- Complete tenant isolation
- API-level access control
- Device binding
- No logging of passwords or tokens
- Non-deletable audit logs (via UI)

## Out of MVP Scope
AI-based route optimization, full ERP/SMS/WhatsApp integration, comprehensive report builder, advanced analytics, commercial SaaS offering

Remaining work by phase and step: `docs/IMPLEMENTATION-PLAN.md`.
