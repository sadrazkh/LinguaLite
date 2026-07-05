# LinguaLite

مینی‌اپ تلگرام برای یادگیری زبان با سیستم مرور شبیه لایتنر.

## اجرا در لوکال

```powershell
dotnet run
```

برای باز کردن پروژه در Visual Studio یا Rider از فایل `LinguaLite.sln` استفاده کنید.

## Deploy روی CapRover

این پروژه برای CapRover آماده شده و این فایل‌ها را دارد:

- `captain-definition`
- `Dockerfile`
- `.dockerignore`

### تنظیمات ضروری CapRover

در اپ `lingua-lite` این موارد را تنظیم کنید:

1. در بخش **HTTP Settings** گزینه **Enable HTTPS** را فعال کنید.
2. در بخش **App Configs > Environmental Variables** این مقدار را اضافه کنید:

```text
DATA_DIR=/data
```

3. در بخش **Persistent Directories / Volumes** یک volume اضافه کنید:

```text
Path in App: /data
Label: lingua-lite-data
```

با این کار فایل دیتای برنامه در مسیر `/data/deck.json` ذخیره می‌شود و با هر redeploy پاک نمی‌شود.

### Deploy

کل پوشه پروژه را از ریشه‌ای که فایل `captain-definition` داخل آن است deploy کنید:

```powershell
caprover deploy
```

یا اگر از داشبورد CapRover استفاده می‌کنید، همین پوشه را zip کنید و آپلود کنید. فایل `captain-definition` باید در ریشه zip باشد، نه داخل یک پوشه اضافی.

## ذخیره‌سازی

فعلا ذخیره‌سازی با فایل JSON انجام می‌شود تا MVP سریع و کم‌هزینه بالا بیاید.

- لوکال: `App_Data/deck.json`
- CapRover: `/data/deck.json`

برای نسخه چندکاربره واقعی باید مرحله بعدی این باشد:

- اعتبارسنجی `Telegram.WebApp.initData`
- ذخیره کارت‌ها بر اساس Telegram user id
- مهاجرت از JSON به SQLite یا PostgreSQL

برای شروع روی CapRover، همین JSON همراه با volume کافی است. اگر اپ عمومی و چندکاربره شود، PostgreSQL گزینه بهتر است.

## اتصال به تلگرام

بعد از Deploy، دامنه HTTPS اپ را در BotFather ثبت کنید:

1. بروید به `@BotFather`
2. `/mybots`
3. انتخاب بات
4. `Bot Settings`
5. `Configure Mini App`
6. `Enable Mini App`
7. آدرس HTTPS اپ، مثلا:

```text
https://lingua-lite.your-domain.com
```

برای دکمه پایین چت هم از `/setmenubutton` در BotFather استفاده کنید و همان URL را بدهید.
