using System.Text.RegularExpressions;

namespace PiiRemover.Api.Services;

/// <summary>
/// Post-processing pipeline applied to every STT transcript result.
/// Converts spoken decimals, normalises years, replaces spoken punctuation commands.
/// Ported from RT.Microservice (Rads4Vet).
/// </summary>
public static class TranscriptProcessor
{
    public static string Process(string transcript)
    {
        transcript = ConvertDecimalNumbers(transcript);
        transcript = NormalizeSpokenDates(transcript);
        transcript = ApplyPunctuationCommands(transcript);
        return transcript;
    }

    // ── Decimal numbers ──────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"]="0",["one"]="1",["two"]="2",["three"]="3",["four"]="4",
        ["five"]="5",["six"]="6",["seven"]="7",["eight"]="8",["nine"]="9",
        ["ten"]="10",["eleven"]="11",["twelve"]="12",["thirteen"]="13",
        ["fourteen"]="14",["fifteen"]="15",["sixteen"]="16",["seventeen"]="17",
        ["eighteen"]="18",["nineteen"]="19",["twenty"]="20",["thirty"]="30",
        ["forty"]="40",["fifty"]="50",["sixty"]="60",["seventy"]="70",
        ["eighty"]="80",["ninety"]="90",["hundred"]="100",
    };

    private static readonly Dictionary<string, string> SpanishNumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cero"]="0",["uno"]="1",["dos"]="2",["tres"]="3",["cuatro"]="4",
        ["cinco"]="5",["seis"]="6",["siete"]="7",["ocho"]="8",["nueve"]="9",["diez"]="10",
    };

    private const string NumberWordGroup =
        "zero|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety|hundred";

    private const string SpanishNumberGroup =
        "cero|uno|dos|tres|cuatro|cinco|seis|siete|ocho|nueve|diez";

    private static string ConvertDecimalNumbers(string t)
    {
        t = Regex.Replace(t, $@"\b({NumberWordGroup})\s+point\s+({NumberWordGroup})\b",
            m => $"{NumberWords.GetValueOrDefault(m.Groups[1].Value, m.Groups[1].Value)}.{NumberWords.GetValueOrDefault(m.Groups[2].Value, m.Groups[2].Value)}",
            RegexOptions.IgnoreCase);
        t = Regex.Replace(t, $@"\b({NumberWordGroup})\s+point\s+(\d+)\b",
            m => $"{NumberWords.GetValueOrDefault(m.Groups[1].Value, m.Groups[1].Value)}.{m.Groups[2].Value}",
            RegexOptions.IgnoreCase);
        t = Regex.Replace(t, $@"\b(\d+)\s+point\s+({NumberWordGroup})\b",
            m => $"{m.Groups[1].Value}.{NumberWords.GetValueOrDefault(m.Groups[2].Value, m.Groups[2].Value)}",
            RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\b(\d+)\s+point\s+(\d+)\b", "$1.$2", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, $@"\b({SpanishNumberGroup})\s+punto\s+({SpanishNumberGroup})\b",
            m => $"{SpanishNumberWords.GetValueOrDefault(m.Groups[1].Value, m.Groups[1].Value)}.{SpanishNumberWords.GetValueOrDefault(m.Groups[2].Value, m.Groups[2].Value)}",
            RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\b(\d+)\s+punto\s+(\d+)\b", "$1.$2", RegexOptions.IgnoreCase);
        return t;
    }

    // ── Spoken dates / years ─────────────────────────────────────────────────

    private static readonly Dictionary<string, int> Ones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"]=0,["one"]=1,["two"]=2,["three"]=3,["four"]=4,["five"]=5,["six"]=6,["seven"]=7,
        ["eight"]=8,["nine"]=9,["ten"]=10,["eleven"]=11,["twelve"]=12,["thirteen"]=13,
        ["fourteen"]=14,["fifteen"]=15,["sixteen"]=16,["seventeen"]=17,["eighteen"]=18,["nineteen"]=19,
    };

    private static readonly Dictionary<string, int> Tens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["twenty"]=20,["thirty"]=30,["forty"]=40,["fifty"]=50,
        ["sixty"]=60,["seventy"]=70,["eighty"]=80,["ninety"]=90,
    };

    private const string SpokenUnder100 =
        @"(?:zero|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety)(?:\s*-?\s*(?:one|two|three|four|five|six|seven|eight|nine))?";

    private static int? ParseSpokenNumberUnder100(string text)
    {
        text = Regex.Replace(text.Trim().ToLowerInvariant().Replace("-", " "), @"\s+", " ");
        if (Regex.IsMatch(text, @"^\d{1,2}$")) { var v = int.Parse(text); return v is >= 0 and <= 99 ? v : null; }
        int total = 0;
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "and") continue;
            if (Ones.TryGetValue(part, out var o)) total += o;
            else if (Tens.TryGetValue(part, out var ten)) total += ten;
            else return null;
        }
        return total is >= 0 and <= 99 ? total : null;
    }

    private static string NormalizeSpokenDates(string t)
    {
        t = Regex.Replace(t, $@"\btwo\s+thousand(?:\s+and)?\s+({SpokenUnder100}|\d{{1,2}})\b",
            m => { var p = ParseSpokenNumberUnder100(m.Groups[1].Value); return p.HasValue ? (2000 + p.Value).ToString() : m.Value; },
            RegexOptions.IgnoreCase);
        t = Regex.Replace(t,
            @"\btwenty\s+twenty(?:\s*-?\s*((?:one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen)|\d{1,2}))?\b",
            m => { if (!m.Groups[1].Success) return "2020"; var p = ParseSpokenNumberUnder100(m.Groups[1].Value); return p.HasValue ? (2020 + p.Value).ToString() : m.Value; },
            RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\b2000\s+(\d{2})\b", m => (2000 + int.Parse(m.Groups[1].Value)).ToString());
        t = Regex.Replace(t, @"\b2000(\d{2})\b",    m => (2000 + int.Parse(m.Groups[1].Value)).ToString());
        t = Regex.Replace(t, @"\b200(\d{2})\b",     m => { var y = 2000 + int.Parse(m.Groups[1].Value); return y is >= 2000 and <= 2099 ? y.ToString() : m.Value; });
        return t;
    }

    // ── Punctuation commands ─────────────────────────────────────────────────

    private static string ApplyPunctuationCommands(string t)
    {
        // English
        t = Regex.Replace(t, @"\s*\bperiod\b\s*",           ".", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bcomma\b\s*",            ",", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bquestion mark\b\s*",    "?", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bexclamation mark\b\s*", "!", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bexclamation point\b\s*","!", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bsemicolon\b\s*",        ";", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bcolon\b\s*",            ":", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bslash\b\s*",            "/", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\basterisk\b\s*",         "*", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bpercent sign\b\s*",     "%", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bplus sign\b\s*",        "+", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bminus sign\b\s*",       "-", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\ben dash\b\s*",          "–", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bem dash\b\s*",          "—", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bhyphen\b\s*",           "-", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bdash\b\s*",             "—", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bopen parenthesis\b\s*", "(", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bclose parenthesis\b\s*",")", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bopen bracket\b\s*",     "[", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bclose bracket\b\s*",    "]", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bopen brace\b\s*",       "{", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bclose brace\b\s*",      "}", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bquotation mark\b\s*",   "\"", RegexOptions.IgnoreCase);

        // Spanish
        t = Regex.Replace(t, @"\s*\bpunto y coma\b\s*",                    ";",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bdos puntos\b\s*",                      ":",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bsigno de porcentaje\b\s*",             "%",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bsigno de interrogaci[oó]n\b\s*",       "?",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bsigno de exclamaci[oó]n\b\s*",         "!",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bsigno de m[aá]s\b\s*",                 "+",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bsigno de menos\b\s*",                  "-",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\babrir par[eé]ntesis\b\s*",             "(",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bcerrar par[eé]ntesis\b\s*",            ")",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\babrir corchete\b\s*",                  "[",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bcerrar corchete\b\s*",                 "]",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\babrir llave\b\s*",                     "{",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bcerrar llave\b\s*",                    "}",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\braya de incisi[oó]n\b\s*",             "–",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bgu[ií][oó]n largo\b\s*",              "—",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bpunto\b\s*",                           ".",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bcoma\b\s*",                            ",",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bbarra\b\s*",                           "/",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\basterisco\b\s*",                       "*",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bcomillas\b\s*",                        "\"", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bgu[ií][oó]n\b\s*",                    "-",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\braya\b\s*",                            "—",  RegexOptions.IgnoreCase);

        // Hebrew
        t = Regex.Replace(t, @"\s*נקודה ופסיק\s*(?=\s|$)",                        ";",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*פתיחת סוגריים מרובעים\s*(?=\s|$)",             "[",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*סגירת סוגריים מרובעים\s*(?=\s|$)",             "]",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*פתיחת סוגריים מסולסלים\s*(?=\s|$)",            "{",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*סגירת סוגריים מסולסלים\s*(?=\s|$)",            "}",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*מקף ארוך\s*(?=\s|$)",                          "—",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*מקף בינוני\s*(?=\s|$)",                        "–",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*נקודה\s*(?=\s|$)",                             ".",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*פסיק\s*(?=\s|$)",                              ",",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*נקודתיים\s*(?=\s|$)",                          ":",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*סימן שאלה\s*(?=\s|$)",                         "?",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*סימן קריאה\s*(?=\s|$)",                        "!",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*סימן אחוז\s*(?=\s|$)",                         "%",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*סימן פלוס\s*(?=\s|$)",                         "+",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*סימן מינוס\s*(?=\s|$)",                        "-",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*פתיחת סוגריים\s*(?=\s|$)",                     "(",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*סגירת סוגריים\s*(?=\s|$)",                     ")",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*קו נטוי\s*(?=\s|$)",                           "/",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*כוכבית\s*(?=\s|$)",                            "*",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*מרכאות\s*(?=\s|$)",                            "\"", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*מקף\s*(?=\s|$)",                               "-",  RegexOptions.IgnoreCase);

        // Arabic
        t = Regex.Replace(t, @"\s*فاصلة منقوطة\s*(?=\s|$)",                      ";",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*علامة النسبة المئوية\s*(?=\s|$)",              "%",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*علامة استفهام\s*(?=\s|$)",                     "?",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*علامة تعجب\s*(?=\s|$)",                        "!",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*علامة الجمع\s*(?=\s|$)",                       "+",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*علامة الطرح\s*(?=\s|$)",                       "-",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*فتح قوس مربع\s*(?=\s|$)",                      "[",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*إغلاق قوس مربع\s*(?=\s|$)",                    "]",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*فتح قوس معقوف\s*(?=\s|$)",                     "{",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*إغلاق قوس معقوف\s*(?=\s|$)",                   "}",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*علامات الاقتباس\s*(?=\s|$)",                   "\"", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*شرطة طويلة\s*(?=\s|$)",                        "—",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*شرطة متوسطة\s*(?=\s|$)",                       "–",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*نقطة\s*(?=\s|$)",                              ".",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*فاصلة\s*(?=\s|$)",                             ",",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*نقطتان\s*(?=\s|$)",                            ":",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*فتح قوس\s*(?=\s|$)",                           "(",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*إغلاق قوس\s*(?=\s|$)",                         ")",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*خط مائل\s*(?=\s|$)",                           "/",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*نجمة\s*(?=\s|$)",                              "*",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*شرطة\s*(?=\s|$)",                              "-",  RegexOptions.IgnoreCase);

        // French
        t = Regex.Replace(t, @"\s*\bpoint[-\s]virgule\b\s*",               ";",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bpoint d[''']interrogation\b\s*",       "?",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bpoint d[''']exclamation\b\s*",         "!",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bdeux points\b\s*",                     ":",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bsigne de pourcentage\b\s*",            "%",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bparenth[eè]se ouvrante\b\s*",         "(",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bparenth[eè]se fermante\b\s*",         ")",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bcrochet ouvrant\b\s*",                 "[",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bcrochet fermant\b\s*",                 "]",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\baccolade ouvrante\b\s*",               "{",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\baccolade fermante\b\s*",               "}",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\btiret demi[-\s]cadratin\b\s*",        "–",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\btiret cadratin\b\s*",                  "—",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\btrait d[''']union\b\s*",               "-",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bpoint\b\s*",                           ".",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bvirgule\b\s*",                         ",",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bsigne plus\b\s*",                      "+",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bsigne moins\b\s*",                     "-",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bbarre oblique\b\s*",                   "/",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bast[ée]risque\b\s*",                  "*",  RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\bguillemets\b\s*",                      "\"", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*\btiret\b\s*",                           "-",  RegexOptions.IgnoreCase);

        return NormalizePunctuationSpacing(t);
    }

    private static string NormalizePunctuationSpacing(string t)
    {
        t = Regex.Replace(t, @"(\d)\.(\d)", "$1__DECIMAL__$2");
        t = Regex.Replace(t, @"\s+([.,!?;:%*/–—\-])", "$1");
        t = Regex.Replace(t, @"\s+([\)\]\}""])", "$1");
        t = Regex.Replace(t, @"\s+([\(\[\{])", "$1");
        t = Regex.Replace(t, @"([\(\[\{""'])\s+", "$1");
        t = Regex.Replace(t, @"([.!?;:,])(?=\S)", "$1 ");
        t = Regex.Replace(t, @"([\)\]\}""'])(?=\S)", "$1 ");
        t = Regex.Replace(t, @"(\d)__DECIMAL__(\d)", "$1.$2");
        t = Regex.Replace(t, @"\s{2,}", " ");
        return t;
    }
}
