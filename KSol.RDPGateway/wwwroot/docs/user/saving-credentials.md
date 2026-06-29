# Saving your credentials

By default, each time you connect to a desktop you enter the desktop's username and password. To skip
that, you can **store** those credentials so ZeroVDI signs you in automatically.

## How to save them

1. On the **Your desktops** dashboard, click the **gear icon** on a desktop card.
2. Under **Stored VM credentials (single sign-on)**, enter the username and password for that desktop.
3. Save. The card now signs you in automatically the next time you connect.

When credentials are stored, you'll see a **Stored** badge and the console connects without prompting.

## Removing saved credentials

In the same settings panel, click **Clear stored credentials**. After that you'll be asked for the
username and password again at connect time.

## Is this safe?

Your stored credentials are kept on the server and are **never shown back to you or sent to your
browser** — ZeroVDI injects them into the connection on the server side at the moment you connect. If
you change the desktop's password, update the stored credentials here too, or clear them.

> These are the credentials *for the remote desktop*, which may differ from your ZeroVDI sign-in. To
> change how you sign in to ZeroVDI itself, see [Your account](your-account.md).
