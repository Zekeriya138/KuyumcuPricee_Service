// Kuyumcu.PriceService/Models/BranchDtos.cs
public sealed record CreateBranchDto(string Name, string? City, string? Address, string? Phone);
public sealed record UpdateBranchDto(string Name, string? City, string? Address, string? Phone);
public sealed record BranchDto(Guid Id, string Name, string? City, string? Address, string? Phone, DateTime CreatedAt);
