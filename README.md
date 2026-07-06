# LinguaLite

مینی‌اپ تلگرام برای یادگیری زبان با لایتنر، کارت فیدبک، دیکشنری هوشمند، اصلاح و تحلیل متن، خروجی/ورودی JSON، ربات تلگرام و پنل ادمین جدا.

## اجرای لوکال

```powershell
dotnet run --launch-profile http
```

آدرس لوکال:

```text
http://localhost:5005
http://localhost:5005/admin
```

توکن ادمین لوکال در `Properties/launchSettings.json` تنظیم شده است:

```text
ADMIN_TOKEN=local-admin
```

در حالت Development اگر `DATABASE_URL` یا `POSTGRES_CONNECTION_STRING` وجود نداشته باشد، اپ برای تست لوکال از فایل `App_Data/local-database.json` استفاده می‌کند. این پوشه در گیت ignore است.

## دیتابیس

در سرور باید PostgreSQL تنظیم شود تا دیتا با هر دیپلوی نپرد.

یکی از این envها کافی است:

```text
DATABASE_URL=postgres://USER:PASSWORD@HOST:5432/DATABASE
```

یا:

```text
POSTGRES_CONNECTION_STRING=Host=HOST;Port=5432;Database=DATABASE;Username=USER;Password=PASSWORD;SSL Mode=Prefer
```

برای دیتابیس CapRover مثل `srv-captain--kousar-db` معمولا الگو این است:

```text
DATABASE_URL=postgres://postgres:YOUR_PASSWORD@srv-captain--kousar-db:5432/postgres
```

اگر پسورد کاراکتر خاص دارد، URL encode کن یا از `POSTGRES_CONNECTION_STRING` استفاده کن.

## Envهای اصلی CapRover

```text
DATABASE_URL=postgres://...
TELEGRAM_BOT_TOKEN=توکن_ربات_تلگرام
ADMIN_TOKEN=یک_توکن_قوی_برای_ادمین
OPENROUTER_MODEL=google/gemma-4-31b-it:free
OPENROUTER_REFERER=https://YOUR_DOMAIN
```

کلید OpenRouter اختیاری است:

```text
OPENROUTER_API_KEY=sk-or-v1-...
```

اگر کلید را روی سرور نگذاری، کاربر می‌تواند کلید خودش را در تب اکانت وارد کند. کلید کاربر فقط در مرورگر همان دستگاه ذخیره می‌شود.

## پنل ادمین

پنل ادمین داخل مینی‌اپ نمایش داده نمی‌شود و جداست:

```text
https://YOUR_DOMAIN/admin
```

در پنل ادمین می‌توانی این‌ها را مدیریت کنی:

- کاربران، Telegram ID، username، وضعیت فعال/غیرفعال و یادآوری
- پلن‌ها، رنگ badge، دسترسی ابزارها و سقف روزانه/ماهانه
- سهمیه جدا برای کارت‌سازی AI، دیکشنری و اصلاح متن
- کدهای فعال‌سازی پلن
- مدل OpenRouter
- تنظیمات ربات، Mini App URL و Webhook

برای محدودیت‌ها مقدار `-1` یعنی نامحدود.

## ربات تلگرام

Webhook:

```text
https://YOUR_DOMAIN/api/bot/webhook
```

در پنل ادمین مقدارهای `Public Base URL` و `Telegram Mini App URL` را تنظیم کن و بعد دکمه تنظیم Webhook را بزن.

فرمان‌های ربات:

```text
/start
/help
/status
/due
/code LL-XXXX
/remind_on
/remind_off
/add word | معنی
/feedback wrong -> correct
```

ربات Telegram ID، username و chat id را ذخیره می‌کند تا در پنل ادمین دیده شوند و یادآوری‌ها کار کنند.

## شناسه کاربر

در تلگرام، کاربر با `initData` معتبر تلگرام شناسایی می‌شود و شناسه ذخیره‌سازی به شکل `tg_USER_ID` است. بنابراین همان کاربر روی گوشی و کامپیوتر به کارت‌های خودش می‌رسد.

در لوکال، اپ از `X-Dev-User-Id` یا شناسه محلی مرورگر استفاده می‌کند.

## قابلیت‌ها

- کارت‌های Word، Sentence، Question و Feedback
- مرور لایتنر با نمایش اول روی کارت و بعد دکمه پشت کارت
- افزودن، ویرایش و حذف کارت برای کاربر
- فیدبک کارت برای اشتباه‌های واقعی مثل `I programmer -> I am a programmer`
- تکمیل خودکار کارت با OpenRouter
- دیکشنری هوشمند با دکمه افزودن مستقیم به لایتنر
- اصلاح و تحلیل متن با دکمه افزودن به فیدبک‌ها
- خروجی و ورودی JSON با پشتیبانی بهتر از فارسی
- کد فعال‌سازی پلن برای کاربر
- پنل ادمین با پلن‌های رنگی و سهمیه‌های جدا

## Deploy

فایل‌های CapRover در ریشه پروژه هستند:

- `captain-definition`
- `Dockerfile`
- `.dockerignore`

با CLI:

```powershell
caprover deploy
```

یا از داشبورد CapRover همین پوشه را zip کن. `captain-definition` باید در ریشه zip باشد.

## OpenRouter

مدل پیش‌فرض:

```text
google/gemma-4-31b-it:free
```

مدل از env یا تنظیمات پنل ادمین می‌آید. برای تغییر مدل در سرور، `OPENROUTER_MODEL` را عوض کن یا از پنل ادمین مقدار OpenRouter Model را ذخیره کن.
