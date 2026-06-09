namespace PiiRemover.Core.Licensing;

public class LicenseInfo
{
    public string OrgName { get; set; } = string.Empty;
    public string LicenseId { get; set; } = string.Empty;
    public DateOnly IssuedDate { get; set; }
    public DateOnly ExpiryDate { get; set; }
    public int MaxClients { get; set; } = 999;
    // 0 = unlimited requests; any positive value = hard cap on total API calls
    public long RequestQuota { get; set; } = 0;
    public List<string> Features { get; set; } = new();
}
