using System.Security.Claims;
using kuyumcu_domain.Entities;
using kuyumcu_domain.Enums;
using kuyumcu_infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/notes-reminders")]
[Authorize]
public sealed class NotesRemindersController : ControllerBase
{
    private readonly AppDbContext _db;

    public NotesRemindersController(AppDbContext db) => _db = db;

    public sealed record NoteDto(Guid Id, string Title, string Content, DateTime CreatedAt, DateTime UpdatedAt);
    public sealed record UpsertNoteReq(string Title, string Content);

    public sealed record ReminderDto(
        Guid Id,
        string Title,
        string Description,
        int Frequency,
        DateTime StartsAt,
        DateTime NextRunAt,
        DateTime? LastRunAt,
        bool IsActive,
        DateTime CreatedAt);
    public sealed record UpsertReminderReq(string Title, string Description, int Frequency, DateTime StartsAt, bool IsActive);
    public sealed record SnoozeReminderReq(int Minutes);

    [HttpGet("notes")]
    public async Task<IActionResult> ListNotes(CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var branchId = GetBranchId();
        var items = await _db.BranchNotes.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted)
            .OrderByDescending(x => x.UpdatedAt)
            .Select(x => new NoteDto(x.Id, x.Title, x.Content, x.CreatedAt, x.UpdatedAt))
            .ToListAsync(ct);
        return Ok(items);
    }

    [HttpPost("notes")]
    public async Task<IActionResult> CreateNote([FromBody] UpsertNoteReq req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var branchId = GetBranchId();
        var userId = GetUserId();
        var title = (req.Title ?? "").Trim();
        var content = (req.Content ?? "").Trim();
        if (string.IsNullOrWhiteSpace(title)) return BadRequest(new { error = "Not başlığı zorunludur." });
        if (string.IsNullOrWhiteSpace(content)) return BadRequest(new { error = "Not içeriği zorunludur." });

        var row = new BranchNote
        {
            TenantId = tenantId,
            BranchId = branchId,
            UserId = userId,
            Title = title,
            Content = content,
            UpdatedAt = DateTime.UtcNow
        };
        _db.BranchNotes.Add(row);
        await _db.SaveChangesAsync(ct);
        return Ok(new NoteDto(row.Id, row.Title, row.Content, row.CreatedAt, row.UpdatedAt));
    }

    [HttpPut("notes/{id:guid}")]
    public async Task<IActionResult> UpdateNote(Guid id, [FromBody] UpsertNoteReq req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var branchId = GetBranchId();
        var title = (req.Title ?? "").Trim();
        var content = (req.Content ?? "").Trim();
        if (string.IsNullOrWhiteSpace(title)) return BadRequest(new { error = "Not başlığı zorunludur." });
        if (string.IsNullOrWhiteSpace(content)) return BadRequest(new { error = "Not içeriği zorunludur." });

        var row = await _db.BranchNotes.FirstOrDefaultAsync(
            x => x.Id == id && x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct);
        if (row is null) return NotFound();

        row.Title = title;
        row.Content = content;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new NoteDto(row.Id, row.Title, row.Content, row.CreatedAt, row.UpdatedAt));
    }

    [HttpDelete("notes/{id:guid}")]
    public async Task<IActionResult> DeleteNote(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var branchId = GetBranchId();
        var row = await _db.BranchNotes.FirstOrDefaultAsync(
            x => x.Id == id && x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct);
        if (row is null) return NotFound();
        row.IsDeleted = true;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("reminders")]
    public async Task<IActionResult> ListReminders(CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var branchId = GetBranchId();
        var items = await _db.BranchReminders.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted)
            .OrderByDescending(x => x.NextRunAt)
            .Select(x => new ReminderDto(
                x.Id,
                x.Title,
                x.Description,
                (int)x.Frequency,
                x.StartsAt,
                x.NextRunAt,
                x.LastRunAt,
                x.IsActive,
                x.CreatedAt))
            .ToListAsync(ct);
        return Ok(items);
    }

    [HttpPost("reminders")]
    public async Task<IActionResult> CreateReminder([FromBody] UpsertReminderReq req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var branchId = GetBranchId();
        var userId = GetUserId();
        if (!Enum.IsDefined(typeof(ReminderFrequency), req.Frequency))
            return BadRequest(new { error = "Hatırlatma sıklığı geçersiz." });
        var title = (req.Title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(title)) return BadRequest(new { error = "Hatırlatma başlığı zorunludur." });
        var startsAt = req.StartsAt == default ? DateTime.UtcNow : req.StartsAt.ToUniversalTime();
        var frequency = (ReminderFrequency)req.Frequency;
        var row = new BranchReminder
        {
            TenantId = tenantId,
            BranchId = branchId,
            UserId = userId,
            Title = title,
            Description = (req.Description ?? "").Trim(),
            Frequency = frequency,
            StartsAt = startsAt,
            NextRunAt = CalculateNextRun(startsAt, frequency),
            IsActive = req.IsActive
        };
        _db.BranchReminders.Add(row);
        await _db.SaveChangesAsync(ct);
        return Ok(new ReminderDto(row.Id, row.Title, row.Description, (int)row.Frequency, row.StartsAt, row.NextRunAt, row.LastRunAt, row.IsActive, row.CreatedAt));
    }

    [HttpPut("reminders/{id:guid}")]
    public async Task<IActionResult> UpdateReminder(Guid id, [FromBody] UpsertReminderReq req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var branchId = GetBranchId();
        if (!Enum.IsDefined(typeof(ReminderFrequency), req.Frequency))
            return BadRequest(new { error = "Hatırlatma sıklığı geçersiz." });
        var title = (req.Title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(title)) return BadRequest(new { error = "Hatırlatma başlığı zorunludur." });

        var row = await _db.BranchReminders.FirstOrDefaultAsync(
            x => x.Id == id && x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct);
        if (row is null) return NotFound();

        var startsAt = req.StartsAt == default ? row.StartsAt : req.StartsAt.ToUniversalTime();
        var frequency = (ReminderFrequency)req.Frequency;
        row.Title = title;
        row.Description = (req.Description ?? "").Trim();
        row.Frequency = frequency;
        row.StartsAt = startsAt;
        row.NextRunAt = CalculateNextRun(startsAt, frequency);
        row.IsActive = req.IsActive;
        await _db.SaveChangesAsync(ct);

        return Ok(new ReminderDto(row.Id, row.Title, row.Description, (int)row.Frequency, row.StartsAt, row.NextRunAt, row.LastRunAt, row.IsActive, row.CreatedAt));
    }

    [HttpDelete("reminders/{id:guid}")]
    public async Task<IActionResult> DeleteReminder(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var branchId = GetBranchId();
        var row = await _db.BranchReminders.FirstOrDefaultAsync(
            x => x.Id == id && x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct);
        if (row is null) return NotFound();
        row.IsDeleted = true;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("reminders/{id:guid}/ack")]
    public async Task<IActionResult> AcknowledgeReminder(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var branchId = GetBranchId();
        var row = await _db.BranchReminders.FirstOrDefaultAsync(
            x => x.Id == id && x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct);
        if (row is null) return NotFound();

        var now = DateTime.UtcNow;
        row.LastRunAt = now;
        if (row.Frequency == ReminderFrequency.Once)
        {
            row.IsActive = false;
            row.IsDeleted = true;
            row.NextRunAt = now;
            await _db.SaveChangesAsync(ct);
            return Ok(new ReminderDto(row.Id, row.Title, row.Description, (int)row.Frequency, row.StartsAt, row.NextRunAt, row.LastRunAt, row.IsActive, row.CreatedAt));
        }

        var next = row.NextRunAt;
        if (next < row.StartsAt) next = row.StartsAt;
        if (next < now) next = now;
        do
        {
            next = AddFrequency(next, row.Frequency);
        } while (next <= now);
        row.NextRunAt = next;

        await _db.SaveChangesAsync(ct);
        return Ok(new ReminderDto(row.Id, row.Title, row.Description, (int)row.Frequency, row.StartsAt, row.NextRunAt, row.LastRunAt, row.IsActive, row.CreatedAt));
    }

    [HttpPost("reminders/{id:guid}/snooze")]
    public async Task<IActionResult> SnoozeReminder(Guid id, [FromBody] SnoozeReminderReq req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var branchId = GetBranchId();
        var row = await _db.BranchReminders.FirstOrDefaultAsync(
            x => x.Id == id && x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct);
        if (row is null) return NotFound();

        var minutes = req.Minutes <= 0 ? 15 : Math.Min(req.Minutes, 60 * 24 * 7);
        var now = DateTime.UtcNow;
        row.NextRunAt = now.AddMinutes(minutes);
        row.IsActive = true;
        await _db.SaveChangesAsync(ct);
        return Ok(new ReminderDto(row.Id, row.Title, row.Description, (int)row.Frequency, row.StartsAt, row.NextRunAt, row.LastRunAt, row.IsActive, row.CreatedAt));
    }

    private static DateTime CalculateNextRun(DateTime startsAt, ReminderFrequency frequency)
    {
        var next = startsAt;
        var now = DateTime.UtcNow;
        while (next < now)
        {
            next = frequency switch
            {
                ReminderFrequency.Hourly => next.AddHours(1),
                ReminderFrequency.Daily => next.AddDays(1),
                ReminderFrequency.Weekly => next.AddDays(7),
                ReminderFrequency.Monthly => next.AddMonths(1),
                ReminderFrequency.Yearly => next.AddYears(1),
                ReminderFrequency.Once => next,
                _ => next.AddDays(1)
            };
            if (frequency == ReminderFrequency.Once) break;
        }
        return next;
    }

    private static DateTime AddFrequency(DateTime at, ReminderFrequency frequency)
    {
        return frequency switch
        {
            ReminderFrequency.Hourly => at.AddHours(1),
            ReminderFrequency.Daily => at.AddDays(1),
            ReminderFrequency.Weekly => at.AddDays(7),
            ReminderFrequency.Monthly => at.AddMonths(1),
            ReminderFrequency.Yearly => at.AddYears(1),
            ReminderFrequency.Once => at,
            _ => at.AddDays(1)
        };
    }

    private Guid GetTenantId()
    {
        var claim = User?.Claims?.FirstOrDefault(c =>
            c.Type.Equals("tenant_id", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.IsNullOrWhiteSpace(claim) && Guid.TryParse(claim, out var fromJwt))
            return fromJwt;
        if (Request.Headers.TryGetValue("X-Tenant-Id", out var hdr) &&
            Guid.TryParse(hdr.ToString(), out var fromHdr))
            return fromHdr;
        throw new InvalidOperationException("TenantId missing (JWT veya X-Tenant-Id).");
    }

    private Guid GetBranchId()
    {
        var claim = User?.Claims?.FirstOrDefault(c =>
            c.Type.Equals("branch_id", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.IsNullOrWhiteSpace(claim) && Guid.TryParse(claim, out var fromJwt))
            return fromJwt;
        if (Request.Headers.TryGetValue("X-Branch-Id", out var hdr) &&
            Guid.TryParse(hdr.ToString(), out var fromHdr))
            return fromHdr;
        throw new InvalidOperationException("BranchId missing (JWT veya X-Branch-Id).");
    }

    private Guid GetUserId()
    {
        var claim = User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrWhiteSpace(claim) && Guid.TryParse(claim, out var userId))
            return userId;
        throw new InvalidOperationException("UserId missing.");
    }
}
