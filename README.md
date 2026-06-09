# PiiRemover

On-premises PII redaction service for organisations. Accepts documents (PDF, scanned images, plain text), detects sensitive information using configurable rules, and returns redacted text. No cloud dependencies — runs entirely on Windows / IIS.

---

## Features

- **OCR** — Windows.Media.Ocr (built-in, Hebrew + English, dual-pass) with Tesseract fallback
- **PDF** — text layer extraction + page rasterisation + embedded image OCR via PdfPig / PDFtoImage
- **PII detection** — regex patterns, keyword lists, per-field priority, right-to-left safe replacement
- **Configurable fields** — 13 built-in fields (Israeli ID, phone, email, credit card, DOB, IBAN, Hebrew names, …); add your own via the backoffice
- **API key auth** — per-client SHA-256 hashed keys, managed through the backoffice
- **RSA-2048 signed license** — expiry date + optional request quota enforced at the middleware layer (HTTP 402 on violation)
- **Backoffice** — Razor Pages UI at `/admin` for clients, fields, logs, settings, and password management
- **Windows Event Log** — all operations logged; Production mode (params only) or Debug mode (full text)
- **Automatic log cleanup** — background service deletes `RequestLogs` older than N months (configurable)

---

## Solution structure

```
PiiRemover.sln
├── PiiRemover.Api          — ASP.NET Core 10 (net10.0-windows), IIS in-process
│   ├── Controllers/        — OcrController, RedactController, UtilController
│   ├── Extractors/         — OcrExtractor (Windows OCR + Tesseract), PdfTextExtractor
│   ├── Middleware/         — LicenseMiddleware, ApiKeyMiddleware
│   ├── Pages/Admin/        — Razor backoffice (Login, Dashboard, Clients, Fields, Logs, Settings)
│   └── Services/           — LogCleanupService (BackgroundService)
├── PiiRemover.Core         — Detection engine, models, interfaces, licensing
│   ├── Engines/            — RegexPatternEngine, ConstListEngine, LlmPromptEngine (stub), RedactionOrchestrator
│   ├── Extractors/         — ITextExtractor, PlainTextExtractor, OcrOptions
│   └── Licensing/          — LicenseInfo, LicenseValidator
├── PiiRemover.Data         — SQLite repositories (Dapper)
│   ├── Repositories/       — ClientRepository, FieldRepository, LogRepository, QuotaRepository, SettingsRepository
│   ├── SchemaInitializer   — WAL mode, pragmas, CREATE TABLE IF NOT EXISTS
│   ├── AdminSeeder         — Seeds SHA-256("2026") admin password on first run
│   └── PiiDataSeeder       — Seeds 13 default PII fields + demo API client
├── PiiRemover.LicenseTool  — CLI to generate and verify RSA-signed .lic files
└── PiiRemover.Tests        — xUnit integration tests (WebApplicationFactory, in-memory DB)
```

---

## Quick start (development)

```bash
# 1. Build
dotnet build

# 2. Run
dotnet run --project PiiRemover.Api

# 3. Open Swagger
# http://localhost:5000/swagger

# 4. Open backoffice
# http://localhost:5000/admin
# Username: admin   Password: 2026

# 5. Run tests
dotnet test PiiRemover.Tests
```

On first run the app prints the demo API key to the console:
```
  API Key : demo-api-key-changeme-12345
  Header  : X-Api-Key: demo-api-key-changeme-12345
```
Use this key in Swagger (click **Authorize**) or in the Postman collection.

---

## API endpoints

| Method | Path | Auth | Description |
|---|---|---|---|
| POST | `/api/v1/ocr/extract` | X-Api-Key | Extract text (no redaction) |
| POST | `/api/v1/redact/redact` | X-Api-Key | Extract + remove PII |
| POST | `/api/v1/util/hash` | none | SHA-256 a value |

All endpoints accept `multipart/form-data` with a `file` field (PDF, image, or plain text). No file size limit.

**Example — redact a document:**
```bash
curl -X POST http://localhost:5000/api/v1/redact/redact \
  -H "X-Api-Key: demo-api-key-changeme-12345" \
  -F "file=@document.pdf"
```

**Response:**
```json
{
  "redactedText": "Patient: [NAME]  ID: [ID]  DOB: [DOB]  Phone: [PHONE]",
  "matchCount": 4,
  "fieldsHit": ["Person Name (English)", "Israeli ID (ת.ז.)", "Date of Birth (תאריך לידה)", "Israeli Phone (טלפון)"],
  "durationMs": 312
}
```

---

## Built-in PII fields

| Field | Replacement | What it catches |
|---|---|---|
| Email Address | `[email]` | user@domain.com |
| Israeli ID (ת.ז.) | `[ID]` | 9-digit identity number |
| Israeli Phone | `[PHONE]` | Mobile 05X, landline 0X, +972 |
| Credit Card | `[CARD]` | Visa, Mastercard, Amex, Diners |
| Date of Birth | `[DOB]` | DD/MM/YYYY, YYYY-MM-DD |
| Passport Number | `[PASSPORT]` | Israeli 8-digit + foreign formats |
| IP Address | `[IP]` | IPv4 |
| Bank Account | `[BANK]` | Branch-account number |
| Person Name (English) | `[NAME]` | Mr/Dr/Prof + capitalised words |
| שם פרטי (עברי) | `[שם]` | ~60 common Hebrew first names |
| שם משפחה (עברי) | `[משפחה]` | ~60 common Hebrew family names |
| IBAN / SWIFT | `[IBAN]` | IL IBAN, generic IBAN, BIC |
| מספר חברה (ח.פ.) | `[ח.פ.]` | Israeli company registration |
| Israeli Licence Plate | `[PLATE]` | 12-345-67 format |

All fields are active by default and can be toggled, edited, or extended via the backoffice at `/admin/fields`.

---

## IIS deployment

```bash
dotnet publish PiiRemover.Api -c Release -o C:\inetpub\piiremover
```

Then in IIS:
1. Create a new site pointing to `C:\inetpub\piiremover`
2. Set the application pool to **No Managed Code**
3. Copy `license.lic` and `tessdata/` into the publish folder
4. Browse to `/admin` to verify

The `web.config` is included in the publish output and configures AspNetCoreModuleV2 in-process hosting with no request size limit.

---

## License tool

```bash
# Generate a new RSA key pair (one-time)
dotnet run --project PiiRemover.LicenseTool -- keygen --out keys/

# Issue a license
dotnet run --project PiiRemover.LicenseTool -- generate \
  --org "Acme Corp" \
  --expiry 2027-06-09 \
  --key keys/private.pem \
  --quota 50000 \
  --out license.lic

# Verify a license
dotnet run --project PiiRemover.LicenseTool -- verify --file license.lic
```

> **Keep `private.pem` secret.** Never commit it — it is in `.gitignore`.

---

## Postman collection

Import `PiiRemover.postman_collection.json` from the repo root. Set:
- `{{baseUrl}}` → `http://localhost:5000`
- `{{apiKey}}` → your API key

Every request includes automated test assertions.

---

## Tech stack

| Concern | Choice |
|---|---|
| Framework | ASP.NET Core 10, net10.0-windows10.0.19041.0 |
| Hosting | IIS in-process (AspNetCoreModuleV2) |
| Database | SQLite (WAL mode) + Dapper |
| OCR primary | Windows.Media.Ocr (built-in, no install needed) |
| OCR fallback | Tesseract 5 |
| PDF | PdfPig (text layer) + PDFtoImage/PDFium (rasterise) |
| Image processing | SkiaSharp |
| Licensing | RSA-2048, SHA-256 signed JSON |
| Tests | xUnit + FluentAssertions + WebApplicationFactory |
