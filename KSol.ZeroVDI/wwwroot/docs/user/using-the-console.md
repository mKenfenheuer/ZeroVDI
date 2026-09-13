# Using the console

The console is the browser tab where your remote desktop appears. It behaves like a normal desktop:
click, type, and use it as you would any computer.

## Full screen

For the most room, put your browser into full-screen mode (**F11** on most browsers). The remote
desktop fills your whole screen. Press **F11** again to leave full screen.

## Keyboard

Most keys pass straight through to the remote desktop, including the arrow and navigation keys, the
Windows key, AltGr, the numeric keypad and the function row. A few combinations are special:

- **Ctrl+Alt+Del** never reaches the browser — every operating system claims it first. Use the
  keyboard button in the session toolbar to send it to the remote desktop (that is how you reach the
  Windows lock, change-password and Task Manager screen).
- Some browser shortcuts (like **Ctrl+W** to close the tab, or **F11** for full screen) are handled
  by your browser before they reach the desktop.
- If a shortcut isn't reaching the remote machine, try going full screen first.
- On a Mac, the **Command** key is sent as the remote **Windows** key, so Command+R opens the remote
  Run box. Use **Ctrl+C** and **Ctrl+V** for copy and paste inside the remote desktop, or the
  clipboard panel below to move text between the two machines.
- **Caps Lock, Num Lock and Scroll Lock** are kept in step with your real keyboard, including when
  you switch away from the session and toggle them elsewhere.

Your keyboard layout (for example German QWERTZ or French AZERTY) is detected from your browser and
passed to the remote desktop automatically, so keys produce the characters printed on them. If the
wrong characters appear, check your browser's language settings — the layout is derived from them on
browsers that can't report the physical keyboard directly.

## Clipboard (copy &amp; paste)

When clipboard sharing is enabled, text moves between your computer and the remote desktop through
the **clipboard panel** — click the clipboard button in the session toolbar to open it:

- **To the remote desktop:** type or paste text into the panel and click **Send to remote**, then
  paste inside the session as usual.
- **From the remote desktop:** copy text inside the session and it appears in the panel; click
  **Copy** to put it on your computer's clipboard. Your browser may ask for permission the first
  time — allow it.

Browsers don't let a web page read your clipboard silently, which is why the panel is the exchange
point rather than copy/paste happening invisibly in the background.

If the panel isn't available, check that **Clipboard** is turned on in
[Connection settings](connection-settings.md).

## Sound, microphone and camera

Audio from the remote desktop plays through your speakers when **Audio** is enabled. If you need the
remote desktop to hear your microphone or see your camera (for calls inside the session), enable
**Microphone** and **Camera** in [Connection settings](connection-settings.md). Your browser will
ask permission the first time.

## Connection quality

The signal bars in the session toolbar show the health of your **whole** connection — from your
browser, through the ZeroVDI gateway, to the remote desktop itself. Next to the bars you'll see the
current round-trip latency and throughput. Hover over the indicator to see the two legs separately:
**You ↔ gateway** (your internet link) and **Gateway ↔ desktop** (the datacenter path) — handy for
telling whether a sluggish session is caused by your own network or by the far side.

## Reconnecting

If your network drops briefly, ZeroVDI tries to reconnect automatically. If the screen goes black or
freezes, close the tab and click **Connect** again from the dashboard — your session is usually still
running where you left it.
