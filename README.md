# LinguaLite

مینی‌اپ تلگرام برای یادگیری زبان با لایتنر، دیتای جدا برای هر کاربر، کارت فیدبک، خروجی/ورودی دیتا، پنل ادمین و تکمیل خودکار با OpenRouter.

## اجرای لوکال

```powershell
dotnet run
```

پروژه با `LinguaLite.sln` باز می‌شود.

## تنظیمات CapRover

Envهای اصلی:

```text
DATA_DIR=/data
TELEGRAM_BOT_TOKEN=توکن_بات_تلگرام
ADMIN_TOKEN=یک_توکن_قوی_برای_ادمین
OPENROUTER_MODEL=google/gemma-4-31b-it:free
```

کلید OpenRouter اختیاری است. اگر روی سرور بگذاری، همه کاربران از همان استفاده می‌کنند:

```text
OPENROUTER_API_KEY=sk-or-v1-...
```

اگر نگذاری، کاربر می‌تواند کلید خودش را در بخش ابزار وارد کند.

Volume پایدار:

```text
Path in App: /data
Label: lingua-lite-data
```

داده‌ها در فایل زیر ذخیره می‌شوند:

```text
/data/database.json
```

شناسه کاربر تلگرام (`tg_USER_ID`) مبنای ذخیره‌سازی است؛ بنابراین همان کاربر روی موبایل و دسکتاپ به همان کارت‌ها می‌رسد.

## قابلیت‌ها

- کارت‌های Word، Sentence، Question و Feedback
- تکمیل کارت با OpenRouter
- Export و Import کارت‌ها با JSON
- کد فعال‌سازی برای باز کردن قابلیت‌ها
- پنل ادمین برای دیدن کاربران و ساخت access code
- کنترل active/plan/features از API ادمین

## Deploy

فایل‌های CapRover در ریشه پروژه هستند:

- `captain-definition`
- `Dockerfile`
- `.dockerignore`

با CLI:

```powershell
caprover deploy
```

یا از داشبورد CapRover همین پوشه را zip کنید. `captain-definition` باید در ریشه zip باشد.

## OpenRouter

مدل پیش‌فرض فعلی:

```text
google/gemma-4-31b-it:free
```

مدل از config سرور می‌آید، نه از UI کاربر. برای تغییر مدل، مقدار `OPENROUTER_MODEL` را عوض کنید.
