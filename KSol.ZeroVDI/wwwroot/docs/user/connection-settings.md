# Connection settings

Each desktop has a set of connection options that control what's shared between your local computer
and the remote desktop. You can adjust them before connecting.

## Opening the settings

On the **Your desktops** dashboard, click the **gear icon** on a desktop card to open its connection
settings. Your choices are remembered for next time.

> Pools don't have per-desktop settings here — they use the defaults your administrator configured.

You can also change the settings while connected: click the **gear icon** in the session bar at the
top of the screen, adjust the options and click **Apply & reconnect**. The desktop reconnects in a
moment and you stay signed in. Leave **Use these settings next time too** ticked to keep them.

## What each option does

| Option | What it controls |
|---|---|
| **Audio** | Plays sound from the remote desktop through your speakers. |
| **Clipboard** | Lets you copy and paste text between your computer and the remote desktop. In Chrome and Edge you can turn on automatic sync in the clipboard panel; otherwise use the panel to send and copy text. |
| **Microphone** | Makes your microphone available inside the session (e.g. for calls). |
| **Camera** | Makes your webcam available inside the session. |
| **Graphics mode** | How the screen is sent to your browser. Leave on the default unless you have a reason to change it. If your browser cannot decode H.264 video, ZeroVDI switches to RemoteFX Progressive by itself. |
| **HiDPI** | Renders at your screen's native pixel density (capped at 200 % and 4K). Sharper on Retina screens, but streams more data. |
| **Performance** | Trades visual extras (like wallpaper and animations) for speed on slower connections. |

Turning options **off** can make the connection faster and is more private — only enable what you
actually need.

## Permissions

The first time you use clipboard, microphone or camera, your browser asks permission. If you
accidentally deny it, click the padlock / site-info icon in your browser's address bar to re-allow
it, then reconnect.

## Saved sign-in

The settings panel is also where you can save the desktop's username and password so you're not asked
each time. See [Saving your credentials](saving-credentials.md).
