namespace PiiRemover.Core.Extractors;

public class OcrOptions
{
    // Ordered list of engines to try. First non-empty result wins.
    // Supported: "WindowsOcr", "Tesseract"
    public List<string> EngineOrder { get; set; } = [];

    // Path to Tesseract tessdata folder
    public string TessdataPath { get; set; } = "tessdata";

    // Tesseract language string (e.g. "heb+eng")
    public string TesseractLanguages { get; set; } = "heb+eng";

    // Max parallel OCR operations. 0 = Environment.ProcessorCount.
    // Prevents CPU/RAM spikes under stress — OCR is heavy.
    public int MaxConcurrency { get; set; } = 0;
}
