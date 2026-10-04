# Project Brief: LifeDrop GeoTrack

## Project Name
LifeDrop GeoTrack

## Project Type
Field Force Management System for Pharmaceutical Distribution Companies

## Core Purpose
A comprehensive system managing the full cycle of field operations:
**Planning → Assignment → Transit → Arrival → Check-in → Visit Execution & Report/Order Logging → Check-out → Follow-up → Analysis**

## Target Users
- Field visitors (Medical Representatives, Scientific Representatives)
- Warehouse staff
- Drivers
- Supervisors/Area Managers
- Sales Managers
- Company Managers
- Super Admins

## Initial Scale
- ~50 field staff in Iraq (Erbil, Baghdad, Sulaymaniyah, Kirkuk, Duhok, Nineveh, Salah al-Din)
- Designed to scale to 150 users without major redesign

## Supported Languages
- Kurdish (RTL)
- Arabic (RTL)
- Persian (RTL)
- English (LTR)

## Product Components
1. **Mobile App (.NET MAUI)** - Operational tool for field staff
2. **Web Panel (React + TypeScript)** - Management dashboard
3. **Backend (ASP.NET Core)** - API, access control, location validation, synchronization, live events, database

## Core Principles
- Tenant isolation (mandatory TenantId on all operational entities)
- Server-side access control (not just UI-based)
- Audit logging for sensitive operations
- State integrity (Locally stored / Sent / Server accepted / Rejected)
- No exposure of sensitive data
- Preservation of existing changes