using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KSol.ZeroVDI.Data;

namespace KSol.ZeroVDI.Controllers
{
    /// <summary>
    /// Read-only "all access grants" overview. Granting, editing credentials/defaults and revoking now
    /// live on the two object pages (the user page's Resource-access tab and the resource editor's
    /// Users-&amp;-access tab); this controller no longer exposes Create/Edit/Delete — those entry points
    /// were removed to keep a single, unambiguous place to manage each grant.
    /// </summary>
    [Authorize(Roles = "Admin")]
    [Route("admin/authorizations")]
    public class RDPResourceUserAuthorizationsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public RDPResourceUserAuthorizationsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET: /admin/authorizations — read-only list of all direct (user, resource) grants.
        [HttpGet("")]
        public async Task<IActionResult> Index()
        {
            var grants = await _context.RDPResourceUserAuthorizations
                .Include(r => r.RDPResource)
                .Include(r => r.User)
                .OrderBy(r => r.User!.UserName)
                .ToListAsync();
            return View(grants);
        }
    }
}
