---
name: gfx-stall-mitm-findings
description: GFX stall ROOT CAUSE = client never sent its initial DISPLAYCONTROL_MONITOR_LAYOUT (no-op guard suppressed it); host waits on it before the 2nd RESET_GRAPHICS that unlocks free-run streaming. Fixed. Also rdpmitm decoder fixes (MPPC+ZGFX), --replay, gateway live recorder.
metadata:
  type: project
---

## ★ ROOT CAUSE (2026-06-23, v2): client sends an unexpected DVC CLOSE on Geometry channels
Decisive evidence from true-order replay of our session: the host's s2c stops MID-MESSAGE — last GFX is a DataFirst declaring totalLen=22405 but only 11184 bytes arrive (11221 short). The host sends one big partial chunk then three tiny control PDUs (Create Geometry id=18, Create Geometry id=18, Close Geometry id=18) and goes dormant (last s2c t=828ms; session then idle 8s while we send only mouse moves).
Diff vs mstsc on the Geometry (MS-RDPEGT) channels: **mstsc NEVER closes Geometry — only the SERVER sends DVC CLOSE (all S2C Close), on its own schedule. Our client sent a CLIENT CLOSE (C2S Close) right after accepting each Geometry channel** ("accept-then-close"). The host then re-opened Geometry, we closed it again — a Create/Close war on the drdynvc static channel that coincides exactly with the GFX stall. The old code comment claiming "macOS app sends CLOSE" was WRONG (verified false against the fresh capture).
FIX: protocol.js `_dvcOnCreate` isGeometry branch — ACCEPT and KEEP OPEN (don't send a client CLOSE; the CREATE_RSP status 0 + dvcById registration already happened above). Let the host close it. Geometry DATA we receive falls to the unhandled-channel log (harmless; mstsc doesn't respond to it either).

### Superseded lead (kept for context): missing initial MONITOR_LAYOUT
Earlier I thought the gate was the missing first DISPLAYCONTROL_MONITOR_LAYOUT. Added a `_monitorLayoutSent` fix so the no-op guard never suppresses the first layout — that fix is CORRECT and kept (our client now sends `RDPEDISP: monitor layout 2560x1606 @ 100%`), but it did NOT lift the stall (still stopped at frame 3). And mstsc's c2s rdpedisp MONITOR_LAYOUT actually comes at t=28 AFTER its 2nd RESET (line 339), so it's not the 2nd-reset trigger. So MONITOR_LAYOUT was a real gap but not the gate.
Codec note: host streams AVC444v2 keyframes then codecId=0x0000 UNCOMPRESSED wire-to-surface deltas (small rects) → 3.6MB. Our decode of frames 1-3 = all-black (nonBlack=0) — genuine pre-reconfigure surface; real content arrives Gen-2 after the host's 2nd RESET_GRAPHICS, which only happens once the Geometry CLOSE war is gone.

## TRUE-ORDER DIFF: extra camera/audio DVCs (2026-06-23, strongest lead)
With the gateway's live RdpRecorder writing meta.txt, replayed BOTH our session and a fresh mstsc MITM capture in true wire order and diffed the c2s setup. Results:
- **ConfirmActive capsets: byte-identical** (22 sets, same order/lengths) — caps cleared AGAIN.
- **GFX acks: 3 FRAME_ACK + 3 QOE (frames 1-3), well-formed, match mstsc.**
- **0 round-trip mismatches** in our entire session — every PDU we send is byte-valid.
- **THE difference**: our client advertises camera+mic, so the host opens DVCs **mstsc never gets**: `AUDIO_PLAYBACK_LOSSY_DVC`, `RDCamera_Device_Enumerator`, and `RDPGWCam0` (camera device, on TWO channels id12+id17). The host runs a full MS-RDPECAM probe (ActivateDevice→StreamList→MediaTypeList→CurrentMediaType→PropertyList→**Deactivate**→CLOSE both cam channels). Our rdpecam.js responses are CORRECT (we ack/answer each per spec), but the host probes then **closes** the camera, and GFX stalls shortly after (frame3 + start of frame4's DataFirst, then silence). mstsc's session has NONE of this camera/lossy traffic and streams 3.6MB.
- Camera msg map (wire `02 XX`, version2): 07=ActivateDeviceReq,01=Success,09=StreamListReq,0a=StreamListRsp,0b=MediaTypeListReq,0c=MediaTypeListRsp,0d=CurrentMediaTypeReq,0e=CurrentMediaTypeRsp,14=PropertyListReq,15=PropertyListRsp,08=DeactivateDeviceReq.
- **NEXT (the A/B test):** capture with camera+mic+lossy DISABLED (`setCameraEnabled(false)`, `setMicrophoneEnabled(false)`) — if GFX then flows past frame 3, the camera/audio advertisement is the gate; if not, that's cleared too. Toggles in client.js (cameraEnabled/microphoneEnabled → opts.camera/opts.microphone in protocol.js ~line 2015-2021).

## Gateway live recorder + shared RdpWire lib (2026-06-23)
Repo restructured: `ksol-rdpgw/` now holds `KSol.RDPGateway/` (gateway), `RDP.MITM/` (proxy), and `RdpWire/` (shared decode/encode/dump engine = `KSol.RDP.Wire`, namespace KSol.RDPGateway.RDP). Both gateway and MITM ProjectReference RdpWire. The gateway's `RdpRelaySession` has an `RdpRecorder` (gated on env `RDPGW_DUMP_DIR`) feeding both pump directions through the shared `RdpSession`, writing pdus.log/pdus.jsonl + meta.txt (per-chunk dir+ts+len for true-order replay). `rdpmitm --replay <dir>` decodes either MITM dumps or the gateway's our_c2s/our_s2c (prefers meta.txt for ordering). Recorder is observational only — relay forwards original bytes, never the re-encoded ones.

## OUR-CLIENT CAPTURE (decisive, 2026-06-23)
Captured our OWN client's decrypted stream via the gateway's `RDPGW_DUMP_DIR` tee (RdpRelaySession → `/tmp/rdpgw-dump/our_c2s.bin`+`our_s2c.bin`), then decoded it with the now-fixed rdpmitm decoder (`dotnet run -- --replay /tmp/rdpgw-dump`). Findings:
- Our client receives s2c GFX **only 48 KB** (vs mstsc 2.7 MB): RESET_GRAPHICS+CREATE_SURFACE+MAP_SURFACE_TO_OUTPUT (frame1), WIRE_TO_SURFACE_1 12669B AVC444v2 (frame2 keyframe), WIRE_TO_SURFACE_1 109B (frame3 delta), then the host sends the START of frame4's DataFirst and **stops**. Exactly the stall boundary, byte-confirmed.
- Our client DID send 3 FRAME_ACKNOWLEDGE + 3 QOE_FRAME_ACKNOWLEDGE — frameId=1,2,3, queueDepth=0, totalDecoded=1/2/3, QOE ts≈wall-clock, diffSE small. **Structurally identical to mstsc's acks.** So the GFX-ack path is CORRECT and is NOT the stall cause (overturns the "queueDepth/QOE timestamp" lead).
- Our DVC handshake completes: ~11 CREATE_RSP OK + ~9 FAIL (accept/reject policy), matching intent.
- mstsc gets the SAME frames 1-3 and acks them the SAME way, yet the host continues to mstsc (frame 40+) and stops for us. **The differentiator is therefore NOT in the GFX channel (caps, acks, codec all match) — it is elsewhere in our session setup** (candidates not yet excluded: the exact DVC accept/reject SET vs mstsc, ClientInfo/CS_CORE fields, or a connection-sequence PDU the host gates sustained streaming on).
- NEXT: capture our client WITH meta-style ordering (or add timestamps to the gateway tee) and byte-diff our_c2s vs mstsc c2s across the whole setup window, not just GFX.

## rdpmitm decoder fixes (2026-06-23) — now decodes the full GFX path
The MITM (`/Users/max/Git/tools/rdpmitm`) GFX decode was failing (`bad pduLength`) because it skipped two decompression layers. Fixed:
- **SVC-layer MPPC**: `RdpSession.ReassembleSvc` now MPPC-inflates chunks with CHANNEL_PACKET_COMPRESSED (0x00200000); type from the 0x000F0000 mask (0=8K,1=64K). Added `Mppc.cs` (decompress-only port of mppc.js). drdynvc routed through ReassembleSvc too (its CAPS_ADVERTISE is MPPC'd). This DECODED mstsc's GFX CAPS_ADVERTISE: 9 capsets, **byte-identical to ours** (V8/V8.1/V10/V10.2/V10.3/V10.4/V10.7/V11.1/V11.3 with flags 0x02/0x02/0x22/0x22/0x20/0x02/0x82/0x82/0x82) — so caps are CONFIRMED not the gate.
- **GFX-layer ZGFX**: `DecodeGfx` inflates payloads starting 0xE0/0xE1 via a per-channel Zgfx ctx; plaintext acks (valid 8-byte RDPGFX header) pass through; raw non-descriptor bytes are labeled, not fed to ZGFX (avoids history desync).
- Added `--replay [dir]` offline mode: re-decode c2s.bin/s2c.bin (+meta.txt for true order) OR the gateway's our_c2s/our_s2c (no meta → feed c2s then s2c) into pdus.log/jsonl.
- Spec confirmation (MS-RDPEGFX 3.1.9.1.2 + FreeRDP `dvcman_receive_channel_data` / `zgfx_decompress`): our client's GFX framing model is CORRECT — one zgfx_decompress per reassembled DVC message, shared ctx, walk concatenated PDUs. A bare DVC DATA with no pending DATA_FIRST is a complete message. The "0x5d75d7 raw chunks" were a bug in a throwaway python script (misread DATA_FIRST 0x24 as DATA 0x34), NOT real.
- Codec in use: **codecId=0x000F AVC444v2** (confirmed in both mstsc and our stream).

## ROOT CAUSE (fixed)
The browser GFX stall (host streams ~3 frames then stops) was caused by **off-by-one capability-set TYPE numbers** in the client's Confirm Active PDU (`confirmActivePdu` in `protocol.js`). Per [MS-RDPBCGR]: LARGE_POINTER=0x1B, SURFACE_COMMANDS=0x1C, BITMAP_CODECS=0x1D, FRAME_ACKNOWLEDGE=0x1E ([MS-RDPRFX] 2.2.1.3). Our code sent them as 0x1C / 0x1D / 0x1E and **omitted FRAME_ACKNOWLEDGE entirely**. So the host read our LargePointer body as SurfaceCommands, SurfaceCommands as BitmapCodecs, BitmapCodecs as FrameAcknowledge — it never saw a valid SURFACE_COMMANDS cap (the GFX surface-pipeline gate) and mis-negotiated frame-ack, so it stopped streaming after the initial frames. Fix: correct the four types and add `capFrameAcknowledge()`; bodies are byte-for-byte what mstsc sends (LP `03 00`; SC `12 00 00 00 00 00 00 00`; BC the 23-byte GUID blob; FA `02 00 00 00`). Found by diffing mstsc's Confirm Active capsets (22 sets) against ours (15) via the MITM dump.

## Diagnostic trail (eliminations, kept for context)

The browser GFX H.264 stream stalls after ~3 logical frames (RESET + keyframe + 1 delta) against the test host ([[rdp-test-host]] 10.1.250.112). A MITM capture of a working mstsc/macOS-RDP session against the SAME host (tool at `/Users/max/Git/tools/rdpmitm`, dumps `/tmp/rdpmitm/{c2s,s2c}.bin` + `meta.txt`) streamed **3.3 MB s2c** vs our **~13 KB**. Comparing the two **eliminated** these as the cause:

- **DVC channel accept/reject is IDENTICAL to mstsc.** mstsc rejects CoreInput(id2), MouseCursor(id8), Video::Control(id10), Video::Data(id11); accepts Graphics(id7), Geometry(id12), audio. Exactly our decisions. So MS-RDPEVOR and MS-RDPEGT channel handling are NOT the gate (also confirmed by spec: EVOR is server-initiated and the host sent zero bytes on it; keeping Geometry open changed nothing).
- **GFX acks are IDENTICAL.** mstsc sends FRAME_ACKNOWLEDGE (0x0d, 20B) + QOE_FRAME_ACKNOWLEDGE (0x16, 20B) per frame — same PDUs/sizes as us.
- **The host needs NO client keep-alive.** During mstsc's keyframe burst and the next burst, mstsc's c2s stayed frozen (sent nothing) while the host kept streaming. So there is no magic post-keyframe PDU we're missing.
- **Decoder is NOT involved.** window.RDP_GFX_NODECODE=1 (drop+ack frames, no WebCodecs) still stalls at the same point.
- **Input is well-formed and ignored.** Keyboard (scancodes) + mouse moves all correctly framed per [MS-RDPBCGR] 2.2.8.1.2.2 and on the wire; host produces zero GFX in response.
- **Acks aren't malformed.** Per [MS-RDPEGFX] 3.2.5.13 the server only uses frameId + queueDepth (we send 0 = keep streaming); totalFramesDecoded is not used for flow control.

**Remaining suspect = the INITIAL capability negotiation.** Since our post-keyframe GFX c2s matches mstsc but the host throttles only us, the host likely classifies our client as not-sustained-video-capable from the MCS Connect Initial (CS_CORE earlyCapabilityFlags) or the Confirm Active capability sets (bitmap / surface-commands / large-pointer / GFX caps). The mitm passes mstsc's Connect Initial through verbatim (only patching serverSelectedProtocol/clientRequestedProtocols); we build our own. Next: diff mstsc's caps vs what `protocol.js` builds.

NOTE: mstsc's GFX CAPS_ADVERTISE is RDP-bulk-compressed in c2s (61-byte high-entropy blob, no plaintext capset version constants found); ours is sent uncompressed. Both get CAPS_CONFIRM, so compression itself isn't the gate.

Parser findings (`/tmp/rdpmitm/parse_caps.py` — de-frames TPKT/X224/MCS, isolates drdynvc channel 1007, decodes DVC PDUs incl. v3 0x06/0x07, lists CREATE/CLOSE + GFX PDUs):
- Host streamed mstsc **2163 GFX DVC DATA PDUs** (vs our ~3 frames). mstsc replied with 527 DATA PDUs on GFX (the per-frame 0x0d+0x16 acks).
- **Host streams GFX to mstsc UNCOMPRESSED at the DVC layer** (all DATA/DATA_FIRST, compressed=False). The s2c GFX payloads are ZGFX blobs (descriptor 0xe0 single-segment); v3 compressed DVC (0x06/0x07) is NOT used for the GFX data stream — v3 mattered only for the capabilities handshake.
- Host offers mstsc a **`WebAuthN_Channel`** DVC (id 12/16) that we have not seen handled in our client — worth checking whether the host offers it to us and we reject it.
- drdynvc channelId is 1007 in this capture; GFX dynamic channelId is 7.

Related: [[gfx-dvc-v3-required]] (this host needs DVC v3; nested ZGFX), [[gfx-h264-rendering]], [[browser-rdp-feature]]. Parser for the dumps: `tools/rdpmitm` dumps are MCS-framed; a caps-diff parser must also handle the v3 RDP8_LITE-compressed DVC DATA we added.
