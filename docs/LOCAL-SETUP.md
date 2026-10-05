# راه‌اندازی LifeDrop روی ویندوز (لوکال)

هدف: اجرای کامل پروژه روی سیستم خودتان — پایگاه‌داده، API، پنل وب و اپ اندروید روی گوشی واقعی — و اجرای همهٔ تست‌های CI پیش از push.

## ۱. نصب‌ها (یک بار)

| ابزار | نکته |
| --- | --- |
| **Git for Windows** | https://git-scm.com — Claude Code روی ویندوز هم به آن نیاز دارد |
| **Docker Desktop** | با WSL 2؛ برای پایگاه‌داده (PostgreSQL 16 + PostGIS) |
| **Visual Studio 2022 (17.14+) یا 2026 Community** | هنگام نصب workload **.NET Multi-platform App UI (MAUI)** را انتخاب کنید؛ .NET 10 SDK، Android SDK، JDK و شبیه‌ساز را با هم نصب می‌کند |
| **Node.js 22 LTS** | برای پنل وب |
| **Claude Code** | نسخهٔ دسکتاپ (تب Code با پوشهٔ محلی) یا CLI |

بدون Visual Studio هم می‌شود: .NET 10 SDK را نصب کنید، سپس `dotnet workload install maui-android` و برای Android SDK/JDK:
`dotnet build src\LifeDrop.Mobile\LifeDrop.Mobile.csproj -t:InstallAndroidDependencies -f net10.0-android -p:AcceptAndroidSDKLicenses=True`

PowerShell: اگر اجرای اسکریپت‌ها بسته بود، یک بار:
`Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`

## ۲. دریافت کد

```powershell
git clone https://github.com/rskhoshnaw/lifeGeo.git
cd lifeGeo
```

## ۳. پایگاه‌داده (Docker)

```powershell
docker compose up -d                  # بار اول image را دانلود می‌کند
.\scripts\local\Reset-LocalDb.ps1     # lifedrop_dev: همهٔ مایگریشن‌ها + grants + دادهٔ نمونه
```

- دو پایگاه‌داده ساخته می‌شود: `lifedrop_test_ci` (تست‌ها، مثل CI) و `lifedrop_dev` (اجرای برنامه).
- `Reset-LocalDb.ps1` را بعد از هر مایگریشن جدید دوباره اجرا کنید (دادهٔ لوکال پاک و از نو ساخته می‌شود).
- پایگاه‌داده روی پورت **54321** و فقط روی همین کامپیوتر باز است. پورت‌های 5432 (پروژهٔ KBSAIS)، 5433، 5434، 55432، 55433 (پورت‌های قدیمی یا رزرو شده توسط ویندوز) استفاده نمی‌شوند. اگر 54321 هم اشغال شد: `$env:LIFEDROP_DB_PORT='54320'` و بعد `docker compose up -d` (اسکریپت‌ها همان مقدار را می‌خوانند).
- برای اتصال با pgAdmin/DBeaver: `localhost:54321`، کاربر `postgres`، رمز `temp_root`.
- حذف کامل داده‌ها: `docker compose down -v`

**دادهٔ نمونه** — کد شرکت: `dev`، رمز همه: `LifeDrop-Local-1`

| کاربر | نقش |
| --- | --- |
| manager@dev.local | مدیر فروش |
| supervisor@dev.local | سرپرست (ناحیهٔ اربیل) |
| finance@dev.local | مدیر حسابات |
| rep1@dev.local | نماینده (اربیل) — فقط با گوشی تأییدشده |
| rep2@dev.local | نماینده (سلیمانیه) — فقط با گوشی تأییدشده |

سه مشتری تأییدشده با موقعیت (P-001، D-001، W-001). برای آزمایش Check-in، موقعیت یک مشتری را به جایی که هستید ببرید (پنل یا SQL داخل `db\local\seed_dev.sql`).

## ۴. API و پنل

دو پنجرهٔ PowerShell:

```powershell
.\scripts\local\Start-Api.ps1     # http://localhost:5256  (با نقش محدود lifedrop_app)
.\scripts\local\Start-Panel.ps1   # http://localhost:5173
```

در پنل با `dev` / `manager@dev.local` / `LifeDrop-Local-1` وارد شوید.

## ۵. اپ روی گوشی اندروید

1. در گوشی: Developer options → **USB debugging** روشن؛ با کابل وصل کنید و اجازه را تأیید کنید.
2. API در حال اجرا باشد (بخش ۴)، سپس:
   ```powershell
   .\scripts\local\Run-Android.ps1
   ```
   `adb reverse` باعث می‌شود گوشی API را روی `http://127.0.0.1:5256` ببیند (آدرس پیش‌فرض اپ در Debug).
3. در اپ: کد شرکت `dev`، `rep1@dev.local`، رمز → **تۆمارکردنی ئەم مۆبایلە** (ثبت گوشی).
4. در پنل، تب Devices → **Approve**.
5. در اپ: **چوونەژوورەوە** (ورود) → شروع شیفت → برنامهٔ امروز.
6. در پنل، تب Visits یک ویزیت برای rep1 بسازید → در اپ برنامه را تازه کنید.

اشکال‌یابی با Visual Studio: پروژهٔ `src\LifeDrop.Mobile` را باز کنید، گوشی را به‌عنوان مقصد انتخاب و F5 بزنید (قبلش `adb reverse tcp:5256 tcp:5256`).

## ۶. پیش از هر push: همهٔ بررسی‌های CI

```powershell
.\scripts\local\Test-All.ps1            # تست‌های DB، تست‌های .NET، پنل (lint، type، test، build)
.\scripts\local\Test-All.ps1 -Android   # + build اندروید
```

اگر همه سبز بود، CI هم سبز می‌شود (همان پایگاه‌داده و همان دستورها).

## مشکلات رایج

| مشکل | راه‌حل |
| --- | --- |
| `docker compose` خطای اتصال | Docker Desktop را باز کنید و صبر کنید تا «Running» شود |
| API: `BLOCKER ... superuser` | رشتهٔ اتصال باید `lifedrop_app` باشد، نه `postgres` (Start-Api.ps1 درست است) |
| اپ: «پەیوەندی بە سێرڤەرەوە نییە» | API روشن است؟ `adb reverse tcp:5256 tcp:5256` را دوباره اجرا کنید (بعد از قطع کابل لازم است) |
| ورود نماینده: «فقط با گوشی تأییدشده» | گوشی را ثبت و در پنل تأیید کنید (بخش ۵) |
| `adb` شناخته نمی‌شود | پوشهٔ `platform-tools` را به PATH اضافه کنید: با Visual Studio معمولاً `C:\Program Files (x86)\Android\android-sdk\platform-tools`، با Android Studio `%LOCALAPPDATA%\Android\Sdk\platform-tools` |
