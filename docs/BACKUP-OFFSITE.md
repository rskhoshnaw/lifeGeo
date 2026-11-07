# پشتیبان رمزنگاری‌شده روی Google Drive (مرحلهٔ 5.26)

هر ۳ روز دو فایل روی Google Drive می‌رود، هر دو با **کلید عمومی** رمزنگاری‌شده: (۱) یک dump تازه از دیتابیس، (۲) تنظیمات سرور (`deploy/.env` و کلید Firebase)
که برای بالا آوردن دوبارهٔ سرور لازم‌اند. (مرحلهٔ 5.27: دو کلید و فایل تنظیمات.) سرور فقط می‌تواند رمزنگاری کند؛
**کلید خصوصی فقط پیش شماست**. پس اگر سرور، Drive یا توکن آن به دست کسی بیفتد، داده‌ها خوانده نمی‌شوند.
(پشتیبان شبانهٔ محلی `backup.sh` سر جایش می‌ماند.)

> **هشدار:** اگر همهٔ کلیدهای خصوصی گم شوند، هیچ‌کس نمی‌تواند پشتیبان‌ها را باز کند، نه شما و نه ما. برای همین **دو کلید** می‌سازیم؛ هر کدام به تنهایی فایل‌ها را باز می‌کند.
> **هرگز کلید خصوصی را روی سرور یا در چت نگذارید.**

## چطور از گم شدن کلید جلوگیری کنیم
- **کلید ۱:** پیش شما، در مدیر رمز عبور (مثلاً Bitwarden؛ گزینهٔ «دسترسی اضطراری» را برای یک نفر مورد اعتماد روشن کنید) + یک نسخهٔ چاپی.
- **کلید ۲:** جای دیگر و جدا (فلش در گاوصندوق، یا پیش یک شریک یا فرد مورد اعتماد که فقط در حالت اضطراری از آن استفاده کند).
- دو کلید را **در یک جا** نگذارید. اگر یکی گم شد، همان روز یک کلید جدید بسازید، در `AGE_RECIPIENT` عوض کنید و بک‌آپ بعدی با کلیدهای تازه ساخته می‌شود (بک‌آپ‌های قدیمی همان کلیدهای قبلی را می‌خواهند).

## ۱) ساخت دو کلید (روی کامپیوتر خودتان، نه سرور)
1. برنامهٔ `age` را از https://github.com/FiloSottile/age/releases بگیرید (ویندوز: فایل `windows-amd64.zip`) و باز کنید.
2. در PowerShell همان پوشه، **دو بار** (دو فایل متفاوت):
   ```
   .\age-keygen.exe -o key1.txt
   .\age-keygen.exe -o key2.txt
   ```
   هر کدام یک خط مثل `Public key: age1xxxxxxxx...` چاپ می‌کند. هر دو خط (age1...) را کپی کنید، کلیدهای عمومی‌اند.
3. فایل‌های `key1.txt` و `key2.txt` کلیدهای خصوصی‌اند (با `AGE-SECRET-KEY-` شروع می‌شوند). طبق بالا در دو جای جدا نگه دارید و بعد آن‌ها را از کامپیوتر پاک کنید.

## ۲) نصب روی سرور
```
apt install -y age rclone
```

## ۳) تنظیم `deploy/.env` روی سرور
```
nano /opt/lifedrop/deploy/.env
```
این سه خط را اضافه یا پر کنید (دو کلید عمومی را با یک فاصله جدا کنید؛ اگر فقط یکی بگذارید، اسکریپت هشدار می‌دهد) و `BACKUP_REMOTE` را **خالی** بگذارید، چون آن، نسخهٔ رمزنگاری‌نشده می‌فرستد:
```
AGE_RECIPIENT=age1xxxxxxxx... age1yyyyyyyy...
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
باید دو خط بنویسد: `offsite backup written: ...lifedrop-....dump.age` (دیتابیس) و `...lifedrop-config-....tar.age` (تنظیمات). در Google Drive شما پوشهٔ `lifedrop-backups` و این دو فایل دیده می‌شوند.

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

## بازگردانی (روی کامپیوتر شما، با **هر کدام** از دو کلید خصوصی)
1. دو فایل را از Drive بگیرید: `lifedrop-....dump.age` و `lifedrop-config-....tar.age`.
2. رمزگشایی (با `key1.txt` یا `key2.txt`، هر کدام که دارید):
   ```
   .\age.exe -d -i key1.txt -o lifedrop.dump lifedrop-....dump.age
   .\age.exe -d -i key1.txt -o config.tar lifedrop-config-....tar.age
   ```
3. سرور تازه: مخزن را در `/opt/lifedrop` بگیرید؛ `config.tar` را در پوشهٔ `deploy` باز کنید (`tar -xf config.tar -C /opt/lifedrop/deploy`، دو فایل `.env` و `secrets/service-account.json` سر جایشان می‌آیند؛ `chown 1654:1654` و `chmod 400` برای کلید Firebase را فراموش نکنید).
4. فایل `lifedrop.dump` را طبق `docs/DEPLOY.md` (بخش بازگردانی) برگردانید.

**یک بار بازگردانی را با هر دو کلید جداگانه امتحان کنید** (باید هر دو فایل را باز کنند). پشتیبانی که بازگردانی نشده، پشتیبان نیست.
