using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace PiiRemover.Api.Controllers;

[ApiController]
[Route("api/v1/util")]
public class UtilController : ControllerBase
{
    // POST /api/v1/util/hash
    // No API-key auth required — used to generate hashes for client provisioning.
    [HttpPost("hash")]
    public IActionResult Hash([FromBody] HashRequest req)
    {
        if (string.IsNullOrEmpty(req.Value))
            return BadRequest(new { error = "value is required" });

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(req.Value))
        ).ToLowerInvariant();

        return Ok(new { hash });
    }
}

public record HashRequest(string Value);
