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

    // ── Line-level ───────────────────────────────────────────────────────
    WholeLine,      // Redacts / removes the ENTIRE LINE if the pattern is found
                    //   anywhere on that line.  Pattern is a .NET regex sub-expression.
                    //   Use plain text (auto-escaped) or a regex fragment.
                    //   Set field ReplaceWith to "" to DELETE the line entirely.
                    //   Examples:
                    //     "Patient:"      removes every line containing "Patient:"
                    //     "\d{9}"         removes every line with a 9-digit sequence

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

    /// <summary>
    /// Optional newline-separated list of plain-text markers.
    /// The pattern only fires AFTER the LAST occurrence of any marker found earliest in the text.
    /// Empty / null = start of document.
    /// Example: "Patient:\nשם:"
    /// </summary>
    public string? ScopeStart { get; set; }

    /// <summary>
    /// Optional newline-separated list of plain-text markers.
    /// The pattern only fires BEFORE the FIRST occurrence of any marker found in the text.
    /// If no marker is found, <see cref="ScopeEndPosition"/> is used as fallback.
    /// Empty / null = rely solely on ScopeEndPosition.
    /// Example: "אבחנות פעילות:\nתלונה עיקרית:\nסיכום:\nBLURRED\nUNSPECIFIED"
    /// </summary>
    public string? ScopeEnd { get; set; }

    /// <summary>
    /// Mandatory fallback: if ScopeEnd markers are defined but none are found in the document,
    /// the scope ends at this character offset from the document start.
    /// Also acts as an absolute cap when no ScopeEnd markers are defined at all.
    /// Default: 500 characters — covers a typical medical document header.
    /// Set to 0 to disable the position cap (use only marker-based scoping).
    /// </summary>
    public int ScopeEndPosition { get; set; } = 500;
}
