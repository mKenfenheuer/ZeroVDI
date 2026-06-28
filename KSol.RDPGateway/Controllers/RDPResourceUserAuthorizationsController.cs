using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;

namespace KSol.RDPGateway.Controllers
{
    [Authorize(Roles = "Admin")]
    [Route("admin/authorizations")]
    public class RDPResourceUserAuthorizationsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly RDP.CredentialProtector _credentials;

        public RDPResourceUserAuthorizationsController(ApplicationDbContext context, RDP.CredentialProtector credentials)
        {
            _context = context;
            _credentials = credentials;
        }

        // GET: /admin/authorizations
        [HttpGet("")]
        public async Task<IActionResult> Index()
        {
            var applicationDbContext = _context.RDPResourceUserAuthorizations.Include(r => r.RDPResource).Include(r => r.User);
            return View(await applicationDbContext.ToListAsync());
        }

        // GET: /admin/authorizations/details/5
        [HttpGet("details/{id}")]
        public async Task<IActionResult> Details(string id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var rDPResourceUserAuthorization = await _context.RDPResourceUserAuthorizations
                .Include(r => r.RDPResource)
                .Include(r => r.User)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (rDPResourceUserAuthorization == null)
            {
                return NotFound();
            }

            return View(rDPResourceUserAuthorization);
        }

        // GET: /admin/authorizations/create
        [HttpGet("create")]
        public IActionResult Create()
        {
            ViewData["RDPResourceId"] = new SelectList(_context.RDPResources, "Id", "Name");
            ViewData["UserId"] = new SelectList(_context.Users, "Id", "UserName");
            return View();
        }

        // POST: /admin/authorizations/create
        // To protect from overposting attacks, enable the specific properties you want to bind to.
        // For more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost("create")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("UserId,RDPResourceId")] RDPResourceUserAuthorization rDPResourceUserAuthorization)
        {
            if (ModelState.IsValid)
            {
                _context.Add(rDPResourceUserAuthorization);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            ViewData["RDPResourceId"] = new SelectList(_context.RDPResources, "Id", "Name", rDPResourceUserAuthorization.RDPResourceId);
            ViewData["UserId"] = new SelectList(_context.Users, "Id", "UserName", rDPResourceUserAuthorization.UserId);
            return View(rDPResourceUserAuthorization);
        }

        // GET: /admin/authorizations/edit/5
        [HttpGet("edit/{id}")]
        public async Task<IActionResult> Edit(string id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var rDPResourceUserAuthorization = await _context.RDPResourceUserAuthorizations.FindAsync(id);
            if (rDPResourceUserAuthorization == null)
            {
                return NotFound();
            }
            ViewData["RDPResourceId"] = new SelectList(_context.RDPResources, "Id", "Name", rDPResourceUserAuthorization.RDPResourceId);
            ViewData["UserId"] = new SelectList(_context.Users, "Id", "UserName", rDPResourceUserAuthorization.UserId);
            return View(rDPResourceUserAuthorization);
        }

        // POST: RDPResourceUserAuthorizations/Edit/5
        // Binds only the user/resource link and the console ConnectionDefaults. The encrypted
        // credential envelopes (Protected*) are NEVER bound here — they are managed separately by
        // SetCredentials/ClearCredentials so a form submit can't clear or overpost them. We load the
        // tracked entity and mutate the allowed fields rather than Update() a fresh graph (which would
        // null the stored creds).
        [HttpPost("edit/{id}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, [Bind("Id,UserId,RDPResourceId,ConnectionDefaults")] RDPResourceUserAuthorization rDPResourceUserAuthorization)
        {
            if (id != rDPResourceUserAuthorization.Id)
            {
                return NotFound();
            }

            var existing = await _context.RDPResourceUserAuthorizations.FindAsync(id);
            if (existing == null)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                existing.UserId = rDPResourceUserAuthorization.UserId;
                existing.RDPResourceId = rDPResourceUserAuthorization.RDPResourceId;
                existing.ConnectionDefaults = rDPResourceUserAuthorization.ConnectionDefaults;
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            ViewData["RDPResourceId"] = new SelectList(_context.RDPResources, "Id", "Name", rDPResourceUserAuthorization.RDPResourceId);
            ViewData["UserId"] = new SelectList(_context.Users, "Id", "UserName", rDPResourceUserAuthorization.UserId);
            return View(rDPResourceUserAuthorization);
        }

        // POST: RDPResourceUserAuthorizations/SetCredentials/5
        // Stores (encrypted) VM credentials for one (user, resource) authorization, enabling SSO
        // auto-connect from the in-browser console. The plaintext password is never persisted or
        // echoed back — only the DataProtection envelopes are stored.
        [HttpPost("setcredentials/{id}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetCredentials(string id, string username, string password, string? domain)
        {
            var auth = await _context.RDPResourceUserAuthorizations.FindAsync(id);
            if (auth == null) return NotFound();

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                TempData["CredError"] = "Username and password are both required to store credentials.";
                return RedirectToAction(nameof(Edit), new { id });
            }

            auth.ProtectedUsername = _credentials.Protect(username);
            auth.ProtectedPassword = _credentials.Protect(password);
            auth.ProtectedDomain = _credentials.Protect(domain);
            await _context.SaveChangesAsync();
            TempData["CredStatus"] = "Stored VM credentials updated.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        // POST: RDPResourceUserAuthorizations/ClearCredentials/5
        [HttpPost("clearcredentials/{id}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ClearCredentials(string id)
        {
            var auth = await _context.RDPResourceUserAuthorizations.FindAsync(id);
            if (auth == null) return NotFound();

            auth.ProtectedUsername = null;
            auth.ProtectedPassword = null;
            auth.ProtectedDomain = null;
            await _context.SaveChangesAsync();
            TempData["CredStatus"] = "Stored VM credentials cleared.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        // GET: /admin/authorizations/delete/5
        [HttpGet("delete/{id}")]
        public async Task<IActionResult> Delete(string id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var rDPResourceUserAuthorization = await _context.RDPResourceUserAuthorizations
                .Include(r => r.RDPResource)
                .Include(r => r.User)
                .FirstOrDefaultAsync(m => m.Id == id);
            if (rDPResourceUserAuthorization == null)
            {
                return NotFound();
            }

            return View(rDPResourceUserAuthorization);
        }

        // POST: /admin/authorizations/delete/5
        [HttpPost("delete/{id}"), ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            var rDPResourceUserAuthorization = await _context.RDPResourceUserAuthorizations.FindAsync(id);
            if (rDPResourceUserAuthorization != null)
            {
                _context.RDPResourceUserAuthorizations.Remove(rDPResourceUserAuthorization);
            }

            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        private bool RDPResourceUserAuthorizationExists(string id)
        {
            return _context.RDPResourceUserAuthorizations.Any(e => e.Id == id);
        }
    }
}
