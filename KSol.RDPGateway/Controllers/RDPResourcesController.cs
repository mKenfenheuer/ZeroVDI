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
using KSol.RDPGateway.RDP;

namespace KSol.RDPGateway.Controllers
{
    [Authorize(Roles = "Admin")]
    public class RDPResourcesController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ProxmoxClient _proxmox;
        private readonly ProxmoxBackendProvider _backends;

        public RDPResourcesController(ApplicationDbContext context, ProxmoxClient proxmox, ProxmoxBackendProvider backends)
        {
            _context = context;
            _proxmox = proxmox;
            _backends = backends;
        }

        // GET: RDPResources
        public async Task<IActionResult> Index()
        {
            return View(await _context.RDPResources.ToListAsync());
        }

        // GET: RDPResources/Details/5
        public async Task<IActionResult> Details(string id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var rDPResource = await _context.RDPResources
                .FirstOrDefaultAsync(m => m.Id == id);
            if (rDPResource == null)
            {
                return NotFound();
            }

            return View(rDPResource);
        }

        // GET: RDPResources/Create
        public IActionResult Create()
        {
            return View(new RDPResource());
        }

        // POST: RDPResources/Create
        // Manual resources only: an admin supplies the address and options. Proxmox-backed resources
        // are created by the sync service, not here. Id is server-generated (GUID).
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("Name,Description,IpAddress,Port,RdpOptions")] RDPResource rDPResource)
        {
            if (ModelState.IsValid)
            {
                rDPResource.Source = ResourceSource.Manual;
                _context.Add(rDPResource);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            return View(rDPResource);
        }

        // GET: RDPResources/Edit/5
        public async Task<IActionResult> Edit(string id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var rDPResource = await _context.RDPResources.FindAsync(id);
            if (rDPResource == null)
            {
                return NotFound();
            }
            return View(rDPResource);
        }

        // POST: RDPResources/Edit/5
        // Load the tracked entity and apply only the editable fields. For Proxmox resources the
        // address/options are owned by the sync (read-only here), so only name/description are
        // applied; for Manual resources the address, port and RDP options are editable too.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, [Bind("Id,Name,Description,IpAddress,Port,RdpOptions")] RDPResource input)
        {
            if (id != input.Id)
            {
                return NotFound();
            }

            var existing = await _context.RDPResources.FirstOrDefaultAsync(r => r.Id == id);
            if (existing == null)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                existing.Name = input.Name;
                existing.Description = input.Description;
                if (existing.Source == ResourceSource.Manual)
                {
                    existing.IpAddress = input.IpAddress;
                    existing.Port = input.Port;
                    existing.RdpOptions = input.RdpOptions ?? existing.RdpOptions;
                }
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            return View(existing);
        }

        // GET: RDPResources/Delete/5
        public async Task<IActionResult> Delete(string id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var rDPResource = await _context.RDPResources
                .FirstOrDefaultAsync(m => m.Id == id);
            if (rDPResource == null)
            {
                return NotFound();
            }

            return View(rDPResource);
        }

        // POST: RDPResources/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            var rDPResource = await _context.RDPResources.FindAsync(id);
            if (rDPResource != null)
            {
                _context.RDPResources.Remove(rDPResource);
            }

            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        // POST: RDPResources/Exclude/{id}
        // Excludes a Proxmox VM from indexing: writes the exclude marker into the VM notes (so future
        // discovers skip it) and deletes the resource row and its authorizations.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Exclude(string id)
        {
            var res = await _context.RDPResources.FirstOrDefaultAsync(r => r.Id == id);
            if (res == null)
            {
                return NotFound();
            }

            if (res.Source != ResourceSource.Proxmox || res.ProxmoxBackendId == null
                || res.ProxmoxNode == null || res.ProxmoxVmId == null)
            {
                TempData["Status"] = "Only Proxmox resources can be excluded.";
                return RedirectToAction(nameof(Index));
            }

            var backend = await _backends.GetAsync(res.ProxmoxBackendId.Value);
            if (backend == null || !backend.IsConfigured)
            {
                TempData["Status"] = "The resource's Proxmox backend is unavailable; cannot set the exclude marker.";
                return RedirectToAction(nameof(Index));
            }

            // Stamp the exclude marker into the VM notes (preserving other content).
            var notes = await _proxmox.GetNotesAsync(backend, res.ProxmoxNode, res.ProxmoxVmId.Value);
            var stamped = ProxmoxNotes.WriteExcluded(notes);
            var ok = await _proxmox.SetNotesAsync(backend, res.ProxmoxNode, res.ProxmoxVmId.Value, stamped);
            if (!ok)
            {
                TempData["Status"] = $"Could not write the exclude marker to VM {res.ProxmoxVmId} (check token permissions). Resource not removed.";
                return RedirectToAction(nameof(Index));
            }

            // Remove the row and its authorizations.
            var auths = await _context.RDPResourceUserAuthorizations
                .Where(a => a.RDPResourceId == res.Id)
                .ToListAsync();
            _context.RDPResourceUserAuthorizations.RemoveRange(auths);
            _context.RDPResources.Remove(res);
            await _context.SaveChangesAsync();

            TempData["Status"] = $"Excluded \"{res.Name}\" from indexing.";
            return RedirectToAction(nameof(Index));
        }

        private bool RDPResourceExists(string id)
        {
            return _context.RDPResources.Any(e => e.Id == id);
        }
    }
}
