-- ============================================================
-- PiiRemover — Comprehensive Medical Document PII Fix
-- Run on: C:\WebApps\PiiRemover\piiremovals.db
-- Date: 2026-06-30
-- ============================================================

BEGIN TRANSACTION;

-- ════════════════════════════════════════════════════════════
-- PART 1 — FIX EXISTING PATTERNS (scope + false-positive cleanup)
-- ════════════════════════════════════════════════════════════

-- ── 1a. Remove 500-char scope cap from ALL existing patterns ─────────────────
-- Medical reports have PII anywhere — name at top, phone at bottom.
UPDATE PiiPatterns SET ScopeEndPosition = 0 WHERE ScopeEndPosition > 0;

-- ── 1b. Delete Passport "any 8 digits" pattern (too generic for medical docs) ─
-- \b[0-9]{8}\b matches reference numbers, section codes, dates without separators, etc.
-- The stricter pattern 13 (\b[A-Z]{1,2}[0-9]{6,9}\b) is kept — it requires letters.
DELETE FROM PiiPatterns WHERE Id = 12;

-- ── 1c. Delete Licence Plate "any 7-8 digits" pattern (too generic) ──────────
-- \b\d{7,8}\b matches phone extensions, report numbers, etc.
-- The dashed format pattern 24 (\b\d{2,3}[-–]\d{2,3}[-–]\d{2,3}\b) is kept.
DELETE FROM PiiPatterns WHERE Id = 25;

-- ── 1d. Disable Bank Account field — causes false positive on patient IDs ─────
-- \b\d{2,3}[-\/]\d{5,9}\b matches patient ID format "01-3825782" as a bank account.
-- Medical documents rarely contain bank accounts; disabling prevents wrong redaction.
UPDATE PiiFields SET IsActive = 0 WHERE Id = 8;

-- ── 1e. Tighten Israeli ID regex to not match dashed formats ──────────────────
-- \b\d{9}\b already requires 9 consecutive digits — dashes break the \b boundary,
-- so it won't match "01-3825782-1" directly. The AfterLabel below handles that case.
-- No change needed to the regex itself.


-- ════════════════════════════════════════════════════════════
-- PART 2 — ADD MISSING FIELDS
-- ════════════════════════════════════════════════════════════

-- ── 2a. Patient Name (שם מטופל) ──────────────────────────────────────────────
INSERT INTO PiiFields (ClientId, FieldName, ReplaceWith, IsActive, IsPreserve, Priority)
VALUES (NULL, 'שם מטופל (Patient Name)', '█', 1, 0, 500);

INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority, ScopeEndPosition)
SELECT Id, 'AfterLabel', 'שם מטופל:', 100, 0
FROM PiiFields WHERE FieldName = 'שם מטופל (Patient Name)' AND ClientId IS NULL;

-- Variant with space before colon
INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority, ScopeEndPosition)
SELECT Id, 'AfterLabel', 'שם מטופל :', 95, 0
FROM PiiFields WHERE FieldName = 'שם מטופל (Patient Name)' AND ClientId IS NULL;


-- ── 2b. Patient ID — dashed format (ת.ז מטופל) ───────────────────────────────
-- "ת.ז מטופל : patient id 01-3825782-1" — whole rest-of-line is PII
-- Added to existing Israeli ID field (Id=2) so it stays grouped there.
INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority, ScopeEndPosition)
VALUES (2, 'AfterLabel', 'ת.ז מטופל :', 110, 0);

INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority, ScopeEndPosition)
VALUES (2, 'AfterLabel', 'ת.ז מטופל:', 105, 0);

-- Short form seen on some forms
INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority, ScopeEndPosition)
VALUES (2, 'AfterLabel', 'ת.ז.:', 100, 0);

INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority, ScopeEndPosition)
VALUES (2, 'AfterLabel', 'ת.ז :', 95, 0);


-- ── 2c. Referring Doctor (רופא מפנה) ─────────────────────────────────────────
INSERT INTO PiiFields (ClientId, FieldName, ReplaceWith, IsActive, IsPreserve, Priority)
VALUES (NULL, 'רופא מפנה (Referring Doctor)', '█', 1, 0, 490);

INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority, ScopeEndPosition)
SELECT Id, 'AfterLabel', 'רופא מפנה', 100, 0
FROM PiiFields WHERE FieldName = 'רופא מפנה (Referring Doctor)' AND ClientId IS NULL;

INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority, ScopeEndPosition)
SELECT Id, 'AfterLabel', 'ד"ר|2', 80, 0
FROM PiiFields WHERE FieldName = 'רופא מפנה (Referring Doctor)' AND ClientId IS NULL;

INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority, ScopeEndPosition)
SELECT Id, 'AfterLabel', 'דר''|2', 75, 0
FROM PiiFields WHERE FieldName = 'רופא מפנה (Referring Doctor)' AND ClientId IS NULL;


-- ── 2d. License Number (מספר רשיון) ──────────────────────────────────────────
INSERT INTO PiiFields (ClientId, FieldName, ReplaceWith, IsActive, IsPreserve, Priority)
VALUES (NULL, 'מספר רשיון (License No)', '█', 1, 0, 490);

INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority, ScopeEndPosition)
SELECT Id, 'AfterLabel', 'מספר רשיון|1', 100, 0
FROM PiiFields WHERE FieldName = 'מספר רשיון (License No)' AND ClientId IS NULL;

-- Spelling variant with yod
INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority, ScopeEndPosition)
SELECT Id, 'AfterLabel', 'מספר רישיון|1', 95, 0
FROM PiiFields WHERE FieldName = 'מספר רשיון (License No)' AND ClientId IS NULL;


-- ════════════════════════════════════════════════════════════
-- PART 3 — Enable Names List
-- ════════════════════════════════════════════════════════════

-- ── 3a. Activate the Names List ──────────────────────────────────────────────
-- The list has 1,000,000+ Israeli names (whole-word, case-insensitive).
-- Catches names anywhere in the document body, not just after labels.
-- e.g. "צריך לבדוק שוב את משה יבגי בבדיקה חוזרת" → both words redacted.
UPDATE PiiFields SET IsActive = 1 WHERE Id = 15;


-- ════════════════════════════════════════════════════════════
-- PART 4 — VERIFY: show final state of all active fields
-- ════════════════════════════════════════════════════════════
COMMIT;

SELECT
  f.Id       AS FieldId,
  f.FieldName,
  f.IsActive,
  f.Priority AS FieldPri,
  p.Id       AS PatId,
  p.PatternType,
  substr(p.Pattern, 1, 60) AS Pattern,
  p.Priority AS PatPri,
  p.ScopeEndPosition
FROM PiiFields f
LEFT JOIN PiiPatterns p ON p.FieldId = f.Id
WHERE f.ClientId IS NULL
ORDER BY f.IsActive DESC, f.Id, p.Id;
