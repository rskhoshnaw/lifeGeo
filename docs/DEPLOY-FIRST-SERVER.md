# اولین سرور LifeDrop — راهنمای قدم‌به‌قدم برای مبتدی

دامنه: `lifegeotracking.com` (ثبت‌شده در Cloudflare). شرکت اول: کد `lifedrops`. سرور: Hostinger KVM 2، Ubuntu 24.04.
نشانی‌ها: API = `https://lifedrops.lifegeotracking.com`، پنل = `https://panel.lifegeotracking.com`.
(اپ از کد شرکت نشانی می‌سازد: کد `lifedrops` ← `https://lifedrops.lifegeotracking.com`؛ پس برای این سرور رکورد `api` لازم نیست.)
توضیح کامل‌تر و دلیل هر کار: `docs/DEPLOY.md`. هر دستور را کپی کن، بزن، و خروجی را با «خروجی درست» مقایسه کن. رمزها را در چت نگذار.

## ۱. رکورد DNS (Cloudflare)
dash.cloudflare.com ← دامنه `lifegeotracking.com` ← **DNS** ← **Records** ← **Add record**:
- Type: `A` — Name: `lifedrops` — IPv4 address: آی‌پی سرور — Proxy status: **DNS only** (ابر خاکستری، نه نارنجی) — Save.

بررسی (در PowerShell ویندوز، چند دقیقه بعد): `nslookup lifedrops.lifegeotracking.com` باید آی‌پی سرور را نشان دهد.

## ۲. ورود به سرور
در PowerShell: `ssh root@آی‌پی-سرور` (بار اول yes). خروجی درست: خط فرمان سرور (`root@...:~#`).

## ۳. آماده‌سازی (یک بار)
```bash
apt update && apt -y upgrade
ufw allow OpenSSH && ufw allow 80/tcp && ufw allow 443/tcp && ufw --force enable
apt -y install git python3 unattended-upgrades
docker --version || curl -fsSL https://get.docker.com | sh
```
خروجی درست: `docker --version` شمارهٔ نسخه می‌دهد؛ `ufw status` سه قاعده را نشان می‌دهد.

## ۴. گرفتن برنامه
```bash
git clone https://github.com/rskhoshnaw/lifeGeo.git /opt/lifedrop
cd /opt/lifedrop/deploy
cp .env.example .env && chmod 600 .env
```

## ۵. تنظیمات (`.env`)
چهار رمز بساز (هر بار دستور را اجرا کن و خروجی را کپی کن): `openssl rand -base64 36`
بعد: `nano .env` و این‌ها را پر کن:
```
API_DOMAIN=lifedrops.lifegeotracking.com
PANEL_ORIGIN=https://panel.lifegeotracking.com
POSTGRES_PASSWORD=<رمز اول>
APP_DB_PASSWORD=<رمز دوم>
JWT_SIGNING_KEY=<رمز سوم>
```
ذخیره: `Ctrl+O`، Enter، `Ctrl+X`. این فایل فقط روی سرور می‌ماند. رمزها را یک جای امن (مدیر رمز) هم نگه دار.
اگر کلید Firebase (Push) داری: `deploy/secrets/service-account.json` (`docs/PUSH.md`)؛ بدون آن Push خاموش است.

## ۶. راه‌اندازی
```bash
cd /opt/lifedrop/deploy
docker compose up -d db
./setup-db.sh
./create-super-admin.sh      # ایمیل: rasulsaleh@gmail.com ؛ رمز حداقل ۱۲ نویسه
docker compose up -d --build   # چند دقیقه طول می‌کشد
curl https://lifedrops.lifegeotracking.com/health/ready
```
خروجی درست: `{"status":"ready","database":"up",...}`. اگر Caddy گواهی نگرفت: رکورد A را بررسی کن و `docker compose logs caddy`.

## ۷. Backup (از همان روز اول)
```bash
mkdir -p /var/backups/lifedrop
( crontab -l 2>/dev/null; echo '10 3 * * * /opt/lifedrop/deploy/backup.sh >> /var/log/lifedrop-backup.log 2>&1' ) | crontab -
/opt/lifedrop/deploy/backup.sh
```
بعد از اولین backup: `./restore-test.sh` (باید موفق شود). یک نسخه را هم روی کامپیوتر خودت نگه دار (`scp`) یا `BACKUP_REMOTE` را تنظیم کن.

## ۸. پنل (Cloudflare)
Cloudflare دیگر Pages را جدا نشان نمی‌دهد؛ پنل مثل یک Worker با فایل‌های ثابت منتشر می‌شود (تنظیمات در `src/LifeDrop.Web/wrangler.jsonc`، شامل دامنهٔ `panel.lifegeotracking.com` و برگشت به `index.html`).
Cloudflare ← **Workers & Pages** ← **Create application** ← **Import a repository** (Connect to Git) ← مخزن `lifeGeo`:
- **Project name:** `lifedrop-panel` (باید دقیقاً همین باشد)؛ Production branch: `main`
- **Advanced settings ← Root directory:** `src/LifeDrop.Web`
- **Build command:** `npm ci && npm run build`؛ **Deploy command:** `npx wrangler deploy` (پیش‌فرض)
- **Build variables:** `VITE_API_BASE_URL` = `https://lifedrops.lifegeotracking.com` و `NODE_VERSION` = `22`
- «Protect with Cloudflare Access» را فعلاً روشن نکن.
بعد از اولین deploy موفق، `https://panel.lifegeotracking.com` خودکار وصل می‌شود.

## ۹. اولین شرکت
پنل را باز کن (`https://panel.lifegeotracking.com`) ← کد شرکت `platform`، ایمیل و رمز Super Admin ← **شرکت‌ها** ← شرکت جدید: کد `lifedrops`، نام `Life Drops`، ایمیل و رمز اولین مدیر.
اپ: کد شرکت `lifedrops` را در صفحهٔ ورود بنویس (نسخهٔ ساخته‌شده با `LifeDropBaseDomain=lifegeotracking.com`).

## ۱۰. پایش
UptimeRobot (رایگان) روی `https://lifedrops.lifegeotracking.com/health/ready` با هشدار ایمیل.
به‌روزرسانی بعدی: `cd /opt/lifedrop && git pull && cd deploy && ./backup.sh && ./setup-db.sh && docker compose up -d --build`
