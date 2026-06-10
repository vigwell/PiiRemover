namespace PiiRemover.Core.Models;

public enum PatternType
{
    // ── Original ──────────────────────────────────────────────────────────
    Regex,          // Full .NET regex. Most powerful; requires regex knowledge.
    ConstList,      // Pipe-separated exact values: Cohen|Levi|Goldberg
    LlmPrompt,      // Future LLM-based detection (stub)

    // ── Simple / no-regex ─────────────────────────────────────────────────
    Like,           // SQL-style wildcards  (* = any chars, ? = any one char)
                    //   *Cohen*  ·  Dr.?  ·  *@gmail.com

    BeginsWith,     // Any word that starts with this prefix (case-insensitive)
                    //   "Dr."  matches  Dr.Cohen, DR.SMITH

    EndsWith,       // Any word that ends with this suffix (case-insensitive)
                    //   "@hospital.org"  matches  user@hospital.org

    WholeWord,      // Exact whole-word, case-insensitive (no regex knowledge needed)
                    //   "smith"  matches  Smith, SMITH  but NOT  Smithsonian

    // ── Context-aware ─────────────────────────────────────────────────────
    AfterLabel,     // Redacts the VALUE following a known label.
                    //   "Patient Name:"       redacts rest of line
                    //   "Patient Name:|2"     redacts next 2 words
                    //   "Date of Birth:|1"    redacts next 1 word
                    //   The label itself is preserved; only the value is redacted.

    BetweenMarkers, // Redacts text between two delimiters on the same line.
                    //   "ID: | DOB"  →  redacts everything between "ID: " and " DOB"

    NumberSequence, // Digit-only sequences of a given length range.
                    //   "9"     exactly 9 digits  (Israeli ID)
                    //   "7,10"  7–10 digits
                    //   Automatically skips sequences embedded in longer numbers.

    // ── File-based ────────────────────────────────────────────────────────
    FileList,       // Large list of exact values loaded from a file.
                    //   Pattern value = newline-separated terms (stored in DB).
                    //   Populated via admin file upload (TXT, CSV, DAT, …).
                    //   Supports plain single-column and fixed-width dual-column
                    //   files (e.g. "Hebrew name   English name" per line —
                    //   both columns are extracted and matched).
                    //   Matching is case-insensitive, whole-word aware.
                    //   Scales to tens of thousands of entries; cached in memory.
}

public class PiiPattern
{
    public int Id { get; set; }
    public int FieldId { get; set; }
    public PatternType PatternType { get; set; }
    public string Pattern { get; set; } = string.Empty;
    public int Priority { get; set; }
}
