# LinguaLite

مینی‌اپ تلگرام برای یادگیری زبان با لایتنر، دیتای جدا برای هر کاربر، کارت فیدبک، خروجی/ورودی دیتا، پنل ادمین و تکمیل خودکار با OpenRouter.

## اجرای لوکال

```powershell
dotnet run
```

پروژه با `LinguaLite.sln` باز می‌شود.

## دیتابیس

ذخیره‌سازی روی PostgreSQL است. دیگر دیتای اصلی داخل کانتینر یا فایل `/data/database.json` ذخیره نمی‌شود.

یکی از این دو env را تنظیم کن:

```text
DATABASE_URL=postgres://USER:PASSWORD@HOST:5432/DATABASE
```

یا:

```text
POSTGRES_CONNECTION_STRING=Host=HOST;Port=5432;Database=DATABASE;Username=USER;Password=PASSWORD;SSL Mode=Prefer
```

اگر در CapRover از One-Click App برای PostgreSQL استفاده می‌کنی، همان مشخصات دیتابیس را در env اپ اصلی بگذار. تا وقتی این env به همان دیتابیس اشاره کند، با هر دیپلوی دیتا نمی‌پرد.

## تنظیمات CapRover

Envهای اصلی:

```text
DATABASE_URL=postgres://...
TELEGRAM_BOT_TOKEN=توکن_بات_تلگرام
ADMIN_TOKEN=یک_توکن_قوی_برای_ادمین
OPENROUTER_MODEL=google/gemma-4-31b-it:free
```

کلید OpenRouter اختیاری است. اگر روی سرور بگذاری، همه کاربران از همان استفاده می‌کنند:

```text
OPENROUTER_API_KEY=sk-or-v1-...
```

اگر نگذاری، کاربر می‌تواند کلید خودش را در بخش ابزار وارد کند.

## شناسه کاربر و سشن

در تلگرام، شناسه کاربر از `initData` معتبر تلگرام گرفته می‌شود و به شکل `tg_USER_ID` ذخیره می‌شود. بنابراین همان کاربر روی موبایل و دسکتاپ به همان کارت‌ها می‌رسد.

در حالت توسعه بدون `TELEGRAM_BOT_TOKEN`، اپ از `X-Dev-User-Id` یا شناسه لوکال مرورگر استفاده می‌کند.

## قابلیت‌ها

- کارت‌های Word، Sentence، Question و Feedback
- کارت فیدبک با دستور AI جدا برای اصلاح اشتباه واقعی کاربر
- تکمیل کارت با OpenRouter
- Export و Import کارت‌ها با JSON
- کد فعال‌سازی برای باز کردن قابلیت‌ها
- پنل ادمین برای دیدن کاربران، فعال/غیرفعال کردن کاربر و ساخت access code

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

مدل پیش‌فرض فعلی:

```text
google/gemma-4-31b-it:free
```

مدل از config سرور می‌آید، نه از UI کاربر. برای تغییر مدل، مقدار `OPENROUTER_MODEL` را عوض کن.
