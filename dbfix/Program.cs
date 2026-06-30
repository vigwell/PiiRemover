using Microsoft.Data.Sqlite;

var dbPath = args.Length > 0 ? args[0] : @"C:\Users\vigens\source\repos\PiiRemover\PiiRemover.Api\piiremovals.db";
var namesPath = @"C:\Users\vigens\source\repos\PiiRemover\hebrew_names_short.txt";

using var conn = new SqliteConnection($"Data Source={dbPath}");
conn.Open();

int Exec(string sql, Dictionary<string, object?>? p = null)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    if (p != null)
        foreach (var kv in p)
            cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
    return cmd.ExecuteNonQuery();
}

object? Scalar(string sql)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return cmd.ExecuteScalar();
}

bool Exists(string sql)
{
    var r = Scalar(sql);
    return r != null && r != DBNull.Value && Convert.ToInt64(r) > 0;
}

Console.WriteLine("=== PiiRemover DB Full Fix ===");
Console.WriteLine($"DB: {dbPath}");
Console.WriteLine();

// ── 1. Remove scope cap from ALL patterns ─────────────────────────────────
Console.WriteLine("1. Remove ScopeEndPosition cap from all patterns...");
var r1 = Exec("UPDATE PiiPatterns SET ScopeEndPosition = 0 WHERE ScopeEndPosition > 0");
Console.WriteLine($"   {r1} patterns updated");

// ── 2. Disable Bank Account field (false positives on patient IDs) ────────
Console.WriteLine("2. Disable Bank Account field...");
var r2 = Exec("UPDATE PiiFields SET IsActive = 0 WHERE FieldName LIKE '%Bank Account%' OR FieldName LIKE '%חשבון בנק%'");
Console.WriteLine($"   {r2} rows");

// ── 3. Delete overly generic patterns ────────────────────────────────────
Console.WriteLine("3. Delete generic Passport 8-digit pattern...");
// Pattern: \b[0-9]{8}\b  — too broad
var r3a = Exec(@"DELETE FROM PiiPatterns WHERE Pattern = '\b[0-9]{8}\b'");
Console.WriteLine($"   {r3a} rows deleted");

Console.WriteLine("   Delete generic Licence Plate 7-8 digit pattern...");
var r3b = Exec(@"DELETE FROM PiiPatterns WHERE Pattern = '\b\d{7,8}\b'");
Console.WriteLine($"   {r3b} rows deleted");

// ── 4. Add AfterLabel patterns for Israeli ID dashed format ──────────────
Console.WriteLine("4. Add ת.ז AfterLabel patterns to Israeli ID field...");
var idFieldId = Scalar("SELECT Id FROM PiiFields WHERE FieldName LIKE '%Israeli ID%' OR FieldName LIKE '%ת.ז%' LIMIT 1");
if (idFieldId != null)
{
    int fid = Convert.ToInt32(idFieldId);
    var tzPatterns = new[] { "ת.ז מטופל :", "ת.ז מטופל:", "ת.ז.:", "ת.ז :" };
    int added = 0;
    foreach (var pat in tzPatterns)
    {
        if (!Exists($"SELECT COUNT(*) FROM PiiPatterns WHERE FieldId={fid} AND Pattern='{pat}'"))
        {
            Exec("INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority, ScopeEndPosition) VALUES (@fid, 'AfterLabel', @pat, 100, 0)",
                new() { ["@fid"] = fid, ["@pat"] = pat });
            added++;
        }
    }
    Console.WriteLine($"   Added {added} patterns to field {fid}");
}
else Console.WriteLine("   WARNING: Israeli ID field not found");

// ── 5. Add שם מטופל field if missing ─────────────────────────────────────
Console.WriteLine("5. Ensure שם מטופל (Patient Name) field exists...");
if (!Exists("SELECT COUNT(*) FROM PiiFields WHERE FieldName LIKE '%מטופל%' AND FieldName LIKE '%Patient Name%'"))
{
    Exec("INSERT INTO PiiFields (ClientId, FieldName, ReplaceWith, IsActive, IsPreserve, Priority) VALUES (NULL, 'שם מטופל (Patient Name)', '█', 1, 0, 500)");
    var newFid = Convert.ToInt32(Scalar("SELECT last_insert_rowid()"));
    Exec("INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority, ScopeEndPosition) VALUES (@fid, 'AfterLabel', 'שם מטופל:', 100, 0)", new() { ["@fid"] = newFid });
    Exec("INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority, ScopeEndPosition) VALUES (@fid, 'AfterLabel', 'שם מטופל :', 95, 0)", new() { ["@fid"] = newFid });
    Console.WriteLine($"   Created field {newFid} with 2 patterns");
}
else Console.WriteLine("   Already exists, skipping");

// ── 6. Add רופא מפנה field if missing ────────────────────────────────────
Console.WriteLine("6. Ensure רופא מפנה (Referring Doctor) field exists...");
if (!Exists("SELECT COUNT(*) FROM PiiFields WHERE FieldName LIKE '%רופא מפנה%'"))
{
    Exec("INSERT INTO PiiFields (ClientId, FieldName, ReplaceWith, IsActive, IsPreserve, Priority) VALUES (NULL, 'רופא מפנה (Referring Doctor)', '█', 1, 0, 490)");
    var newFid = Convert.ToInt32(Scalar("SELECT last_insert_rowid()"));
    Exec("INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority, ScopeEndPosition) VALUES (@fid, 'AfterLabel', 'רופא מפנה', 100, 0)", new() { ["@fid"] = newFid });
    Exec("INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority, ScopeEndPosition) VALUES (@fid, 'AfterLabel', 'ד\"ר|2', 80, 0)", new() { ["@fid"] = newFid });
    Exec("INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority, ScopeEndPosition) VALUES (@fid, 'AfterLabel', 'דר''|2', 75, 0)", new() { ["@fid"] = newFid });
    Console.WriteLine($"   Created field {newFid} with 3 patterns");
}
else Console.WriteLine("   Already exists, skipping");

// ── 7. Add מספר רשיון field if missing ───────────────────────────────────
Console.WriteLine("7. Ensure מספר רשיון (License No) field exists...");
if (!Exists("SELECT COUNT(*) FROM PiiFields WHERE FieldName LIKE '%רשיון%' OR FieldName LIKE '%רישיון%'"))
{
    Exec("INSERT INTO PiiFields (ClientId, FieldName, ReplaceWith, IsActive, IsPreserve, Priority) VALUES (NULL, 'מספר רשיון (License No)', '█', 1, 0, 490)");
    var newFid = Convert.ToInt32(Scalar("SELECT last_insert_rowid()"));
    Exec("INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority, ScopeEndPosition) VALUES (@fid, 'AfterLabel', 'מספר רשיון|1', 100, 0)", new() { ["@fid"] = newFid });
    Exec("INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority, ScopeEndPosition) VALUES (@fid, 'AfterLabel', 'מספר רישיון|1', 95, 0)", new() { ["@fid"] = newFid });
    Console.WriteLine($"   Created field {newFid} with 2 patterns");
}
else Console.WriteLine("   Already exists, skipping");

// ── 8a. Add שם המבוטח AfterLabel variants to Patient Name field ──────────
Console.WriteLine("8a. Add שם המבוטח label variants...");
var patNameFid = Convert.ToInt32(Scalar("SELECT Id FROM PiiFields WHERE FieldName LIKE '%Patient Name%' OR FieldName LIKE '%שם מטופל%' LIMIT 1"));
var patNameLabels = new[] { "שם המבוטח:", "שם המבוטח :", "שם הפונה:", "שם הפונה :", "שם החולה:", "שם החולה :" };
int pnAdded = 0;
foreach (var lbl in patNameLabels)
{
    if (!Exists($"SELECT COUNT(*) FROM PiiPatterns WHERE FieldId={patNameFid} AND Pattern='{lbl}'"))
    {
        Exec("INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority, ScopeEndPosition) VALUES (@fid,'AfterLabel',@pat,100,0)",
            new() { ["@fid"] = patNameFid, ["@pat"] = lbl });
        pnAdded++;
    }
}
Console.WriteLine($"   Added {pnAdded} label variants to field {patNameFid}");

// ── 8b. Add רישיון: label variant to License field ────────────────────────
Console.WriteLine("8b. Add רישיון: label to License field...");
var licFid = Convert.ToInt32(Scalar("SELECT Id FROM PiiFields WHERE FieldName LIKE '%רשיון%' OR FieldName LIKE '%רישיון%' LIMIT 1"));
var licLabels = new[] { "רישיון:|1", "רישיון :|1", "רופא/מטפל:|2" };
int licAdded = 0;
foreach (var lbl in licLabels)
{
    if (!Exists($"SELECT COUNT(*) FROM PiiPatterns WHERE FieldId={licFid} AND Pattern='{lbl}'"))
    {
        Exec("INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority, ScopeEndPosition) VALUES (@fid,'AfterLabel',@pat,100,0)",
            new() { ["@fid"] = licFid, ["@pat"] = lbl });
        licAdded++;
    }
}
Console.WriteLine($"   Added {licAdded} patterns to field {licFid}");

// ── 8c. Add insurance member number pattern (10-digit) ────────────────────
Console.WriteLine("8c. Add 10-digit insurance member number pattern...");
var idFid = Convert.ToInt32(Scalar("SELECT Id FROM PiiFields WHERE FieldName LIKE '%Israeli ID%' OR FieldName LIKE '%ת.ז%' LIMIT 1"));
if (!Exists($@"SELECT COUNT(*) FROM PiiPatterns WHERE FieldId={idFid} AND Pattern='\b\d{{10}}\b'"))
{
    Exec("INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority, ScopeEndPosition) VALUES (@fid,'Regex',@pat,90,0)",
        new() { ["@fid"] = idFid, ["@pat"] = @"\b\d{10}\b" });
    Console.WriteLine("   Added 10-digit regex to Israeli ID field");
}
else Console.WriteLine("   Already exists");

// ── 8d. Postal code (מיקוד) ───────────────────────────────────────────────
Console.WriteLine("8d. Add postal code patterns...");
var idFid2 = Convert.ToInt32(Scalar("SELECT Id FROM PiiFields WHERE FieldName LIKE '%Israeli ID%' OR FieldName LIKE '%ת.ז%' LIMIT 1"));

// Remove whole-line variant (too greedy), use regex only
Exec("DELETE FROM PiiPatterns WHERE FieldId=@fid AND Pattern='מיקוד:'", new() { ["@fid"] = idFid2 });
Exec("DELETE FROM PiiPatterns WHERE FieldId=@fid AND Pattern='מיקוד:|1'", new() { ["@fid"] = idFid2 });

// Israeli postal code regex: exactly 5 or 7 digits (not part of longer number)
if (!Exists($@"SELECT COUNT(*) FROM PiiPatterns WHERE FieldId={idFid2} AND Pattern='\b\d{{5}}(?:\d{{2}})?\b'"))
{
    Exec("INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority, ScopeEndPosition) VALUES (@fid,'Regex',@pat,90,0)",
        new() { ["@fid"] = idFid2, ["@pat"] = @"\b\d{5}(?:\d{2})?\b" });
    Console.WriteLine("   Added 5/7-digit postal code regex");
}
else Console.WriteLine("   Postal code regex already exists");

// ── 8e. Address field ─────────────────────────────────────────────────────
Console.WriteLine("8e. Ensure Address field exists with all label variants...");
long addrFieldId;
if (!Exists("SELECT COUNT(*) FROM PiiFields WHERE FieldName LIKE '%כתובת%'"))
{
    Exec("INSERT INTO PiiFields (ClientId, FieldName, ReplaceWith, IsActive, IsPreserve, Priority) VALUES (NULL, 'כתובת (Address)', '█', 1, 0, 480)");
    addrFieldId = Convert.ToInt64(Scalar("SELECT last_insert_rowid()"));
    Console.WriteLine($"   Created address field {addrFieldId}");
}
else
{
    addrFieldId = Convert.ToInt64(Scalar("SELECT Id FROM PiiFields WHERE FieldName LIKE '%כתובת%' LIMIT 1"));
    Console.WriteLine($"   Using existing address field {addrFieldId}");
}
// Remove any כתובת patterns accidentally added to wrong fields
Exec("DELETE FROM PiiPatterns WHERE Pattern LIKE 'כתובת%' AND FieldId NOT IN (SELECT Id FROM PiiFields WHERE FieldName LIKE '%כתובת%')");
// Remove old whole-line address patterns (no |N), replace with |6
Exec("DELETE FROM PiiPatterns WHERE Pattern IN ('כתובת:','כתובת :','כתובת המבוטח:','כתובת המטופל:','כתובת הרופא:')");
// Ensure all label variants exist (whole line - no |N)
var addrLabels = new[] { "כתובת:|6", "כתובת :|6", "כתובת המבוטח:|6", "כתובת המטופל:|6", "כתובת הרופא:|6" };
int addrAdded = 0;
foreach (var lbl in addrLabels)
{
    if (!Exists($"SELECT COUNT(*) FROM PiiPatterns WHERE FieldId={addrFieldId} AND Pattern='{lbl}'"))
    {
        Exec("INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority, ScopeEndPosition) VALUES (@fid,'AfterLabel',@pat,100,0)",
            new() { ["@fid"] = addrFieldId, ["@pat"] = lbl });
        addrAdded++;
    }
}
Console.WriteLine($"   Added {addrAdded} label variants");

// ── 8f. Barcode data <XXXXX> ──────────────────────────────────────────────
Console.WriteLine("8f. Add barcode regex...");
if (!Exists("SELECT COUNT(*) FROM PiiFields WHERE FieldName LIKE '%Barcode%'"))
{
    Exec("INSERT INTO PiiFields (ClientId, FieldName, ReplaceWith, IsActive, IsPreserve, Priority) VALUES (NULL, 'Barcode Data', '█', 1, 0, 470)");
    var bcFid = Convert.ToInt32(Scalar("SELECT last_insert_rowid()"));
    Exec("INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority, ScopeEndPosition) VALUES (@fid,'Regex',@pat,100,0)",
        new() { ["@fid"] = bcFid, ["@pat"] = @"<[A-Z]{10,}>" });
    Console.WriteLine($"   Created Barcode field {bcFid}");
}
else Console.WriteLine("   Already exists, skipping");

// ── 8. Enable + load Names List ───────────────────────────────────────────
Console.WriteLine("8. Load Hebrew names list...");
var names = File.ReadAllLines(namesPath, System.Text.Encoding.UTF8)
    .Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
var joined = string.Join("\n", names);
var namesFieldId = Scalar("SELECT Id FROM PiiFields WHERE FieldName LIKE '%Names List%' OR FieldName LIKE '%שמות%' LIMIT 1");
if (namesFieldId != null)
{
    int fid = Convert.ToInt32(namesFieldId);
    Exec("UPDATE PiiPatterns SET Pattern = @p, ScopeEndPosition = 0 WHERE FieldId = @fid",
        new() { ["@p"] = joined, ["@fid"] = fid });
    Exec("UPDATE PiiFields SET IsActive = 1 WHERE Id = @fid", new() { ["@fid"] = fid });
    Console.WriteLine($"   Field {fid}: {names.Length} names loaded, ScopeEndPosition=0, IsActive=1");
}
else Console.WriteLine("   WARNING: Names List field not found");

// ── 9. Final summary ──────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("=== Final State ===");
using var sum = conn.CreateCommand();
sum.CommandText = "SELECT f.Id, f.FieldName, f.IsActive, COUNT(p.Id) as PatCount, SUM(CASE WHEN p.ScopeEndPosition > 0 THEN 1 ELSE 0 END) as ScopedPats FROM PiiFields f LEFT JOIN PiiPatterns p ON p.FieldId = f.Id WHERE f.ClientId IS NULL GROUP BY f.Id ORDER BY f.IsActive DESC, f.Id";
using var rdr = sum.ExecuteReader();
while (rdr.Read())
    Console.WriteLine($"  Field {rdr[0],3}: [{(Convert.ToInt64(rdr[2])==1?"✓":" ")}] {rdr[1],-40} {rdr[3]} patterns  {(Convert.ToInt64(rdr[4])>0 ? $"⚠ {rdr[4]} scoped":"")}" );

Console.WriteLine();
Console.WriteLine("Done. Copy PiiRemover.Api\\piiremovals.db to prod.");
