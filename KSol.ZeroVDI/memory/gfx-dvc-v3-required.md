---
name: gfx-dvc-v3-required
description: This RDP host requires DVC version 3 for GFX; nested-ZGFX architecture and the caps-compression gotcha
metadata:
  type: project
---

The test host (see [[rdp-test-host]], 10.1.250.112) **requires DVC (MS-RDPEDYC) version 3** to stream the GFX channel. Negotiation outcomes observed in `wwwroot/lib/rdpweb/protocol.js` `_dvcOnCapabilities`:

- Echoing the server's advertised version (it sends v3) → host streamed a few uncompressed GFX frames then froze (it switches to compressed DVC PDUs mid-stream which we couldn't parse).
- Responding **v1** → host created the Graphics DVC then sent NOTHING (it only streams GFX over compressed DVC PDUs).
- Responding **v3** (correct) → required. `DVC_CLIENT_MAX_VERSION = 3`.

**v3 obligations we implement:** inbound compressed DVC data — `DATA_FIRST_COMPRESSED` (0x06) and `DATA_COMPRESSED` (0x07), [MS-RDPEDYC] 2.2.3.3/2.2.3.4. Body is an `RDP_SEGMENTED_DATA` (ZGFX) blob compressed with **RDP8_LITE** (8 KB history, type code 0x06). `_dvcOnData(..., compressed=true)` inflates each chunk through a **per-channel, session-persistent `ZgfxDecode`** (`this._dvcZgfx[channelId]`) BEFORE reassembly, because the DATA_FIRST `Length` is the total UNCOMPRESSED size. Reused `ZgfxDecode` as-is: it only tests the PACKET_COMPRESSED bit (0x20) and ignores the type nibble, and LITE's 8 KB match distance is a subset of the 2.5 MB ring.

**Soft-Sync (0x08/0x09) is NOT a concern:** per [MS-RDPEDYC] 3.1.5.3 it requires BOTH peers to set SOFTSYNC_TCP_TO_UDP in Multitransport Channel Data; we never advertise multitransport, so the host can't initiate it.

**Nested ZGFX (two independent contexts):** on the Graphics channel the DVC v3 layer inflates RDP8_LITE first (`this._dvcZgfx[gfxId]`), then `gfx.onChannelData` runs the GFX channel's OWN ZGFX (`RdpGfx.zgfx`) on the result. Don't conflate them.

**Caps-compression gotcha:** CAPS_ADVERTISE must be sent **uncompressed** under v3. Previously it was MPPC-compressed at the STATIC-channel layer (`_sendDvcData(..., compress=true)`, CHANNEL flags 0x600003, to mirror the macOS app). That was fine under a v1 cap, but once v3 is negotiated the host stopped responding to static-layer-compressed caps — accepted the Graphics channel then went silent with NO CAPS_CONFIRM. If a 1:1 macOS-app wire match is ever needed again, do it via DVC-layer compression, not static MPPC.

Related: [[gfx-h264-rendering]], [[browser-rdp-feature]]. Separate open issue: decoded H.264 frames paint all-black (WebCodecs presentation path), independent of this DVC/stall work.
