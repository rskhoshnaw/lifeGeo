# استقرار LifeDrop روی یک سرور (برای هر شرکت یک سرور جدا)

**توجه (D14):** این راهنما هنگام استقرار به‌کار می‌رود؛ تا پایان کامل تست‌ها و امکانات چیزی روی سرور نمی‌رود. هر شرکت مشتری سرور (VPS) و دیتابیس جدا دارد (بخش «یک سرور برای هر شرکت» پایین فایل).

هدف: یک سرور برای حداکثر حدود ۵۰ ویزیتور، با API، پایگاه‌داده و HTTPS روی همان سرور؛ پنل وب روی Cloudflare Pages. فایل‌ها در پوشهٔ `deploy/` هستند.
این راهنما با Docker کار می‌کند؛ هیچ چیز دیگری (دات‌نت، PostgreSQL) روی سرور نصب نمی‌شود.

## ۱. چه چیزی بخرید

| مورد | مشخصات |
| --- | --- |
| سرور مجازی (VPS) ساده با root، پیشنهاد: Hetzner Cloud (آلمان یا فنلاند) یا Contabo؛ نه سرویس «مدیریت‌شده» بدون root | حداقل ۲ vCPU، ۴ گیگابایت RAM، ۴۰ گیگابایت SSD/NVMe؛ پیشنهاد: ۲–۴ vCPU، ۸ گیگابایت RAM، ۸۰ گیگابایت NVMe؛ Ubuntu 24.04 LTS؛ IP ثابت؛ دسترسی root |
| دامنه | مثلاً `yourdomain.com`؛ دو زیردامنه: `api.yourdomain.com` (سرور) و `panel.yourdomain.com` (پنل) |
| فضای backup | یک فضای جدا از سرور (سرور دوم، Object Storage یا حتی کامپیوتر خودتان با scp)، ۲۰–۵۰ گیگابایت |

حجم تخمینی: ۵۰ نفر × هر دقیقه یک نقطه × ۱۰ ساعت = ۳۰ هزار نقطه در روز؛ با نگهداری ۳۰ روز حدود ۹۰۰ هزار ردیف (چند صد مگابایت). خلاصهٔ شیفت‌ها همیشه می‌ماند.

## ۲. آماده‌سازی سرور (یک بار)
1. در پنل دامنه، برای `api.yourdomain.com` یک رکورد **A** به IP سرور بسازید (و برای پنل طبق بخش ۶). صبر کنید تا منتشر شود (`ping api.yourdomain.com`).
2. با SSH وارد شوید، سیستم را به‌روز کنید و فایروال را ببندید به‌جز SSH و وب:
   ```bash
   apt update && apt -y upgrade
   ufw allow OpenSSH && ufw allow 80/tcp && ufw allow 443/tcp && ufw enable
   ```
   بهتر است ورود با رمز SSH را خاموش کنید و فقط با کلید وارد شوید.
3. Docker را نصب کنید (`curl -fsSL https://get.docker.com | sh`) و git و python3 را (`apt -y install git python3`).
4. مخزن را روی `/opt/lifedrop` بگیرید. اگر مخزن خصوصی است از یک **deploy key فقط‌خواندنی** استفاده کنید (در GitHub: Settings → Deploy keys)، نه رمز حساب خودتان.
   ```bash
   git clone git@github.com:rskhoshnaw/lifeGeo.git /opt/lifedrop && cd /opt/lifedrop
   ```

## ۳. تنظیمات
```bash
cd /opt/lifedrop/deploy
cp .env.example .env && chmod 600 .env
nano .env        # API_DOMAIN، PANEL_ORIGIN و رمزها را پر کنید
```
- رمزها را با `openssl rand -base64 36` بسازید و فقط روی همین سرور نگه دارید؛ آن‌ها را در چت یا مخزن نگذارید.
- `PANEL_ORIGIN` نشانی پنل است، مثلاً `https://panel.yourdomain.com` (بدون `/` پایانی).
- `TRACKING_RETENTION_DAYS=30` یعنی نقاط دقیق ۳۰ روز می‌مانند و پاک می‌شوند؛ خلاصهٔ شیفت‌ها می‌ماند. `RATE_LIMIT_PER_MINUTE=600` برای اینکه وای‌فای دفتر که یک نشانی مشترک دارد مسدود نشود.
- اگر Firebase دارید (push)، کلید JSON را در `deploy/secrets/service-account.json` بگذارید؛ بدون آن push خاموش می‌ماند (`docs/PUSH.md`).

## ۴. راه‌اندازی
```bash
cd /opt/lifedrop/deploy
docker compose up -d db            # پایگاه‌داده
./setup-db.sh                      # پایگاه‌داده، نقش محدود lifedrop_app، همهٔ migrationها، grants
./create-super-admin.sh            # ایمیل و رمز (حداقل ۱۲ نویسه) را می‌پرسد
docker compose up -d --build       # API و Caddy (گواهی HTTPS خودکار)
```
بررسی:
```bash
curl https://api.yourdomain.com/health/ready     # {"status":"ready","database":"up",...}
```
اگر `database: insecure_role` دیدید، API با نقش superuser وصل شده؛ رمز و `ConnectionStrings` را بررسی کنید (باید `lifedrop_app` باشد).

## ۵. اولین شرکت
1. پنل را باز کنید و با کد شرکت `platform`، ایمیل و رمز Super Admin وارد شوید.
2. تب **شرکت‌ها** → «شرکت جدید» (کد شرکت، نام، ایمیل و رمز اولین مدیر).
3. مدیر شرکت با کد شرکت خودش وارد می‌شود، کاربران، منطقه‌ها، تیم‌ها و محصولات را می‌سازد (`docs/phase0-api-access-matrix.md`).

## ۶. پنل وب (Cloudflare Pages)
مراحل در `src/LifeDrop.Web/README.md` است؛ نکته‌ها: متغیر `VITE_API_BASE_URL=https://api.yourdomain.com`، و دامنهٔ سفارشی `panel.yourdomain.com`. همان نشانی باید در `PANEL_ORIGIN` باشد، وگرنه مرورگر درخواست‌ها را مسدود می‌کند (CORS).

## ۷. اپ اندروید
در صفحهٔ ورود، نشانی API را `https://api.yourdomain.com` بدهید. ساخت و توزیع APK (امضا، Google Play یا فایل مستقیم) موضوع جداست.

## ۸. Backup (از روز اول)
```bash
sudo mkdir -p /var/backups/lifedrop
crontab -e      # اضافه کنید:
10 3 * * * /opt/lifedrop/deploy/backup.sh >> /var/log/lifedrop-backup.log 2>&1
```
- هر شب یک dump فشرده در `BACKUP_DIR` ساخته می‌شود، فایل خراب یا خالی رد می‌شود، فایل‌های قدیمی‌تر از `BACKUP_KEEP_DAYS` حذف می‌شوند، و اگر `BACKUP_REMOTE` تنظیم باشد یک نسخه به جای دوم می‌رود (rclone یا scp).
- **یک backup که restore نشده، backup نیست:** بعد از اولین backup و سپس هر ماه `./restore-test.sh` را اجرا کنید؛ آخرین dump را در یک پایگاه‌داده موقت برمی‌گرداند، تعداد شرکت‌ها و migrationها را نشان می‌دهد و آن را پاک می‌کند. به پایگاه‌داده واقعی دست نمی‌زند.
- بازگردانی واقعی بعد از خرابی: سرور تازه، `docker compose up -d db`، `./setup-db.sh`، سپس `docker compose exec -T db pg_restore -U postgres -d lifedrop --clean --if-exists --no-owner < فایل.dump` و `docker compose up -d`.

## ۹. به‌روزرسانی
```bash
cd /opt/lifedrop && git pull && cd deploy
./backup.sh                      # پیش از هر به‌روزرسانی
./setup-db.sh                    # فقط migrationهای جدید را اعمال می‌کند
docker compose up -d --build
```

## ۱۰. پایش
- بیرونی: یک سرویس پایش آنلاین (مثلاً UptimeRobot رایگان) روی `https://api.yourdomain.com/health/ready` با هشدار ایمیل.
- روی سرور: `docker compose ps`، `docker compose logs -f api`؛ متریک‌ها از داخل سرور (`METRICS_TOKEN` را بگذارید): `docker compose exec api sh -c ...` یا Prometheus روی همان سرور؛ قواعد هشدار نمونه در `docs/OBSERVABILITY.md`. مسیر `/metrics` از اینترنت بسته است.

## ۱۱. چک‌لیست امنیت
- رمزها فقط در `deploy/.env` (دسترسی ۶۰۰)؛ پایگاه‌داده و API پورت عمومی ندارند (فقط ۸۰ و ۴۴۳ باز است).
- ورود SSH فقط با کلید؛ به‌روزرسانی خودکار امنیتی (`unattended-upgrades`).
- Super Admin: یک یا دو نفر، رمز طولانی و یکتا؛ هر خواندن داده‌ی شرکت دیگر با دلیل در `platform_audit_logs` می‌ماند.
- نگهداری موقعیت کارکنان: قانون محلی و متن رضایت‌نامه را بررسی کنید؛ مدت نگهداری را با `TRACKING_RETENTION_DAYS` تنظیم می‌کنید.

## ۱۲. اگر مشکلی بود
| نشانه | علت محتمل |
| --- | --- |
| Caddy گواهی نمی‌گیرد | رکورد A هنوز به این سرور نمی‌رسد یا پورت ۸۰/۴۴۳ بسته است؛ `docker compose logs caddy` |
| `/health/ready` ۵۰۳ با `database: down` | رمز `APP_DB_PASSWORD` با آنچه `setup-db.sh` گذاشته فرق دارد؛ دوباره `./setup-db.sh` را اجرا کنید |
| ورود از پنل خطای شبکه می‌دهد | `PANEL_ORIGIN` با نشانی دقیق پنل یکی نیست |
| همه ۴۲۹ می‌گیرند | `RATE_LIMIT_PER_MINUTE` را بالاتر ببرید (و `ForwardedHeaders__Enabled` در compose فعال است) |

## One server per company, one app for all (steps 4.2 / 4.3)
Every company gets its own VPS, database and API, so a problem on one never touches another. The app is the same for all of them and holds
no company address. At build time give it a base domain (`dotnet publish -p:LifeDropBaseDomain=lifedrop.example`); company code `acme` then means
`https://acme.lifedrop.example`. To add a company: create its VPS, deploy this kit on it, point the DNS name `acme.<base domain>` at that VPS (Caddy
gets the certificate), create the tenant with code `acme` (`deploy/create-super-admin.sh` / platform company). Nothing changes in the app.
`-p:LifeDropBootstrapUrl=https://...` is only a single-server fallback (and the debug server field in debug builds).

Per-company settings in that server's `deploy/.env`:
- `COMPANY_INDUSTRY=pharma` (default) or `food`: a food distributor gets shops/restaurants wording instead of pharmacies/doctors, and no doctor-only form fields. Panel and app both read it from `GET /company-config/<code>`.
- `SUPPORT_PHONE=+9647510686828`: shown on the panel and app sign-in screens.
- Optional `CompanyDirectory__<code>__ApiBaseUrl` (https) if that company's API lives under another address.
