# LinguaLite

مینی‌اپ تلگرام برای یادگیری زبان با لایتنر، دیتای جدا برای هر کاربر، و تکمیل خودکار کارت با OpenRouter.

## اجرای لوکال

```powershell
dotnet run
```

برای باز کردن پروژه از `LinguaLite.sln` استفاده کنید.

## CapRover

این فایل‌ها باید در ریشه پروژه باشند:

- `captain-definition`
- `Dockerfile`
- `.dockerignore`

در CapRover برای اپ `lingua-lite` این envها را بگذارید:

```text
DATA_DIR=/data
TELEGRAM_BOT_TOKEN=توکن_بات_تلگرام
```

`OPENROUTER_API_KEY` اختیاری است. اگر آن را نگذاری، هر کاربر می‌تواند API key خودش را در بخش تنظیمات اپ وارد کند.

```text
OPENROUTER_API_KEY=sk-or-v1-...
```

Volume پایدار:

```text
Path in App: /data
Label: lingua-lite-data
```

دیتای هر کاربر جدا ذخیره می‌شود:

```text
/data/users/tg_TELEGRAM_USER_ID/deck.json
```

اگر `TELEGRAM_BOT_TOKEN` تنظیم باشد، API فقط `initData` معتبر تلگرام را قبول می‌کند.

## Deploy

با CLI:

```powershell
caprover deploy
```

یا از داشبورد CapRover همین پوشه را zip کنید. دقت کنید `captain-definition` باید در ریشه zip باشد، نه داخل یک فولدر اضافه.

## اتصال تلگرام

در `@BotFather`:

1. `/mybots`
2. انتخاب بات
3. `Bot Settings`
4. `Configure Mini App`
5. `Enable Mini App`
6. آدرس HTTPS اپ را بدهید.

برای دکمه پایین چت:

```text
/setmenubutton
```

## OpenRouter

در بخش تنظیمات اپ:

- `OpenRouter API Key`
- `Model`

مدل پیش‌فرض رایگان:

```text
google/gemma-4-31b-it:free
```

این مدل رایگان، instruction-tuned و text-to-text است و طبق لیست مدل‌های OpenRouter از `response_format` پشتیبانی می‌کند؛ برای ساخت JSON تمیز کارت لغت از مدل‌های رایگان تخصصی code یا safety مناسب‌تر است.

بعد در بخش افزودن، فقط کلمه یا عبارت را بنویسید و دکمه `پر کن` را بزنید تا فیلدهای کارت کامل شوند.
