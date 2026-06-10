namespace DocumentQA.Application.Models;

public sealed record TenantContext(string TenantId, TierInfo Tier, string ApiKey);
