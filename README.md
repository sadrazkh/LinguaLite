# LinguaLite

LinguaLite یک مینی‌اپ تلگرام و PWA برای یادگیری زبان است. هسته اصلی برنامه لایتنر است، ولی کنار آن کارت فیدبک، دیکشنری هوشمند، اصلاح متن با AI، خروجی/ورودی JSON، پنل ادمین، پلن‌بندی و ربات تلگرام هم دارد.

## مسیرهای مهم

```text
/                  اپ کاربر و PWA
/admin             پنل ادمین جدا از مینی‌اپ
/api/health        سلامت سرور
/api/health/db     سلامت دیتابیس
/api/bot/webhook   وبهوک ربات تلگرام
```

## اجرای لوکال

پیش‌نیاز: .NET 8 SDK

```powershell
cd outputs\LinguaLite
dotnet run --launch-profile http
```

آدرس‌ها:

```text
http://localhost:5005
http://localhost:5005/admin
```

توکن ادمین لوکال داخل `Properties/launchSettings.json` است:

```text
ADMIN_TOKEN=local-admin
```

در حالت Development اگر `DATABASE_URL` یا `POSTGRES_CONNECTION_STRING` ست نشده باشد، دیتای تستی در فایل زیر ذخیره می‌شود:

```text
App_Data/local-database.json
```

## دیتابیس

برای سرور حتما PostgreSQL بگذار تا دیتا با هر deploy از بین نرود.

یکی از این دو روش کافی است:

```text
DATABASE_URL=postgres://USER:PASSWORD@HOST:5432/DATABASE
```

یا:

```text
POSTGRES_CONNECTION_STRING=Host=HOST;Port=5432;Database=DATABASE;Username=USER;Password=PASSWORD;SSL Mode=Prefer
```

نمونه CapRover برای دیتابیسی مثل `srv-captain--kousar-db`:

```text
DATABASE_URL=postgres://postgres:YOUR_PASSWORD@srv-captain--kousar-db:5432/postgres
```

اگر پسورد کاراکترهایی مثل `@` یا `#` دارد، یا URL encode کن یا از `POSTGRES_CONNECTION_STRING` استفاده کن.

## Envهای اصلی سرور

```text
DATABASE_URL=postgres://...
ADMIN_TOKEN=یک_توکن_قوی_برای_ادمین
TELEGRAM_BOT_TOKEN=توکن_ربات
TELEGRAM_BOT_USERNAME=نام_ربات_بدون_@
PUBLIC_BASE_URL=https://YOUR_DOMAIN
TELEGRAM_MINI_APP_URL=https://YOUR_DOMAIN
OPENROUTER_MODEL=google/gemma-4-31b-it:free
OPENROUTER_REFERER=https://YOUR_DOMAIN
```

برای OpenRouter یک کلید یا چند کلید می‌توانی بدهی:

```text
OPENROUTER_API_KEY=sk-or-v1-...
```

یا برای load balance ساده:

```text
OPENROUTER_API_KEYS=sk-or-v1-key1,sk-or-v1-key2,sk-or-v1-key3
```

در حالت چند کلیدی، سرور برای هر درخواست یکی از کلیدها را تصادفی انتخاب می‌کند. کلیدها به کلاینت برنمی‌گردند؛ اپ فقط تعداد کلیدهای فعال را نشان می‌دهد.

اگر هیچ کلیدی روی سرور نگذاری، کاربر می‌تواند کلید خودش را در بخش اکانت وارد کند. آن کلید فقط در مرورگر همان دستگاه ذخیره می‌شود.

## Deploy روی CapRover

فایل‌های deploy در ریشه پروژه هستند:

```text
captain-definition
Dockerfile
.dockerignore
```

با CLI:

```powershell
cd outputs\LinguaLite
caprover deploy
```

یا از همین پوشه zip بگیر و در داشبورد CapRover بده. `captain-definition` باید دقیقا در ریشه zip باشد.

بعد از deploy این‌ها را چک کن:

```text
https://YOUR_DOMAIN/api/health
https://YOUR_DOMAIN/api/health/db
https://YOUR_DOMAIN/admin
```

## ربات تلگرام

وبهوک:

```text
https://YOUR_DOMAIN/api/bot/webhook
```

در پنل ادمین:

1. با `ADMIN_TOKEN` وارد شو.
2. `Public Base URL` و `Telegram Mini App URL` را روی دامنه اصلی بگذار.
3. `Bot Username` را وارد کن.
4. `ربات فعال باشد` را روشن کن.
5. دکمه `تنظیم Webhook ربات` را بزن.
6. دکمه `بررسی وضعیت ربات` را بزن و مطمئن شو Webhook ثبت‌شده با Webhook مورد انتظار یکی است.

فرمان‌های ربات:

```text
/start
/help
/status
/login
/due
/code LL-XXXX
/remind_on
/remind_off
/add word | معنی
/feedback wrong -> correct
```

ورود PWA با تلگرام:

1. کاربر ربات را start می‌کند.
2. دستور `/login` را می‌زند.
3. ربات یک کد ۶ رقمی می‌دهد.
4. کاربر کد را در صفحه ورود PWA وارد می‌کند.
5. همان اکانت تلگرام روی مرورگر و گوشی به همان کارت‌ها وصل می‌شود.

## اپ کاربر

تب‌های اصلی:

- `مرور`: اول فقط روی کارت دیده می‌شود. پشت کارت بعد از دکمه نمایش داده می‌شود.
- `افزودن`: ساخت کارت Word، Sentence، Question و Feedback.
- `کارت‌ها`: لیست کارت‌ها، ویرایش، حذف، نمایش پشت کارت، جعبه لایتنر و تاریخ مرور بعدی به شمسی.
- `دیکشنری`: معنی، تعریف ساده، مترادف، مثال و افزودن مستقیم به لایتنر.
- `اصلاح`: تحلیل جمله، خطاها، نسخه درست، ترجمه و افزودن به فیدبک‌ها.
- `اکانت`: وضعیت کاربر، پلن، مدل AI، گزارش مصرف امروز، تنظیم تم، سطح زبان، بروزرسانی PWA، کد فعال‌سازی، کلید OpenRouter دستگاه، import/export.

کارت فیدبک برای اشتباه‌های واقعی است. مثال:

```text
I programmer -> I am a programmer
```

روی کارت جواب درست لو نمی‌رود؛ کاربر اول باید خودش اصلاح کند، بعد پشت کارت را ببیند.

## پنل ادمین

پنل ادمین جداست و داخل مینی‌اپ برای کاربرها نمایش داده نمی‌شود:

```text
https://YOUR_DOMAIN/admin
```

امکانات:

- مشاهده کاربران، Telegram ID، username، پلن، وضعیت و یادآوری
- صفحه‌بندی کاربران برای لیست‌های بزرگ‌تر
- آمار امروز هر کاربر: فعالیت، کارت اضافه‌شده، مرور، AI کارت، دیکشنری و اصلاح
- فعال/غیرفعال کردن کاربر
- تغییر پلن کاربر
- ساخت و ویرایش پلن‌ها
- تنظیم رنگ badge پلن
- محدودیت جدا برای AI کارت، دیکشنری و اصلاح متن
- ساخت کد دسترسی اتوماتیک یا کاستوم
- تغییر ظرفیت کدهای دسترسی
- دیدن اینکه هر کد توسط چه کسانی استفاده شده
- تنظیم مدل OpenRouter
- تنظیم URLهای تلگرام و وبهوک ربات
- ارسال پیام گروهی یا هدفمند از طریق ربات براساس فیلترهایی مثل پلن، وضعیت، منبع، کد دسترسی و جستجو

در پلن‌ها مقدار `-1` یعنی نامحدود.

## کدهای دسترسی

ادمین می‌تواند کد بسازد:

```text
PLUS1405
PRO-USER-01
LL-ABCD12
```

برای هر کد:

- پلن مشخص می‌شود.
- ظرفیت مصرف مشخص می‌شود.
- ظرفیت بعدا قابل افزایش یا کاهش است، ولی از تعداد مصرف‌شده کمتر نمی‌شود.
- در لیست کدها مشخص می‌شود چه کاربرهایی با آن کد فعال شده‌اند.

کاربر کد را در تب `اکانت` وارد می‌کند یا در ربات می‌زند:

```text
/code PLUS1405
```

## OpenRouter و مدل AI

مدل پیش‌فرض:

```text
google/gemma-4-31b-it:free
```

اولویت مدل:

1. مقدار ذخیره‌شده در پنل ادمین
2. env `OPENROUTER_MODEL`
3. مقدار پیش‌فرض داخل `appsettings.json`

برای کلید API:

1. اگر کاربر در اکانت خودش کلید وارد کرده باشد، همان درخواست با همان کلید می‌رود.
2. اگر کلید کاربر نبود، سرور از `OPENROUTER_API_KEYS` یا `OPENROUTER_API_KEY` استفاده می‌کند.
3. اگر هیچ کلیدی نبود، درخواست AI خطا می‌دهد و پیام تنظیم کلید نمایش داده می‌شود.

سطح زبان کاربر از تب `اکانت` قابل تنظیم است (`A1` تا `C2`). AI از این سطح برای سختی مثال‌ها، تعریف‌ها، تمرین‌ها و اصلاح متن استفاده می‌کند.

## Import و Export

کاربر از تب `اکانت` می‌تواند کارت‌ها را JSON خروجی بگیرد یا وارد کند. خروجی با BOM ساخته می‌شود تا فارسی در ابزارهای مختلف بهتر خوانده شود.

## راهنمای ساخت آموزش با اسکرین‌شات

برای اینکه بعدا با AI یا یک همکار مستند تصویری بسازی، این صفحه‌ها را اسکرین‌شات بگیر:

```text
/                       صفحه مرور و تب‌ها
/ بعد از رفتن به اکانت   گزارش مصرف، کد فعال‌سازی و کلید OpenRouter
/admin                  ورود ادمین
/admin بعد از ورود       تنظیمات ربات، پلن‌ها، کاربران و کدهای دسترسی
```

متن پیشنهادی برای دادن به AI:

```text
این اسکرین‌شات‌ها مربوط به LinguaLite است؛ یک PWA و Telegram Mini App برای یادگیری زبان.
از روی README و تصاویر، یک راهنمای فارسی کاربر و یک راهنمای فارسی ادمین بساز.
روی جریان‌های مرور کارت، افزودن کارت فیدبک، فعال‌سازی پلن با کد، اتصال PWA به ربات، و تنظیم Webhook تمرکز کن.
```

## عیب‌یابی سریع

اگر دیتا لود نمی‌شود:

```text
/api/health
/api/health/db
```

اگر ربات جواب نمی‌دهد:

- `TELEGRAM_BOT_TOKEN` را چک کن.
- در `/admin` دکمه `بررسی وضعیت ربات` را بزن.
- Webhook ثبت‌شده باید با `https://YOUR_DOMAIN/api/bot/webhook` یکی باشد.
- `Bot Enabled` باید روشن باشد.

اگر AI خطا می‌دهد:

- `OPENROUTER_API_KEYS` یا `OPENROUTER_API_KEY` را چک کن.
- مدل را در پنل ادمین یا env بررسی کن.
- سهمیه پلن کاربر را در پنل ادمین ببین.

اگر کاربرها دیتاهای هم را می‌بینند:

- در تلگرام باید `TELEGRAM_BOT_TOKEN` درست باشد تا `initData` معتبر شود.
- خارج از تلگرام کاربر باید با `/login` به اکانت تلگرام وصل شود.
- در Production روی dev header حساب نکن.
