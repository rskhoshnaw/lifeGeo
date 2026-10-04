# Project Overview - LifeDrop

## Project Name
LifeDrop GeoTrack

## Objective
Field force management system for pharmaceutical distribution companies.
Full cycle: Planning → Assignment → Transit → Arrival → Check-in → Visit execution & report/order logging → Check-out → Follow-up → Analysis

## Product Components
1. Mobile App (.NET MAUI) – Operational tool for field visitors, medical representatives, warehouse staff, and drivers
2. Web Panel (React + TypeScript) – Management of users, customers, visits, tracking, sales, finance, reporting, and auditing
3. Backend (ASP.NET Core) – API, access control, location validation, synchronization, live events, and database

## Initial Scale
Approximately 50 field staff in Iraq (Erbil, Baghdad, Sulaymaniyah, Kirkuk, Duhok, Nineveh, Salah al-Din)
Design must be scalable to 150 users without requiring a major redesign.

## Supported Languages
User Interface: Kurdish, Arabic, Persian (RTL), and English (LTR)

## Core Principles
- Tenant isolation
- Server-side access control (not just UI-based)
- Audit logging for sensitive operations
- State integrity (Locally stored / Sent / Server accepted / Rejected)
- No exposure of sensitive data
- Preservation of existing changes