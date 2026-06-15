using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using PiiRemover.Core.Engines;

namespace PiiRemover.Data;

/// <summary>
/// Seeds default PII fields/patterns and a demo API client on first run.
/// Entirely idempotent — skips any row that already exists by name.
/// </summary>
public static class PiiDataSeeder
{
    // Fixed demo API key shown once in the console on first seed.
    // Production clients are created through the backoffice.
    private const string DemoClientName = "Demo Client";
    private const string DemoApiKey     = "demo-api-key-changeme-12345";

    public static void Seed(string connectionString, string? namesFilePath = null)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();

        SeedDemoClient(conn);
        SeedFields(conn);
        SeedNamesFile(conn, namesFilePath);
    }

    // ── Demo client ───────────────────────────────────────────────────────────

    private static void SeedDemoClient(SqliteConnection conn)
    {
        var exists = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM Clients WHERE Name = @name", new { name = DemoClientName });
        if (exists > 0) return;

        var hash = HashKey(DemoApiKey);
        conn.Execute(
            "INSERT INTO Clients (Name, ApiKeyHash, IsActive) VALUES (@name, @hash, 1)",
            new { name = DemoClientName, hash });

        Console.WriteLine("=======================================================");
        Console.WriteLine($"  Demo client created.");
        Console.WriteLine($"  API Key : {DemoApiKey}");
        Console.WriteLine($"  Header  : X-Api-Key: {DemoApiKey}");
        Console.WriteLine("  Change this key via the backoffice after first login.");
        Console.WriteLine("=======================================================");
    }

    // ── PII field definitions ─────────────────────────────────────────────────

    private record FieldDef(string Name, string Replace, (string type, string pattern, int priority)[] Patterns);

    private static readonly FieldDef[] Fields =
    [
        new("Email Address", "[email]",
        [
            ("Regex", @"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}", 100)
        ]),

        // Israeli national ID (ת.ז.) — 9-digit number, Luhn-like checksum.
        // Regex catches the shape; false positives on raw 9-digit numbers are acceptable
        // because the redaction engine later validates the Luhn checksum in code.
        new("Israeli ID (ת.ז.)", "[ID]",
        [
            ("Regex", @"\b\d{9}\b", 100)
        ]),

        new("Israeli Phone (טלפון)", "[PHONE]",
        [
            // Mobile  05X-XXXXXXX  or  +972-5X-XXXXXXX
            ("Regex", @"\b0(?:5[0-9]|7[2-9])[- ]?\d{3}[- ]?\d{4}\b", 100),
            // Landline  0X-XXXXXXX
            ("Regex", @"\b0[23489][- ]?\d{3}[- ]?\d{4}\b", 90),
            // International prefix
            ("Regex", @"\+972[- ]?(?:0)?[2-9][- ]?\d{3}[- ]?\d{4}", 95)
        ]),

        new("Credit Card (כרטיס אשראי)", "[CARD]",
        [
            // Visa
            ("Regex", @"\b4[0-9]{3}[- ]?[0-9]{4}[- ]?[0-9]{4}[- ]?[0-9]{4}\b", 100),
            // Mastercard
            ("Regex", @"\b5[1-5][0-9]{2}[- ]?[0-9]{4}[- ]?[0-9]{4}[- ]?[0-9]{4}\b", 100),
            // Amex (15-digit)
            ("Regex", @"\b3[47][0-9]{2}[- ]?[0-9]{6}[- ]?[0-9]{5}\b", 100),
            // Diners / other 16-digit
            ("Regex", @"\b(?:6011|65|64[4-9])[0-9]{2}[- ]?[0-9]{4}[- ]?[0-9]{4}[- ]?[0-9]{4}\b", 90)
        ]),

        new("Date of Birth (תאריך לידה)", "[DOB]",
        [
            // DD/MM/YYYY  DD-MM-YYYY  DD.MM.YYYY
            ("Regex", @"\b(0?[1-9]|[12][0-9]|3[01])[\/\-\.](0?[1-9]|1[012])[\/\-\.](19|20)\d{2}\b", 100),
            // YYYY-MM-DD (ISO)
            ("Regex", @"\b(19|20)\d{2}-(0?[1-9]|1[012])-(0?[1-9]|[12][0-9]|3[01])\b", 90)
        ]),

        new("Passport Number (דרכון)", "[PASSPORT]",
        [
            // Israeli passport: 8-digit numeric string in context
            ("Regex", @"\b[0-9]{8}\b", 80),
            // Foreign passport: letter(s) + 6–9 digits
            ("Regex", @"\b[A-Z]{1,2}[0-9]{6,9}\b", 90)
        ]),

        new("IP Address", "[IP]",
        [
            ("Regex",
             @"\b(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\b",
             100)
        ]),

        // Bank account — Israeli format: 2-3 digit branch + 5-9 digit account
        new("Bank Account (חשבון בנק)", "[BANK]",
        [
            ("Regex", @"\b\d{2,3}[-\/]\d{5,9}\b", 80)
        ]),

        // Person name (English) — title + capitalised words
        new("Person Name (English)", "[NAME]",
        [
            ("Regex", @"\b(?:Mr|Mrs|Ms|Miss|Dr|Prof|Eng)\.?\s+[A-Z][a-z]{1,20}(?:\s+[A-Z][a-z]{1,20}){0,2}\b", 70)
        ]),

        // Common Hebrew first names — ConstList (pipe-separated keywords)
        new("שם פרטי (עברי)", "[שם]",
        [
            ("ConstList",
             "דוד|משה|יוסף|אברהם|יעקב|יצחק|שלמה|ישראל|מאיר|אהרון|" +
             "חיים|שמעון|מרדכי|בנימין|אליהו|רפאל|גדעון|אמיר|עמית|יאיר|" +
             "איתי|נועם|גיא|רון|עידו|תומר|שיר|אורי|ניר|יובל|" +
             "שרה|רחל|לאה|מרים|דינה|רבקה|נעמי|רות|אסתר|יעל|" +
             "דבורה|חנה|תמר|רונית|מיכל|שושנה|זיוה|אורית|נורית|גילה|" +
             "ליאת|מאיה|נועה|שיר|דנה|ליה|טל|עדן|אלה|יובל",
             90)
        ]),

        // Common Hebrew family names
        new("שם משפחה (עברי)", "[משפחה]",
        [
            ("ConstList",
             "כהן|לוי|מזרחי|פרץ|ביטון|אברהם|פרידמן|שפירא|שמש|דוד|" +
             "אוחיון|חדד|בן דוד|בן משה|גולדברג|גרינברג|וינשטיין|" +
             "שלום|אלון|בר|גל|זיו|חן|טל|כץ|לם|מור|נוי|עוז|פז|" +
             "רז|שן|תם|אמיר|בן ארי|גבאי|דגן|הראל|זהבי|חיימוב|" +
             "טוב|יפה|כרמי|לבנה|מלכה|נגר|סלע|עמר|פלד|צור|קדם|" +
             "רוזן|שגב|תורג|אבני|בכר|גפן|דרור|הוד|זמר|חפץ|טבי",
             85)
        ]),

        // IBAN / SWIFT
        new("IBAN / SWIFT", "[IBAN]",
        [
            // IBAN: IL + 2 check digits + 19 digits  (Israel)
            ("Regex", @"\bIL\d{2}[0-9]{19}\b", 100),
            // Generic IBAN: 2 letters + 2 digits + up to 30 alphanum
            ("Regex", @"\b[A-Z]{2}\d{2}[A-Z0-9]{4,30}\b", 70),
            // SWIFT/BIC: 8 or 11 chars
            ("Regex", @"\b[A-Z]{4}[A-Z]{2}[A-Z0-9]{2}(?:[A-Z0-9]{3})?\b", 65)
        ]),

        // Israeli company / association registration numbers (ח.פ. / ע.מ.)
        new("מספר חברה / עוסק (ח.פ.)", "[ח.פ.]",
        [
            // 9-digit company reg (same shape as ID — higher priority in context)
            ("Regex", @"(?:ח\.?פ\.?|ע\.?מ\.?|עוסק מורשה)[:\s#]*(\d{9})", 100),
            ("Regex", @"\b5[0-9]{8}\b", 60)   // Israeli companies start with 5x
        ]),

        // Driver licence / licence plate
        new("Israeli Licence Plate (לוחית רישוי)", "[PLATE]",
        [
            // New format: 12-345-67 or 1234567 (7 digits)
            ("Regex", @"\b\d{2,3}[-–]\d{2,3}[-–]\d{2,3}\b", 90),
            ("Regex", @"\b\d{7,8}\b", 50)
        ]),
    ];

    // ── Seeding logic ─────────────────────────────────────────────────────────

    private static void SeedFields(SqliteConnection conn)
    {
        // Run only once — if seeding was already completed (even if fields were later deleted
        // manually by the admin), do not recreate them. A fresh DB (Settings table empty)
        // will re-seed automatically.
        var alreadySeeded = conn.ExecuteScalar<string>(
            "SELECT Value FROM Settings WHERE Key = 'PiiFieldsSeedCompleted'");
        if (alreadySeeded == "1") return;

        foreach (var f in Fields)
        {
            var fieldId = conn.ExecuteScalar<int?>(
                "SELECT Id FROM PiiFields WHERE FieldName = @name AND ClientId IS NULL",
                new { name = f.Name });

            if (fieldId is null)
            {
                fieldId = conn.ExecuteScalar<int>(
                    """
                    INSERT INTO PiiFields (ClientId, FieldName, ReplaceWith, IsActive)
                    VALUES (NULL, @name, @replace, 1);
                    SELECT last_insert_rowid();
                    """,
                    new { name = f.Name, replace = f.Replace });
            }

            foreach (var (type, pattern, priority) in f.Patterns)
            {
                var patExists = conn.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM PiiPatterns WHERE FieldId = @fid AND Pattern = @p",
                    new { fid = fieldId, p = pattern });
                if (patExists == 0)
                {
                    conn.Execute(
                        "INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority) VALUES (@fid, @type, @pattern, @priority)",
                        new { fid = fieldId, type, pattern, priority });
                }
            }
        }

        // Mark as seeded so manual deletes survive app restarts
        conn.Execute("""
            INSERT INTO Settings (Key, Value, Description)
            VALUES ('PiiFieldsSeedCompleted', '1', 'Set by PiiDataSeeder on first run — prevents default fields from being re-created after manual deletion.')
            ON CONFLICT(Key) DO UPDATE SET Value = '1'
            """);
    }

    // ── FileList seed: NAMES_TO_REDACT.DAT ───────────────────────────────────

    private const string NamesFieldName = "Names List (FileList)";

    private static void SeedNamesFile(SqliteConnection conn, string? filePath)
    {
        // Skip if already seeded
        var exists = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM PiiFields WHERE FieldName = @name AND ClientId IS NULL",
            new { name = NamesFieldName });
        if (exists > 0) return;

        // Resolve the names file: check supplied path, then well-known download location
        var candidates = new List<string?> { filePath };
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "NAMES_TO_REDACT.DAT"));

        string? content = null;
        foreach (var candidate in candidates)
        {
            if (candidate is not null && File.Exists(candidate))
            {
                content = File.ReadAllText(candidate, System.Text.Encoding.UTF8);
                Console.WriteLine($"[PiiDataSeeder] Seeding names from: {candidate}");
                break;
            }
        }

        if (content is null)
        {
            Console.WriteLine("[PiiDataSeeder] NAMES_TO_REDACT.DAT not found — skipping names list seed. " +
                              "Import it later via Admin → PII Fields → Import File List.");
            return;
        }

        var terms = FileListEngine.ParseFile(content);
        if (terms.Count == 0) return;

        var serialized = FileListEngine.Serialize(terms);

        var fieldId = conn.ExecuteScalar<int>(
            """
            INSERT INTO PiiFields (ClientId, FieldName, ReplaceWith, IsActive)
            VALUES (NULL, @name, '[שם]', 1);
            SELECT last_insert_rowid();
            """,
            new { name = NamesFieldName });

        conn.Execute(
            "INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority) VALUES (@fid, 'FileList', @pattern, 80)",
            new { fid = fieldId, pattern = serialized });

        Console.WriteLine($"[PiiDataSeeder] Seeded '{NamesFieldName}' with {terms.Count} terms.");
    }

    private static string HashKey(string raw)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
