# پشتیبان رمزنگاری‌شده روی Google Drive (مرحلهٔ 5.26)

هر ۳ روز یک dump تازه از دیتابیس گرفته می‌شود، با **کلید عمومی** شما رمزنگاری می‌شود و روی Google Drive می‌رود. سرور فقط می‌تواند رمزنگاری کند؛
**کلید خصوصی فقط پیش شماست**. پس اگر سرور، Drive یا توکن آن به دست کسی بیفتد، داده‌ها خوانده نمی‌شوند.
(پشتیبان شبانهٔ محلی `backup.sh` سر جایش می‌ماند.)

> **هشدار:** اگر کلید خصوصی (`key.txt`) گم شود، هیچ‌کس نمی‌تواند پشتیبان‌ها را باز کند، نه شما و نه ما. دو نسخه از آن نگه دارید
> (مدیر رمز عبور و یک فلش یا کاغذ در جای امن). **هرگز آن را روی سرور یا در چت نگذارید.**

## ۱) ساخت کلید (روی کامپیوتر خودتان، نه سرور)
1. برنامهٔ `age` را از https://github.com/FiloSottile/age/releases بگیرید (ویندوز: فایل `windows-amd64.zip`) و باز کنید.
2. در PowerShell همان پوشه:
   ```
   .\age-keygen.exe -o key.txt
   ```
   خروجی یک خط مثل `Public key: age1xxxxxxxx...` چاپ می‌کند. **فقط همین خط (age1...) را** کپی کنید، همان کلید عمومی است.
3. فایل `key.txt` کلید خصوصی است (با `AGE-SECRET-KEY-` شروع می‌شود). آن را امن نگه دارید (بالا).

## ۲) نصب روی سرور
```
apt install -y age rclone
```

## ۳) تنظیم `deploy/.env` روی سرور
```
nano /opt/lifedrop/deploy/.env
```
این سه خط را اضافه یا پر کنید (کلید عمومی خودتان را بگذارید) و `BACKUP_REMOTE` را **خالی** بگذارید، چون آن، نسخهٔ رمزنگاری‌نشده می‌فرستد:
```
AGE_RECIPIENT=age1xxxxxxxx...
OFFSITE_RCLONE_REMOTE=gdrive:lifedrop-backups
OFFSITE_KEEP_DAYS=45
```

## ۴) وصل کردن Google Drive به rclone
روی سرور: `rclone config`
- `n` (New remote)، نام: `gdrive`، نوع (Storage): `drive` (Google Drive).
- `client_id` و `client_secret`: خالی بگذارید (Enter).
- **scope:** گزینهٔ `drive.file` را انتخاب کنید (دسترسی فقط به فایل‌هایی که خود rclone می‌سازد، نه کل Drive شما). شماره‌اش را در لیست ببینید (معمولاً ۳).
- `root_folder_id`، `service_account_file`: خالی. `Edit advanced config`: n.
- `Use web browser to automatically authenticate?` → **n** (سرور مرورگر ندارد).
- rclone دستوری مثل `rclone authorize "drive" "eyJ..."` نشان می‌دهد. آن را **روی کامپیوتر خودتان** اجرا کنید (rclone باید آنجا نصب باشد: https://rclone.org/downloads/). مرورگر باز می‌شود، وارد Google شوید و اجازه بدهید. rclone یک متن `{"access_token":...}` چاپ می‌کند؛ آن را کامل در سرور جایگذاری (paste) کنید.
- بقیه: Enter، `y` برای تأیید، `q` برای خروج.

امتحان: `rclone lsd gdrive:` (نباید خطا بدهد.)

## ۵) اولین اجرا
```
/opt/lifedrop/deploy/backup-offsite.sh
```
باید بنویسد `offsite backup written: gdrive:lifedrop-backups/lifedrop-....dump.age`. در Google Drive شما پوشهٔ `lifedrop-backups` و فایل `.age` دیده می‌شود.

## ۶) هر ۳ روز خودکار
```
crontab -e
```
خط زیر را اضافه کنید:
```
15 4 */3 * * /opt/lifedrop/deploy/backup-offsite.sh >> /var/log/lifedrop-offsite.log 2>&1
```
(`*/3` یعنی روزهای ۱، ۴، ۷، … ماه؛ در پایان ماه ممکن است فاصله ۱ تا ۳ روز شود. اشکالی ندارد.) اگر اجرا خطا بدهد، متنش در `/var/log/lifedrop-offsite.log` می‌ماند.

نسخه‌های رمزنگاری‌شدهٔ قدیمی‌تر از `OFFSITE_KEEP_DAYS` (پیش‌فرض ۴۵ روز) از Drive پاک می‌شوند.

## بازگردانی (روی کامپیوتر شما، با کلید خصوصی)
1. فایل `.dump.age` را از Drive بگیرید.
2. رمزگشایی:
   ```
   .\age.exe -d -i key.txt -o lifedrop.dump lifedrop-....dump.age
   ```
3. فایل `lifedrop.dump` را مثل هر پشتیبان دیگر طبق `docs/DEPLOY.md` (بخش بازگردانی) و `./restore-test.sh` برگردانید.

**یک بار هم بازگردانی را امتحان کنید** (فایل را رمزگشایی و با restore-test برگردانید). پشتیبانی که بازگردانی نشده، پشتیبان نیست.
