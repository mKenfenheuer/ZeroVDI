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
    public class RDPResourceUserAuthorizationsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public RDPResourceUserAuthorizationsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET: RDPResourceUserAuthorizations
        public async Task<IActionResult> Index()
        {
            var applicationDbContext = _context.RDPResourceUserAuthorizations.Include(r => r.RDPResource).Include(r => r.User);
            return View(await applicationDbContext.ToListAsync());
        }

        // GET: RDPResourceUserAuthorizations/Details/5
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

        // GET: RDPResourceUserAuthorizations/Create
        public IActionResult Create()
        {
            ViewData["RDPResourceId"] = new SelectList(_context.RDPResources, "Id", "Name");
            ViewData["UserId"] = new SelectList(_context.Users, "Id", "UserName");
            return View();
        }

        // POST: RDPResourceUserAuthorizations/Create
        // To protect from overposting attacks, enable the specific properties you want to bind to.
        // For more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
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

        // GET: RDPResourceUserAuthorizations/Edit/5
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
        // To protect from overposting attacks, enable the specific properties you want to bind to.
        // For more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, [Bind("Id,UserId,RDPResourceId")] RDPResourceUserAuthorization rDPResourceUserAuthorization)
        {
            if (id != rDPResourceUserAuthorization.Id)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(rDPResourceUserAuthorization);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!RDPResourceUserAuthorizationExists(rDPResourceUserAuthorization.Id))
                    {
                        return NotFound();
                    }
                    else
                    {
                        throw;
                    }
                }
                return RedirectToAction(nameof(Index));
            }
            ViewData["RDPResourceId"] = new SelectList(_context.RDPResources, "Id", "Name", rDPResourceUserAuthorization.RDPResourceId);
            ViewData["UserId"] = new SelectList(_context.Users, "Id", "UserName", rDPResourceUserAuthorization.UserId);
            return View(rDPResourceUserAuthorization);
        }

        // GET: RDPResourceUserAuthorizations/Delete/5
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

        // POST: RDPResourceUserAuthorizations/Delete/5
        [HttpPost, ActionName("Delete")]
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
