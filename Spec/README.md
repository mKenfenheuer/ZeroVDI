# Protocol specifications

ZeroVDI implements the Remote Desktop Protocol directly — there is no FreeRDP or mstsc in the path.
The code cites the Microsoft Open Specifications by section throughout, for example:

```csharp
// Fast-path keyboard event ([MS-RDPBCGR] 2.2.8.1.2.2.1)
```

The documents themselves are **not** vendored here: they are Microsoft's, published and maintained by
Microsoft, and a stale copy in a third-party repository serves nobody. Read them at the source:

| Document | What it covers | Where |
|---|---|---|
| **[MS-RDPBCGR]** | Basic connectivity and graphics remoting — the connection sequence, fast-path input and output, virtual channels, server redirection | https://learn.microsoft.com/openspecs/windows_protocols/ms-rdpbcgr/ |
| **[MS-RDPEGFX]** | Graphics pipeline extension — surfaces, frames, ZGFX, the H.264 (AVC420/444) and progressive codecs | https://learn.microsoft.com/openspecs/windows_protocols/ms-rdpegfx/ |
| **[MS-RDPRFX]** | RemoteFX codec, including the progressive variant GNOME Remote Desktop streams | https://learn.microsoft.com/openspecs/windows_protocols/ms-rdprfx/ |
| **[MS-RDPEDYC]** | Dynamic virtual channels (`drdynvc`) | https://learn.microsoft.com/openspecs/windows_protocols/ms-rdpedyc/ |
| **[MS-RDPECLIP]** | Clipboard redirection | https://learn.microsoft.com/openspecs/windows_protocols/ms-rdpeclip/ |
| **[MS-RDPEVOR]** | Video optimised remoting | https://learn.microsoft.com/openspecs/windows_protocols/ms-rdpevor/ |
| **[MS-RDPEA]** | Audio output redirection (`rdpsnd`) | https://learn.microsoft.com/openspecs/windows_protocols/ms-rdpea/ |
| **[MS-RDPEAI]** | Audio input redirection | https://learn.microsoft.com/openspecs/windows_protocols/ms-rdpeai/ |
| **[MS-RDPECAM]** | Camera redirection | https://learn.microsoft.com/openspecs/windows_protocols/ms-rdpecam/ |
| **[MS-CSSP]** | CredSSP — the Network Level Authentication handshake | https://learn.microsoft.com/openspecs/windows_protocols/ms-cssp/ |
| **[MS-NLMP]** | NTLM authentication, used inside CredSSP | https://learn.microsoft.com/openspecs/windows_protocols/ms-nlmp/ |

If you are working on the protocol code, download the PDFs or keep the web pages open next to the
source — most of the non-obvious code in `KSol.ZeroVDI/RDP/` and
`KSol.ZeroVDI/wwwroot/lib/rdpweb/` carries the section number that explains it.
