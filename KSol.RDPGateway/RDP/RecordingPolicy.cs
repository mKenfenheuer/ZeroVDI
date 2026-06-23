using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;
using Microsoft.EntityFrameworkCore;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Decides whether a console session should be recorded, by evaluating the ordered
/// <see cref="RecordingRule"/> set. Enabled rules are checked in ascending <see cref="RecordingRule.Order"/>;
/// the first whose scope matches (user / resource / role / global) wins with its Allow/Deny action. If
/// no rule matches, the configured global default (<c>Recording:DefaultEnabled</c>, default false) applies.
/// </summary>
public sealed class RecordingPolicy
{
    private readonly ApplicationDbContext _db;
    private readonly bool _defaultEnabled;

    public RecordingPolicy(ApplicationDbContext db, IConfiguration config)
    {
        _db = db;
        _defaultEnabled = config.GetValue("Recording:DefaultEnabled", false);
    }

    public async Task<bool> ShouldRecordAsync(string userId, string resourceId, IEnumerable<string> roles)
    {
        var roleSet = roles as ICollection<string> ?? roles.ToList();
        var rules = await _db.RecordingRules
            .Where(r => r.Enabled)
            .OrderBy(r => r.Order)
            .ToListAsync();

        foreach (var rule in rules)
        {
            bool match = rule.Scope switch
            {
                RecordingRuleScope.Global => true,
                RecordingRuleScope.User => rule.UserId == userId,
                RecordingRuleScope.Resource => rule.RDPResourceId == resourceId,
                RecordingRuleScope.Role => rule.RoleName != null && roleSet.Contains(rule.RoleName),
                _ => false,
            };
            if (match)
                return rule.Action == RecordingRuleAction.Allow;
        }

        return _defaultEnabled;
    }
}
