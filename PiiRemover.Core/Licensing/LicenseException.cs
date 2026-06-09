namespace PiiRemover.Core.Licensing;

public class InvalidLicenseException(string message) : Exception(message);

public class LicenseExpiredException(string message) : Exception(message);

public class LicenseMissingException(string message) : Exception(message);
