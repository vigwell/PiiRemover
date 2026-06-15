namespace PiiRemover.Core.Logging;

public enum LogMode
{
    Production, // Parameters only — no large text bodies
    Debug       // Full input/output text included
}
