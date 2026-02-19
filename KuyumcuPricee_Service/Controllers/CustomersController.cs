using kuyumcu_application.Abstractions;
using kuyumcu_domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize] // JWT zorunlu
public class CustomersController : ControllerBase
{
    private readonly ICustomerService _svc;
    public CustomersController(ICustomerService svc) => _svc = svc;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? q, CancellationToken ct)
        => Ok(await _svc.ListAsync(q, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get([FromRoute] Guid id, CancellationToken ct)
        => (await _svc.GetAsync(id, ct)) is { } c ? Ok(c) : NotFound();

    public record CreateCustomerDto(
        string FullName,
        string? NationalId,
        DateTime? BirthDate,
        string? Phone,
        string? Email,
        string? City,
        string? District,
        string? Address
    );

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCustomerDto dto, CancellationToken ct)
    {
        var ent = new Customer
        {
            // TenantId servis içinde atanacak
            FullName = dto.FullName,
            NationalId = dto.NationalId,
            BirthDate = dto.BirthDate,
            Phone = dto.Phone,
            Email = dto.Email,
            City = dto.City,
            District = dto.District,
            Address = dto.Address
        };

        var created = await _svc.CreateAsync(ent, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] CreateCustomerDto dto, CancellationToken ct)
    {
        var inp = new Customer
        {
            FullName = dto.FullName,
            NationalId = dto.NationalId,
            BirthDate = dto.BirthDate,
            Phone = dto.Phone,
            Email = dto.Email,
            City = dto.City,
            District = dto.District,
            Address = dto.Address
        };
        return await _svc.UpdateAsync(id, inp, ct) ? NoContent() : NotFound();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete([FromRoute] Guid id, CancellationToken ct)
        => await _svc.DeleteAsync(id, ct) ? NoContent() : NotFound();
}
